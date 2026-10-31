// Browser bake driver: runs an INKWAVE module that needs a real browser (WebGL render targets, 2D canvas, web fonts) in
// headless system Chrome, driven over the DevTools protocol with Node's built-in WebSocket (no puppeteer).
//
//   node Tools/bake/browser.mjs <page> [query]          runs Tools/bake/pages/<page>.html?<query> until the page sets
//                                                       window.__bakeDone (window.__bakeError → exit 1)
//   node Tools/bake/browser.mjs --url <served path> --shot <png> [--wait <s>] [--size 1280x720]
//                                                       a reference screenshot of any served page
//
// A static server on 127.0.0.1 is rooted at the project folder (/reference/inkwave/…, /Tools/bake/pages/…). Pages save
// their output through it (page side: Tools/bake/pages/save.js):
//   POST /save?path=<rel>                         raw body → <project>/<rel>
//   POST /save-png?path=<rel>&w=<w>&h=<h>[&flip=1] raw RGBA8 bytes → PNG (encoded here: a canvas would premultiply alpha)
// Paths are relative to the project and must stay under Assets/_Game/ (no '..', no drive letters).
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import zlib from 'node:zlib';
import { spawn } from 'node:child_process';

const ROOT = path.resolve(import.meta.dirname, '..', '..');
const CHROME = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find((p) => fs.existsSync(p));
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json', '.css': 'text/css',
  '.png': 'image/png', '.jpg': 'image/jpeg', '.webp': 'image/webp', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.woff': 'font/woff',
  '.wasm': 'application/wasm', '.mp3': 'audio/mpeg', '.ogg': 'audio/ogg', '.wav': 'audio/wav', '.glb': 'model/gltf-binary', '.bin': 'application/octet-stream' };

// ---- args
const argv = process.argv.slice(2);
const opt = (k, d) => { const i = argv.indexOf(k); return i >= 0 ? argv[i + 1] : d; };
const shotMode = argv.includes('--shot');
const page = shotMode ? null : argv[0];
const query = shotMode ? '' : (argv[1] || '');
if (!shotMode && !page) { console.error('usage: node Tools/bake/browser.mjs <page> [query] | --url <path> --shot <png> [--wait s] [--size WxH]'); process.exit(2); }
const timeoutS = +opt('--timeout', 600);

// ---- PNG (RGBA8, filter 0)
function png(w, h, rgba, flip) {
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) {
    const sy = flip ? h - 1 - y : y;
    raw[y * (w * 4 + 1)] = 0;
    rgba.copy(raw, y * (w * 4 + 1) + 1, sy * w * 4, (sy + 1) * w * 4);
  }
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(td) >>> 0);
    return Buffer.concat([len, td, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}

// ---- static server + save endpoints
function safeOut(rel) {
  if (!rel || rel.includes('..') || /^[a-zA-Z]:/.test(rel) || rel.startsWith('/') || rel.startsWith('\\')) return null;
  const p = path.resolve(ROOT, rel);
  return p.startsWith(path.join(ROOT, 'Assets', '_Game') + path.sep) ? p : null;
}
let saved = 0;
const server = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://x');
  if (req.method === 'POST' && (u.pathname === '/save' || u.pathname === '/save-png')) {
    const out = safeOut(u.searchParams.get('path'));
    if (!out) { res.writeHead(400); res.end('bad path'); return; }
    const parts = [];
    req.on('data', (c) => parts.push(c));
    req.on('end', () => {
      let body = Buffer.concat(parts);
      if (u.pathname === '/save-png') body = png(+u.searchParams.get('w'), +u.searchParams.get('h'), body, u.searchParams.get('flip') === '1');
      fs.mkdirSync(path.dirname(out), { recursive: true });
      fs.writeFileSync(out, body);
      saved++;
      res.writeHead(200); res.end('ok');
    });
    return;
  }
  const file = path.resolve(ROOT, '.' + decodeURIComponent(u.pathname));
  if (!file.startsWith(ROOT + path.sep) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end('not found'); return; }
  res.writeHead(200, { 'Content-Type': TYPES[path.extname(file).toLowerCase()] || 'application/octet-stream', 'Cache-Control': 'no-store' });
  fs.createReadStream(file).pipe(res);
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const httpPort = server.address().port;

// ---- Chrome
if (!CHROME) { console.error('no Chrome / Edge found'); process.exit(2); }
const userDir = fs.mkdtempSync(path.join(os.tmpdir(), 'iw-bake-'));
const dbgPort = 9300 + Math.floor(Math.random() * 500);
const [W, H] = (opt('--size', '1280x720')).split('x').map(Number);
const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${dbgPort}`, `--user-data-dir=${userDir}`, '--no-first-run',
  '--no-default-browser-check', '--ignore-gpu-blocklist', '--enable-gpu', `--use-angle=${opt('--angle', 'default')}`, '--enable-unsafe-swiftshader',
  `--window-size=${W},${H}`, 'about:blank'], { stdio: 'ignore' });
let exiting = false;
function finish(code) {
  if (exiting) return; exiting = true;
  try { chrome.kill(); } catch { /* gone */ }
  server.close();
  setTimeout(() => { try { fs.rmSync(userDir, { recursive: true, force: true }); } catch { /* locked */ } process.exit(code); }, 400);
}
process.on('SIGINT', () => finish(130));

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let targets = null;
for (let i = 0; i < 60 && !targets; i++) {
  await sleep(250);
  try { const r = await fetch(`http://127.0.0.1:${dbgPort}/json/list`); targets = (await r.json()).filter((t) => t.type === 'page'); if (!targets.length) targets = null; } catch { /* not up yet */ }
}
if (!targets) { console.error('Chrome did not open its debugging port'); finish(2); }

// ---- CDP
const ws = new WebSocket(targets[0].webSocketDebuggerUrl);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
let nextId = 1; const pending = new Map();
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { const { r, j } = pending.get(m.id); pending.delete(m.id); m.error ? j(new Error(m.error.message)) : r(m.result); return; }
  if (m.method === 'Runtime.consoleAPICalled') console.log(`[page ${m.params.type}]`, m.params.args.map((a) => a.value ?? a.description ?? a.type).join(' '));
  else if (m.method === 'Runtime.exceptionThrown') console.error('[page exception]', m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text);
  else if (m.method === 'Log.entryAdded' && m.params.entry.level !== 'verbose') console.log(`[page log ${m.params.entry.level}]`, m.params.entry.text);
};
const send = (method, params = {}) => new Promise((r, j) => { const id = nextId++; pending.set(id, { r, j }); ws.send(JSON.stringify({ id, method, params })); });
await send('Runtime.enable'); await send('Page.enable'); await send('Log.enable');
await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: 1, mobile: false });

const base = `http://127.0.0.1:${httpPort}`;
if (shotMode) {
  const url = base + opt('--url');
  const shot = path.resolve(opt('--shot'));
  await send('Page.navigate', { url });
  await sleep(+opt('--wait', 5) * 1000);
  const { data } = await send('Page.captureScreenshot', { format: 'png' });
  fs.mkdirSync(path.dirname(shot), { recursive: true });
  fs.writeFileSync(shot, Buffer.from(data, 'base64'));
  console.log('shot', path.relative(ROOT, shot));
  finish(0);
} else {
  const url = `${base}/Tools/bake/pages/${page}.html${query ? '?' + query : ''}`;
  console.log('bake', url);
  await send('Page.navigate', { url });
  const t0 = Date.now();
  for (;;) {
    await sleep(500);
    const { result } = await send('Runtime.evaluate', { expression: 'window.__bakeError ? "err:" + window.__bakeError : window.__bakeDone ? "done" : ""', returnByValue: true });
    if (result.value === 'done') { console.log(`done: ${saved} files saved in ${((Date.now() - t0) / 1000).toFixed(1)} s`); finish(0); break; }
    if (result.value && result.value.startsWith('err:')) { console.error('bake failed:', result.value.slice(4)); finish(1); break; }
    if (Date.now() - t0 > timeoutS * 1000) { console.error(`timeout after ${timeoutS} s`); finish(1); break; }
  }
}
