// Page side of Tools/bake/browser.mjs: save output through the bake server, and signal the driver.
export async function save(rel, data) {
  const body = typeof data === 'string' ? new TextEncoder().encode(data) : data;
  const r = await fetch(`/save?path=${encodeURIComponent(rel)}`, { method: 'POST', body });
  if (!r.ok) throw new Error(`save ${rel}: ${r.status}`);
}
/** raw RGBA8 rows (top row first unless flip) → PNG, encoded by the driver (a canvas would premultiply alpha) */
export async function savePng(rel, w, h, rgba, flip = false) {
  const r = await fetch(`/save-png?path=${encodeURIComponent(rel)}&w=${w}&h=${h}&flip=${flip ? 1 : 0}`, { method: 'POST', body: rgba });
  if (!r.ok) throw new Error(`save ${rel}: ${r.status}`);
}
export function done() { window.__bakeDone = true; }
export function fail(e) { console.error(e && e.stack || e); window.__bakeError = String(e && e.message || e); }
