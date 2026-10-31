// Stage exporter (?stage=<id>&time=day|dusk&quality=low|medium|high): builds a stage's dressing exactly as main.js does
// (_buildWorldNow: prop kit → level with the props' colliders → murals → decor; then the Environment with the stage's
// theme for that time, and _applyNight) and exports what Unity draws around the level:
//   Resources/Stages/<id>/geo/<n>.bytes   one IWS1 mesh per scene mesh, in its object's local space (time-independent)
//   Resources/Stages/<id>/tex/<n>.png     textures + canvases (".lin" = linear data, else sRGB)
//   Resources/Stages/<id>/<time>.json     the scene: objects (name, path, TRS, mesh, material, instances, flags),
//                                         materials (three type + parameters, custom chunk tags, ShaderMaterial uniforms),
//                                         textures, lights, fog, exposure, the environment's theme + uniforms + animation
//                                         specs, the mural table
//   Resources/Stages/<id>/<time>.sky.bytes  the sky (dome + clouds) as a 6-face RGBA half-float cubemap (+X −X +Y −Y +Z −Z,
//                                         three's cube camera conventions, faces as rendered); <time>.env.bytes the same for
//                                         the sky copy three's image-based light (PMREM) is made from (no sun disk)
// IWS1 = IWK1's layout (Tools/bake/iwk.mjs) with 32-bit indices when flags & 2, colour in the Color channel (UNorm8×4)
// and any extra attribute in the next free TexCoord (named in the json).
import * as THREE from 'three';
import { PropKit } from '/reference/inkwave/src/world/props.js';
import { dressingFor } from '/reference/inkwave/src/world/dressing.js';
import { inMode, layoutFor } from '/reference/inkwave/src/world/variants.js';
import { MAP_LAYOUTS } from '/reference/inkwave/src/world/maps.js';
import { MAPS, mapTheme } from '/reference/inkwave/src/config.js';
import { Level } from '/reference/inkwave/src/world/level.js';
import { Decor } from '/reference/inkwave/src/world/decor.js';
import { Environment } from '/reference/inkwave/src/world/environment.js';
import { createMuralTexture } from '/reference/inkwave/src/world/murals.js';
import { G } from '/reference/inkwave/src/core/ctx.js';
import { save, savePng, done, fail } from './save.js';

const P = new URLSearchParams(location.search);
const id = P.get('stage') || 'tidewater', time = P.get('time') === 'dusk' ? 'dusk' : 'day', quality = P.get('quality') || 'low';
const skySize = +(P.get('sky') || 256);
const OUT = `Assets/_Game/Resources/Stages/${id}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
// main.js _footprint
const footprint = (level) => level.blocks.filter((b) => (b.aligned || Math.abs(b.axes[1].y) > 0.9999) && b.aabbMax.y < 0.01 && b.aabbMax.y > -2.5 && b.aabbMin.y < -1)
  .map((b) => ({ minX: b.aabbMin.x, maxX: b.aabbMax.x, minZ: b.aabbMin.z, maxZ: b.aabbMax.z,
    ...(b.aligned ? {} : { cx: b.center.x, cz: b.center.z, hx: b.half.x, hz: b.half.z, ax: b.axes[0].x, az: b.axes[0].z }) }));

// ---- IWS1 writer
const VA = { Position: 0, Normal: 1, Color: 3, TexCoord0: 4 };
const F32 = 0, F16 = 1, UN8 = 2;
const _f = new Float32Array(1), _u = new Uint32Array(_f.buffer);
function toHalf(v) {
  _f[0] = v; const x = _u[0];
  const s = (x >>> 16) & 0x8000; let e = ((x >>> 23) & 0xff) - 127 + 15; let m = x & 0x7fffff;
  if (e >= 31) return s | 0x7c00;
  if (e <= 0) { if (e < -10) return s; m |= 0x800000; const sh = 14 - e; let h = m >> sh; if ((m >> (sh - 1)) & 1) h++; return s | h; }
  let h = s | (e << 10) | (m >> 13); if (m & 0x1000) h++; return h;
}
const comp = (a, i, d) => (d === 0 ? a.getX(i) : d === 1 ? a.getY(i) : d === 2 ? a.getZ(i) : a.getW(i));
function iws(g) {
  const A = g.attributes, n = A.position.count;
  const idx = g.index ? g.index.array : Uint32Array.from({ length: n }, (_, i) => i);
  const ch = [{ va: VA.Position, fmt: F32, dim: 3, a: A.position }];
  if (A.normal) ch.push({ va: VA.Normal, fmt: F16, dim: 4, a: A.normal });
  if (A.color) ch.push({ va: VA.Color, fmt: UN8, dim: 4, a: A.color });
  if (A.uv) ch.push({ va: VA.TexCoord0, fmt: F32, dim: 2, a: A.uv });
  const extra = [];
  let tc = 5;
  for (const k of Object.keys(A).sort()) {
    if (['position', 'normal', 'color', 'uv'].includes(k)) continue;
    if (tc > 11) { console.warn('iws: too many attributes, dropped', k); continue; }
    const a = A[k];
    ch.push({ va: tc, fmt: F32, dim: a.itemSize, a });
    extra.push({ name: k, channel: tc - 4, dim: a.itemSize });   // TexCoord<channel>
    tc++;
  }
  const size = (c) => c.dim * (c.fmt === F32 ? 4 : c.fmt === F16 ? 2 : 1);
  const stride = ch.reduce((s, c) => s + size(c), 0);
  const wide = n > 65535, ni = idx.length;
  const idxBytes = ((ni * (wide ? 4 : 2)) + 3) & ~3, head = 24 + ch.length * 4;
  const buf = new ArrayBuffer(head + n * stride + idxBytes), dv = new DataView(buf);
  [73, 87, 83, 49].forEach((b, i) => dv.setUint8(i, b));   // 'IWS1'
  dv.setUint32(4, n, true); dv.setUint32(8, ni, true); dv.setUint32(12, wide ? 2 : 0, true); dv.setUint32(16, stride, true); dv.setUint32(20, ch.length, true);
  ch.forEach((c, k) => { dv.setUint8(24 + k * 4, c.va); dv.setUint8(25 + k * 4, c.fmt); dv.setUint8(26 + k * 4, c.dim); });
  let o = head;
  for (let i = 0; i < n; i++) for (const c of ch) {
    for (let d = 0; d < c.dim; d++) {
      const v = d < c.a.itemSize ? comp(c.a, i, d) : (c.va === VA.Color ? 1 : 0);
      if (c.fmt === F32) { dv.setFloat32(o, v, true); o += 4; }
      else if (c.fmt === F16) { dv.setUint16(o, toHalf(v), true); o += 2; }
      else { dv.setUint8(o, Math.max(0, Math.min(255, Math.round(v * 255)))); o += 1; }
    }
  }
  for (let i = 0; i < ni; i++) { if (wide) { dv.setUint32(o, idx[i], true); o += 4; } else { dv.setUint16(o, idx[i], true); o += 2; } }
  return { buf, verts: n, tris: ni / 3, extra };
}

// ---- textures
const texs = new Map();   // uuid → record
let texN = 0;
async function texRef(t) {
  if (!t) return null;
  if (texs.has(t.uuid)) return texs.get(t.uuid).file;
  const lin = t.colorSpace !== THREE.SRGBColorSpace;
  const rec = { file: `t${texN++}${lin ? '.lin' : ''}`, name: t.name || '', wrapS: t.wrapS, wrapT: t.wrapT, repeat: t.repeat.toArray(), offset: t.offset.toArray(), flipY: t.flipY, srgb: !lin };
  texs.set(t.uuid, rec);
  const img = t.image;
  let w, h, rgba, flip = false;
  if (t.isRenderTargetTexture || !img) { rec.file = null; rec.skipped = 'render target'; return null; }
  if (img.data) {
    if (!(img.data instanceof Uint8Array || img.data instanceof Uint8ClampedArray)) { rec.file = null; rec.skipped = 'data type ' + img.data.constructor.name; return null; }
    w = img.width; h = img.height; rgba = new Uint8Array(w * h * 4);
    const ch = img.data.length / (w * h);
    for (let i = 0; i < w * h; i++) for (let k = 0; k < 4; k++) rgba[i * 4 + k] = k < ch ? img.data[i * ch + k] : (k === 3 ? 255 : img.data[i * ch]);
    flip = !t.flipY;   // DataTexture row 0 = v 0 → bottom of the PNG
  } else {
    w = img.width; h = img.height;
    const cv = document.createElement('canvas'); cv.width = w; cv.height = h;
    const g = cv.getContext('2d'); g.drawImage(img, 0, 0);
    rgba = new Uint8Array(g.getImageData(0, 0, w, h).data.buffer);
    flip = !t.flipY;   // a canvas with flipY (three's default) already has v 1 at the top row
  }
  await savePng(`${OUT}/tex/${rec.file}.png`, w, h, rgba, flip);
  rec.size = [w, h];
  return rec.file;
}

// ---- values
const num = (v) => (typeof v === 'number' ? +v.toFixed(6) : v);
function val(v) {
  if (v == null) return v;
  if (typeof v === 'number' || typeof v === 'boolean' || typeof v === 'string') return num(v);
  if (v.isColor) return [num(v.r), num(v.g), num(v.b)];
  if (v.isVector2 || v.isVector3 || v.isVector4 || v.isQuaternion || v.isMatrix3 || v.isMatrix4) return v.toArray().map(num);
  if (Array.isArray(v)) return v.map(val);
  return undefined;
}
async function uniformsOf(U) {
  const o = {};
  for (const k in U) {
    const v = U[k] && 'value' in U[k] ? U[k].value : U[k];
    if (v && v.isTexture) o[k] = { tex: await texRef(v) };
    else { const x = val(v); if (x !== undefined) o[k] = x; }
  }
  return o;
}

// ---- materials
const mats = new Map();
const defaultOBC = THREE.Material.prototype.onBeforeCompile;
async function matRef(m) {
  if (mats.has(m.uuid)) return mats.get(m.uuid).id;
  const d = { id: mats.size, type: m.type, name: m.name || '', side: m.side, transparent: m.transparent, opacity: num(m.opacity), alphaTest: num(m.alphaTest),
    depthWrite: m.depthWrite, depthTest: m.depthTest, vertexColors: m.vertexColors, toneMapped: m.toneMapped, fog: m.fog, blending: m.blending,
    alphaToCoverage: !!m.alphaToCoverage, polygonOffset: m.polygonOffset ? [m.polygonOffsetFactor, m.polygonOffsetUnits] : null, flatShading: !!m.flatShading };
  mats.set(m.uuid, d);
  for (const k of ['color', 'emissive', 'specularColor', 'sheenColor']) if (m[k]) d[k] = val(m[k]);
  for (const k of ['roughness', 'metalness', 'clearcoat', 'clearcoatRoughness', 'emissiveIntensity', 'envMapIntensity', 'transmission', 'ior', 'sheen', 'specularIntensity'])
    if (typeof m[k] === 'number') d[k] = num(m[k]);
  for (const k of ['map', 'emissiveMap', 'alphaMap', 'normalMap', 'roughnessMap', 'aoMap']) if (m[k]) d[k] = await texRef(m[k]);
  if (m.onBeforeCompile !== defaultOBC) d.custom = (m.customProgramCacheKey && m.customProgramCacheKey !== THREE.Material.prototype.customProgramCacheKey && m.customProgramCacheKey()) || 'custom';
  if (m.defines && Object.keys(m.defines).length) d.defineList = Object.keys(m.defines);
  if (m.isShaderMaterial) { d.uniforms = await uniformsOf(m.uniforms); d.lights = m.lights; }
  return d.id;
}

// ---- sky cubemaps into half-float cube targets, from the origin: the visible dome (+ its cloud layer) alone, and the
// environment's ENV_PASS copy that three's PMREM image-based light is made from (env._envScene)
function bakeSky(renderer, scene, env) {
  const hidden = [];
  scene.traverse((o) => { if ((o.isMesh || o.isPoints || o.isLine || o.isSprite) && o.visible && !isSky(o, env)) { o.visible = false; hidden.push(o); } });
  const fog = scene.fog; scene.fog = null;
  env.sky.position.set(0, 0, 0); env.sky.updateMatrixWorld(true);
  const out = bakeCube(renderer, scene);
  scene.fog = fog;
  for (const o of hidden) o.visible = true;
  return out;
}
function bakeCube(renderer, scene) {
  const rt = new THREE.WebGLCubeRenderTarget(skySize, { type: THREE.HalfFloatType, generateMipmaps: false });
  const cc = new THREE.CubeCamera(0.1, 4000, rt);
  const tm = renderer.toneMapping; renderer.toneMapping = THREE.NoToneMapping;
  cc.update(renderer, scene);
  renderer.toneMapping = tm;
  const N = skySize * skySize * 4, out = new Uint16Array(N * 6), tmp = new Uint16Array(N);
  for (let f = 0; f < 6; f++) { renderer.readRenderTargetPixels(rt, 0, 0, skySize, skySize, tmp, f); out.set(tmp, f * N); }
  rt.dispose();
  return out.buffer;
}
const isSky = (o, env) => { for (let p = o; p; p = p.parent) if (p === env.sky) return true; return false; };

try {
  for (const [fam, file, opt] of [['Titan One', 'TitanOne-latin.woff2', {}], ['Rubik', 'Rubik-latin.woff2', { weight: '400 900' }]]) {
    const ff = new FontFace(fam, `url(/reference/inkwave/assets/fonts/${file})`, opt); document.fonts.add(await ff.load());
  }
  const canvas = document.createElement('canvas'); canvas.width = 64; canvas.height = 64;
  const renderer = new THREE.WebGLRenderer({ canvas });
  renderer.toneMapping = THREE.NeutralToneMapping; renderer.toneMappingExposure = 0.94;
  G.renderer = renderer;
  G.settings = { quality };
  const scene = new THREE.Scene(); G.scene = scene;
  const murals = await createMuralTexture();
  const kit = new PropKit(scene, { castShadow: true, quality });
  const colliders = [];
  for (const it of dressingFor(id)) { if (!inMode(it, 'turf')) continue; const r = kit.add(it.type, it); if (r && r.colliders) colliders.push(...r.colliders); }
  kit.build();
  const level = (G.level = new Level(layoutFor(MAP_LAYOUTS[id], 'turf'), colliders));
  murals.userData.setStage?.(id);
  const decor = new Decor(scene, level);
  const map = MAPS.find((m) => m.id === id);
  const theme = mapTheme(map, time);
  const env = (G.env = new Environment(renderer, scene, { bounds: level.bounds, theme, shadowSize: 1024, footprint: footprint(level), forceDeckField: quality === 'low' }));
  if (env.envMap) scene.environment = env.envMap;
  // main.js _applyNight
  const night = env.getSkyColors?.()?.night ?? 0;
  kit.setNight?.(night); decor.setNight?.(night);
  if (decor.bulbMat) decor.bulbMat.emissiveIntensity *= 1 + 3.2 * night;
  await document.fonts.ready; await sleep(300);   // the prop atlas redraws once its fonts load
  env.update(0, null);
  scene.updateMatrixWorld(true);
  console.log(`${id} ${time}: theme ${theme}, night ${night}`);

  // ---- objects
  const objects = [], geos = new Map(), geoAttrs = [];
  let tris = 0, bytes = 0;
  const path = (o) => { const a = []; for (let p = o; p && p !== scene; p = p.parent) a.unshift(p.name || p.type); return a.join('/'); };
  const visibleChain = (o) => { for (let p = o; p; p = p.parent) if (!p.visible) return false; return true; };
  const list = [];
  scene.traverse((o) => { if (o.isMesh || o.isPoints || o.isLine) list.push(o); });
  for (const o of list) {
    if (!o.isMesh) { console.warn('skipped', o.type, path(o)); continue; }
    if (isSky(o, env)) continue;   // baked into the sky cubemap
    let gid = geos.get(o.geometry.uuid);
    if (gid == null) {
      const r = iws(o.geometry);
      gid = geoAttrs.length; geos.set(o.geometry.uuid, gid);
      await save(`${OUT}/geo/g${gid}.bytes`, r.buf);
      bytes += r.buf.byteLength;
      geoAttrs.push(r.extra);
    }
    const p = new THREE.Vector3(), q = new THREE.Quaternion(), s = new THREE.Vector3();
    o.matrixWorld.decompose(p, q, s);
    const ms = Array.isArray(o.material) ? o.material : [o.material];
    const rec = { name: o.name || '', path: path(o), geo: `g${gid}`, attrs: geoAttrs[gid], mat: [], pos: val(p), rot: val(q), scale: val(s),
      visible: visibleChain(o), castShadow: o.castShadow, receiveShadow: o.receiveShadow, renderOrder: o.renderOrder, frustumCulled: o.frustumCulled };
    for (const m of ms) rec.mat.push(await matRef(m));
    if (o.geometry.groups && o.geometry.groups.length > 1) rec.groups = o.geometry.groups.flatMap((g) => [g.start, g.count, g.materialIndex]);
    const n = (o.geometry.index ? o.geometry.index.count : o.geometry.attributes.position.count) / 3;
    if (o.isInstancedMesh) {
      const M = new THREE.Matrix4(), W = new THREE.Matrix4();
      rec.instN = o.count; rec.inst = [];   // world matrices, 16 floats each (three column-major)
      for (let i = 0; i < o.count; i++) { o.getMatrixAt(i, M); W.multiplyMatrices(o.matrixWorld, M); rec.inst.push(...W.toArray().map(num)); }
      if (o.instanceColor) rec.instanceColors = Array.from(o.instanceColor.array, num);
      tris += n * o.count;
    } else tris += n;
    if (o.customDepthMaterial) rec.depthMat = await matRef(o.customDepthMaterial);
    objects.push(rec);
  }

  // ---- lights, fog, theme
  const lights = [];
  scene.traverse((o) => {
    if (!o.isLight) return;
    const L = { type: o.type, name: o.name, color: val(o.color), intensity: num(o.intensity), visible: o.visible };
    if (o.isDirectionalLight) { L.position = val(o.getWorldPosition(new THREE.Vector3())); L.target = val(o.target.getWorldPosition(new THREE.Vector3())); L.castShadow = o.castShadow; }
    if (o.isHemisphereLight) L.groundColor = val(o.groundColor);
    lights.push(L);
  });
  const T = Environment.THEMES[theme];
  const sky = bakeSky(renderer, scene, env);
  await save(`${OUT}/${time}.sky.bytes`, sky);
  if (env._envScene) await save(`${OUT}/${time}.env.bytes`, bakeCube(renderer, env._envScene));
  const pick = (a, keys) => (a ? a.map((x) => Object.fromEntries(keys.filter((k) => k in x).map((k) => [k, val(x[k])]))) : []);
  const anim = {
    sailboats: pick(env.sailboats, ['a', 'w', 'd', 'phase', 's']),
    buoys: pick(env.buoys, ['x', 'z', 'phase', 's']),
    gulls: pick(env.gulls, ['a0', 'w', 'cx', 'cz', 'r', 'h', 's']),
    moored: (env.moored || []).map((m) => ({ name: m.mesh?.name, phase: num(m.phase), roll: num(m.roll), x: num(m.spec?.x), z: num(m.spec?.z) })),
  };
  const out = {
    id, time, theme, quality, night: num(night),
    exposure: renderer.toneMappingExposure, toneMapping: 'neutral', grade: val(env.grade) ?? env.grade ?? null,
    environmentIntensity: num(scene.environmentIntensity ?? 1),
    fog: scene.fog ? { color: val(scene.fog.color), near: num(scene.fog.near), far: num(scene.fog.far) } : null,
    background: scene.background && scene.background.isColor ? val(scene.background) : null,
    lights, themeDef: JSON.parse(JSON.stringify(T, (k, v) => (typeof v === 'number' ? num(v) : v))), envUniforms: await uniformsOf(env.U), anim,
    sky: { file: `${time}.sky`, size: skySize, format: 'rgba16f', faces: ['+x', '-x', '+y', '-y', '+z', '-z'] },
    murals: { tex: await texRef(murals), table: murals.userData.murals ?? null },
    materials: [...mats.values()], textures: [...texs.values()], objects,
  };
  await save(`${OUT}/${time}.json`, JSON.stringify(out));
  console.log(`exported ${objects.length} objects, ${geoAttrs.length} meshes (${(bytes / 1048576).toFixed(1)} MB), ${mats.size} materials, ${texs.size} textures, ${tris | 0} tris, sky ${skySize}²`);
  done();
} catch (e) { fail(e); }
