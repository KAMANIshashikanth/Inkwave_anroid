// Bakes the squidkid meshes by running INKWAVE's own generators (src/game/character-geo.js and friends) under Node, so
// the Unity port draws exactly the geometry the web game builds at load time. Output: Assets/_Game/Resources/Kid/
//   kid.json                  rig (bone names, parents, rest positions), tank placement, per-hair rest/strand meta
//   <tier>/<part>.bytes       one mesh per file (IWK1 layout below); tiers 'game' and 'far' (+ 'hero' when asked)
//
// usage: node --import ./Tools/bake/register.mjs Tools/bake/character.mjs [--quality low] [--tiers game,far]
//
// Hair meshes are baked per (hair style, hat). The brow strokes live in the same JS mesh but do not depend on the style
// or hat, so they are split off by bone (browL/browR) and baked once per brow kind; the game joins the two at load.
//
// IWK1 (little endian):
//   'IWK1' · u32 vertexCount · u32 indexCount · u32 flags (1 = skinned) · u32 stride · u32 attrCount
//   attrCount × (u8 VertexAttribute, u8 VertexAttributeFormat, u8 dimension, u8 0)     (Unity enum values)
//   vertexCount × stride interleaved vertex data
//   skinned: vertexCount × 4 u8 bone indices, then vertexCount × 4 u8 weights (/255, sorted descending)
//   indexCount × u16 (padded to 4 bytes)
// Channels: Position f32×3 · Normal f16×4 · TexCoord0 = uv f32×2 · TexCoord1 = color f16×4 · TexCoord2 = aEx|aTint|aMat f32
//           TexCoord3 = (aHead|aCloth|aHair|aEyeS|aSq|aSeg).xyz + aOcc f16×4 · TexCoord4 = aFace f16×4
//           TexCoord5 = rest position f32×3 (skinned meshes only)
import fs from 'node:fs';
import { writeMesh } from './iwk.mjs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const ROOT = path.resolve(import.meta.dirname, '..', '..');
const REF = path.resolve(process.env.INKWAVE_REF || path.join(ROOT, 'reference', 'inkwave'));
const OUT = path.join(ROOT, 'Assets', '_Game', 'Resources', 'Kid');
const arg = (name, def) => { const i = process.argv.indexOf('--' + name); return i > 0 ? process.argv[i + 1] : def; };
const QUALITY = arg('quality', 'low');
const TIERS = arg('tiers', 'game,far').split(',');

const imp = (p) => import(pathToFileURL(path.join(REF, 'src', p)).href);
const { G } = await imp('core/ctx.js');
G.settings = { quality: QUALITY };
const GEO = await imp('game/character-geo.js');
const { farGeometry } = await imp('game/character-lod.js');
const STYLE = await imp('game/character-style.js');

// FAR_TRIS in character.js: far-tier triangle targets, applied when the far builder hands back more than 1.5×
const FAR_TRIS = { skin: 2600, cloth: 3800, hair: 2000, eyes: 260 };
const farPart = (key, g) => { const t = FAR_TRIS[key] ?? 500; return g.index.count / 3 > t * 1.5 ? farGeometry(g, t) : g; };


// Split a hair mesh into (hair without brows, brows only) by the brow bones.
const BROW_BONES = new Set([GEO.BONE_INDEX.browL, GEO.BONE_INDEX.browR]);
function splitBrows(g) {
  const SI = g.attributes.skinIndex, idx = g.index.array;
  const isBrow = (v) => BROW_BONES.has(SI.getX(v));
  const keep = [[], []];
  for (let t = 0; t < idx.length; t += 3) keep[isBrow(idx[t]) ? 1 : 0].push(idx[t], idx[t + 1], idx[t + 2]);
  return keep.map((tris) => subset(g, tris));
}
function subset(g, tris) {
  const map = new Map(), order = [];
  for (const v of tris) if (!map.has(v)) { map.set(v, order.length); order.push(v); }
  const out = { attributes: {}, index: { array: Uint16Array.from(tris, (v) => map.get(v)), count: tris.length } };
  for (const k in g.attributes) {
    const a = g.attributes[k], s = a.itemSize, arr = new a.array.constructor(order.length * s);
    order.forEach((v, i) => { for (let d = 0; d < s; d++) arr[i * s + d] = a.getComponent(v, d); });
    out.attributes[k] = wrapAttr(arr, s);
  }
  return out;
}
function wrapAttr(arr, s) {
  return {
    array: arr, itemSize: s, count: arr.length / s,
    getX: (i) => arr[i * s], getY: (i) => arr[i * s + 1], getZ: (i) => arr[i * s + 2], getW: (i) => arr[i * s + 3],
    getComponent: (i, d) => arr[i * s + d],
  };
}

// ---- bake --------------------------------------------------------------------------------------------------------
const v3a = (v) => [+v.x.toFixed(6), +v.y.toFixed(6), +v.z.toFixed(6)];
const manifest = { version: 1, quality: QUALITY, tiers: TIERS, bones: [], tank: null, hairs: [], meshes: [] };
const rest0 = GEO.getRestPositions({ hair: 0, hat: 0, brows: 0 });
GEO.BONE_NAMES.forEach((n) => {
  const p = GEO.BONE_PARENT[n];
  manifest.bones.push({ name: n, parent: p ? GEO.BONE_INDEX[p] : -1, rest: v3a(rest0[n]) });
});
const rec = (name, r) => { manifest.meshes.push({ name, ...r }); return r; };
fs.rmSync(OUT, { recursive: true, force: true });

const nHair = STYLE.HAIR_STYLES, nHat = STYLE.HATS.length, nBrow = STYLE.BROWS.length;
const HAIR_BONES = [];
for (let s = 0; s < GEO.HAIR_MAX; s++) { for (let k = 0; k < GEO.HAIR_SEGS; k++) HAIR_BONES.push(`hair${s}_${k}`); }
for (let s = 0; s < GEO.HAIR_MAX; s++) HAIR_BONES.push(`hairTip${s}`);

for (const tier of TIERS) {
  const K = GEO.getKidShared(tier);
  const far = tier === 'far';
  const P = (key, g) => (far ? farPart(key, g) : g);
  const dir = path.join(OUT, tier);
  rec(tier + '/skin', writeMesh(path.join(dir, 'skin.bytes'), P('skin', K.skin), true));
  rec(tier + '/cloth', writeMesh(path.join(dir, 'cloth.bytes'), P('cloth', K.cloth), true));
  rec(tier + '/eyes', writeMesh(path.join(dir, 'eyes.bytes'), P('eyes', K.eyes), true));
  rec(tier + '/tankGlass', writeMesh(path.join(dir, 'tankGlass.bytes'), K.tank.glass, false));
  rec(tier + '/tankFill', writeMesh(path.join(dir, 'tankFill.bytes'), K.tank.fill, false));
  rec(tier + '/squidBody', writeMesh(path.join(dir, 'squidBody.bytes'), K.squid.body, false));
  rec(tier + '/squidDark', writeMesh(path.join(dir, 'squidDark.bytes'), K.squid.dark, false));
  rec(tier + '/squidEyes', writeMesh(path.join(dir, 'squidEyes.bytes'), K.squid.eyes, false));
  if (!manifest.tank) {
    const T = K.tank;
    manifest.tank = { offset: v3a(T.offset), center: v3a(T.center), offsetTank: v3a(T.offsetTank), tilt: T.tilt, fillBottom: T.fillBottom, fillHeight: T.fillHeight };
  }
  for (let h = 0; h < nHair; h++) for (let hat = 0; hat < nHat; hat++) {
    const H = GEO.getHairStyle({ hair: h, hat, brows: 0 }, tier);
    const [hairOnly] = splitBrows(P('hair', H.geo));
    rec(`${tier}/hair_${h}_${hat}`, writeMesh(path.join(dir, `hair_${h}_${hat}.bytes`), hairOnly, true));
    if (tier === TIERS[0]) {
      const rest = GEO.getRestPositions({ hair: h, hat, brows: 0 });
      manifest.hairs.push({
        hair: h, hat, name: H.name, hatName: H.hat, dropped: H.dropped,
        rest: HAIR_BONES.flatMap((n) => v3a(rest[n])),
        strands: H.meta.map((m) => ({ dir: v3a(m.dir), len: +m.len.toFixed(6), K: m.K ?? 1, G: m.G ?? 1 })),
      });
    }
  }
  for (let b = 0; b < nBrow; b++) {
    const H = GEO.getHairStyle({ hair: 0, hat: 0, brows: b }, tier);
    const [, brows] = splitBrows(P('hair', H.geo));
    rec(`${tier}/brows_${b}`, writeMesh(path.join(dir, `brows_${b}.bytes`), brows, true));
  }
}
manifest.hairBones = HAIR_BONES;
fs.writeFileSync(path.join(OUT, 'kid.json'), JSON.stringify(manifest));
const total = manifest.meshes.reduce((s, m) => s + m.bytes, 0);
console.log(`baked ${manifest.meshes.length} meshes, ${(total / 1048576).toFixed(2)} MB, quality ${QUALITY}, tiers ${TIERS} → ${path.relative(ROOT, OUT)}`);
for (const m of manifest.meshes.filter((m) => !/hair_|brows_/.test(m.name) || /hair_0_0|brows_0/.test(m.name))) console.log(`  ${m.name.padEnd(18)} v=${m.verts} t=${m.tris}`);
