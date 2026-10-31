// Bakes the held weapon and sub-weapon prop models by running INKWAVE's src/game/character-weapons.js under Node.
// Output: Assets/_Game/Resources/KidWeapons/
//   weapons.json                         per kind: grips (handR / handL / inHand / inHandL), muzzle, flags, part pivots
//   <kind>/{bodyStatic,inkStatic,body,ink,bodyFar,inkFar,glow,drum,drumCaps,spin,part_<name>}.bytes   (IWK1, see iwk.mjs)
//   sub_<kind>/{body,ink,spin,spinInk,glow}.bytes
//
// usage: node --import ./Tools/bake/register.mjs Tools/bake/weapons.mjs
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { writeMesh } from './iwk.mjs';

const ROOT = path.resolve(import.meta.dirname, '..', '..');
const REF = path.resolve(process.env.INKWAVE_REF || path.join(ROOT, 'reference', 'inkwave'));
const OUT = path.join(ROOT, 'Assets', '_Game', 'Resources', 'KidWeapons');
const imp = (p) => import(pathToFileURL(path.join(REF, 'src', p)).href);
const { G } = await imp('core/ctx.js');
G.settings = { quality: 'low' };
const W = await imp('game/character-weapons.js');
const { farGeometry } = await imp('game/character-lod.js');
const THREE = await import('three');
// kit weapons bring their own model module (src/game/kits/<kind>-model.js); kits/<kind>.js registers it at import —
// the kit's game logic is not loaded here, only the model
const BM = await imp('game/kits/brolly-model.js');
W.registerWeaponModel('brolly', BM.brollyDef, BM.animateBrolly);
await imp('game/kits/bow-model.js');   // registers 'bow' itself
const BLM = await imp('game/kits/blade-model.js');
W.registerWeaponModel('blade', BLM.buildBlade, BLM.animateBlade);
await imp('game/kits/mitts-model.js');   // registers 'mitts' itself
// kit subs: the model builder lives in the kit module itself (registerSubModel at import). These load cleanly under Node
// (their bot / audio / icon imports only register tables), so they are imported whole; SUB_KINDS then lists them.
for (const k of ['shaker', 'waddle', 'torpedo', 'tracer', 'boomerang']) await imp(`game/kits/${k}.js`);

const v3 = (v) => (v ? [+v.x.toFixed(6), +v.y.toFixed(6), +v.z.toFixed(6)] : null);
const q4 = (q) => (q ? [+q.x.toFixed(7), +q.y.toFixed(7), +q.z.toFixed(7), +q.w.toFixed(7)] : null);
const frame = (f) => (f ? { pos: v3(f.pos), quat: q4(f.quat) } : null);
const hex = (c) => (c == null ? null : typeof c === 'string' ? c : '#' + c.toString(16).padStart(6, '0'));

fs.rmSync(OUT, { recursive: true, force: true });
const man = { version: 1, weapons: [], subs: [] };
let bytes = 0, count = 0;
const put = (dir, name, g) => { if (!g) return false; bytes += writeMesh(path.join(OUT, dir, name + '.bytes'), g, false).bytes; count++; return true; };

for (const kind of [...W.WEAPON_KINDS, ...W.SPECIAL_WEAPON_KINDS]) {
  const d = W.getWeaponDef(kind);
  put(kind, 'bodyStatic', d.bodyStatic); put(kind, 'inkStatic', d.inkStatic);
  put(kind, 'body', d.body); put(kind, 'ink', d.ink);
  // far tier: the merged weapon decimated like the body (character.js _animWeapon: 700 / 300 triangles)
  put(kind, 'bodyFar', farGeometry(d.body, 700)); put(kind, 'inkFar', farGeometry(d.ink, 300));
  const rec = {
    kind, dual: !!d.dual, mirrorDual: !!d.mirrorDual, drawLen: d.drawLen || 0, drumR: d.drumR || 0,
    muzzle: v3(d.muzzle), handR: frame(d.handR), handL: frame(d.handL), inHand: frame(d.inHand), inHandL: frame(d.inHandL),
    glow: put(kind, 'glow', d.glow), drum: put(kind, 'drum', d.drum), drumCaps: put(kind, 'drumCaps', d.drumCaps), drumAt: v3(d.drumAt),
    spin: put(kind, 'spin', d.spin), spinAt: v3(d.spinAt), parts: [],
  };
  for (const name in d.parts || {}) {
    const p = d.parts[name];
    put(kind, 'part_' + name, p.geo);
    rec.parts.push({
      name, mat: p.mat, pivot: v3(p.pivot),
      lamp: p.lamp ? { color: hex(p.lamp.color), emissive: hex(p.lamp.emissive ?? p.lamp.color), intensity: p.lamp.intensity ?? 1, roughness: p.lamp.roughness ?? 0.25 } : null,
    });
  }
  man.weapons.push(rec);
}
// Canopy Brolly extras (Game/Kits/BrollyModel.cs). The canopy gores are authored furled: part geo = R·F0·goreLocal
// (crown-relative). animateBrolly re-poses each gore as T(crown)·R·Rx(a)·diag(sx, s, s)·goreLocal — a rotation × scale,
// so Unity can pose it with a plain Transform once the part holds the gore-local geometry: undo R·F0 here. The damage
// tears ride their gore the same way. FOLD_* / NG / goreRot mirror brolly-model.js.
{
  const NG = 8, FOLD_A = -1.05, FOLD_SX = 0.19, FOLD_S = 0.37;
  const goreRot = (k) => new THREE.Matrix4().makeRotationZ(-k * (Math.PI * 2 / NG));
  const fold = (a, sx, sy, sz) => { const c = Math.cos(a), s = Math.sin(a); return new THREE.Matrix4().set(sx, 0, 0, 0, 0, c * sy, -s * sz, 0, 0, s * sy, c * sz, 0, 0, 0, 0, 1); };
  const FOLD0 = (k) => fold(FOLD_A * (k & 1 ? 1 : 0.965), FOLD_SX * (k & 1 ? 1 : 1.08), FOLD_S, FOLD_S);
  const toLocal = (g, k) => g.clone().applyMatrix4(goreRot(k).multiply(FOLD0(k)).invert());
  const d = W.getWeaponDef('brolly'), rec = man.weapons.find((r) => r.kind === 'brolly');
  for (let k = 0; k < NG; k++) put('brolly', 'part_g' + k, toLocal(d.parts['g' + k].geo, k));
  const tears = [];
  (d.tears || []).forEach((list, stage) => list.forEach((t, i) => { put('brolly', `tear${stage}_${i}`, toLocal(t.geo, t.gore)); tears.push({ stage, gore: t.gore, name: `tear${stage}_${i}` }); }));
  // the stretcher strut: a unit cylinder up +Y (re-aimed per frame); the open canopy (launched wall), canopy space
  const { Parts, C, M } = W.GEO_KIT;
  const { lathe } = await imp('game/character-geo.js');
  const SP = new Parts(); SP.add(lathe([[0, 0], [1, 0], [1, 1], [0, 1]], 6), C.gunmetal, M.metal);
  put('brolly', 'strut', SP.build());
  const open = BM.canopyOpenGeo();
  put('brolly', 'canopyInk', open.ink); put('brolly', 'canopyCream', open.cream); put('brolly', 'canopyHub', open.hub);
  man.brolly = { tears, ay: BM.AY, crownZ: BM.CROWN_Z, rimR: BM.RIM_R, depth: BM.DEPTH, ng: BM.NG };
}
// Tideline Bow: the flying / lodged arrow (bow.js arrowGeometry, a module-private generator — copied here verbatim, as
// bow.js itself pulls in the bot brain and audio): tip at the origin, the shaft runs back along -Z. ink = shaft + ink-bulb
// head (team gloss), fletch = vanes + nock (vertex colour). Game/Kits/Bow.cs draws them.
{
  const LEN = 0.62, _c = new THREE.Color();
  const lz = (prof, seg) => { const g = new THREE.LatheGeometry(prof.map(([r, z]) => new THREE.Vector2(r, z)), seg); g.rotateX(Math.PI / 2); return g; };
  const shaft = lz([[0, -LEN], [0.015, -LEN + 0.005], [0.015, -0.08], [0, -0.07]], 8);
  const hp = new THREE.SplineCurve([[0, -0.12], [0.018, -0.11], [0.034, -0.07], [0.028, -0.032], [0.012, -0.007], [0, 0]].map(([r, z]) => new THREE.Vector2(r, z))).getSpacedPoints(14);
  const head = lz(hp.map((p) => [Math.max(0, p.x), p.y]), 12);
  const parts = [], cols = [];
  const vane = (roll) => {
    const g = new THREE.BoxGeometry(0.003, 0.036, 0.1);
    const pz = g.attributes.position;
    for (let k = 0; k < pz.count; k++) { const y = pz.getY(k), z = pz.getZ(k); pz.setY(k, y > 0 ? y - (z + 0.0425) * 0.12 : y); }
    g.translate(0, 0.032, -LEN + 0.08); g.rotateZ(roll); return g;
  };
  for (let k = 0; k < 3; k++) { parts.push(vane(k * Math.PI * 2 / 3 + 0.4)); cols.push(k ? 0xf4f6fa : 0xffcf33); }
  parts.push(lz([[0, -LEN - 0.022], [0.017, -LEN - 0.02], [0.017, -LEN + 0.014], [0, -LEN + 0.014]], 8)); cols.push(0x1b1e25);
  // (indexed + uv-free for the IWK writer; colours are three's linear working colours, like the vertex colours of every
  // other baked mesh)
  const clean = (g) => { g.deleteAttribute('uv'); return g.index ? g : g.setIndex([...Array(g.attributes.position.count).keys()]); };
  for (let i = 0; i < parts.length; i++) {
    const g = parts[i], n = g.attributes.position.count, a = new Float32Array(n * 3); _c.set(cols[i]);
    for (let k = 0; k < n; k++) { a[k * 3] = _c.r; a[k * 3 + 1] = _c.g; a[k * 3 + 2] = _c.b; }
    g.setAttribute('color', new THREE.BufferAttribute(a, 3));
  }
  const { mergeGeometries } = await import('three/addons/utils/BufferGeometryUtils.js');
  put('bow', 'arrowInk', mergeGeometries([shaft, head].map(clean), false));
  put('bow', 'arrowFletch', mergeGeometries(parts.map(clean), false));
}
// Brine Cutlass: the ink edge (aSeg 0 guard → 1 tip: the charge fills it with light) and the aura shell (the blade body,
// inflated in the shader), both in weapon space (Game/Kits/BladeModel.cs)
{
  const d = W.getWeaponDef('blade');
  put('blade', 'edge', d.bladeEdge); put('blade', 'aura', d.bladeAura);
}
// Sponge Mitts: the flying ink fist (mitts.js fistGeometry, module-private — copied verbatim): a glove-shaped blob flying
// knuckles-first along +Z with a tapering ink tail (Game/Kits/Mitts.cs draws it)
{
  const { superEllipsoid } = await imp('game/character-geo.js');
  const { mergeGeometries } = await import('three/addons/utils/BufferGeometryUtils.js');
  const parts = [];
  parts.push(superEllipsoid(0.105, 0.092, 0.09, 0.72, 0.68, 22, 16, (q) => { if (q.z > 0.03) q.z = 0.03 + (q.z - 0.03) * 0.72; }));
  for (let i = 0; i < 4; i++) {
    const x = -0.069 + i * 0.046;
    parts.push(superEllipsoid(0.027, 0.029, 0.022, 0.8, 0.8, 10, 8).translate(x, 0.03 - Math.abs(x) * 0.12, 0.078 - Math.abs(x) * 0.12));
  }
  parts.push(superEllipsoid(0.055, 0.022, 0.024, 0.8, 0.8, 12, 8).rotateZ(-0.18).translate(0.02, -0.04, 0.074));
  parts.push(superEllipsoid(0.07, 0.062, 0.1, 0.9, 0.9, 16, 12, (q) => { if (q.z < 0) { const t = -q.z / 0.1; q.x *= 1 - 0.55 * t * t; q.y *= 1 - 0.55 * t * t; } }).translate(0, 0, -0.07));
  parts.push(superEllipsoid(0.036, 0.034, 0.042, 1, 1, 10, 8).translate(0, 0.004, -0.2));
  parts.push(superEllipsoid(0.02, 0.02, 0.024, 1, 1, 8, 6).translate(0, 0.008, -0.27));
  for (const g of parts) { if (g.attributes.uv) g.deleteAttribute('uv'); }
  put('mitts', 'fist', mergeGeometries(parts, false));
}
// Shaker Bomb: the charge lamp dome (shaker.js LAMP_GEO, module-private — copied verbatim), one per lamp socket
{
  const { superEllipsoid } = await imp('game/character-geo.js');
  put('sub_shaker', 'lamp', superEllipsoid(0.0057, 0.0057, 0.0034, 1, 1, 12, 6));
}
// Waddle Bomb: the walking bomb's world parts (waddle.js worldGeo, exposed by hooks.mjs): body without the lamp, ink,
// the two legs re-centred on their hips (they swing), the beacon lamp (Game/Kits/Waddle.cs)
{
  const { worldGeo } = await imp('game/kits/waddle.js');
  const g = worldGeo();
  put('sub_waddle', 'worldBody', g.body); put('sub_waddle', 'worldInk', g.ink);
  put('sub_waddle', 'legR', g.legs[0]); put('sub_waddle', 'legL', g.legs[1]); put('sub_waddle', 'lamp', g.lamp);
}
// Tide Torpedo: the world rig's own parts (kits/torpedo.js buildRig): the hull (unmerged body + ink), one fin
// re-centred on its hinge (the rig swings four copies), and the propeller re-centred on PROP_AT (Game/Kits/Torpedo.cs)
{
  const d = W.getSubDef('torpedo');
  put('sub_torpedo', 'hullBody', d.hullBody); put('sub_torpedo', 'hullInk', d.hullInk);
  put('sub_torpedo', 'finLocal', d.finLocal); put('sub_torpedo', 'prop', d.prop);
}
for (const kind of W.SUB_KINDS) {
  const d = W.getSubDef(kind);
  const dir = 'sub_' + kind;
  put(dir, 'body', d.body); put(dir, 'ink', d.ink);
  man.subs.push({
    kind, handL: frame(d.handL), inHandL: frame(d.inHandL),
    spin: put(dir, 'spin', d.spin), spinInk: put(dir, 'spinInk', d.spinInk), spinAt: v3(d.spinAt), glow: put(dir, 'glow', d.glow),
  });
}
fs.writeFileSync(path.join(OUT, 'weapons.json'), JSON.stringify(man));
console.log(`baked ${man.weapons.length} weapons + ${man.subs.length} subs: ${count} meshes, ${(bytes / 1048576).toFixed(2)} MB → ${path.relative(ROOT, OUT)}`);
