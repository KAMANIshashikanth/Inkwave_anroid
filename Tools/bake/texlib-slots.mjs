// Bakes the level shader's per-PATTERN texlib slot table — the part of src/world/levelMaterial.js createLevelMaterial
// that turns the PATTERN ids (mapkit.js + the stages' own slots, stages/surfaces.js) into texlib layers — from the baked
// library's texlib.json (layers + meta, Tools/bake/pages/texlib.js). Same rows as the JS uTLTex float texture:
//   row 0 = uTL  (layer, 1/scale, anti-tiling mode, sym)
//   row 1 = uTLt (tint mode, grate, onWall slot, onTop slot)
//   row 2 = uTLs (stair: period, steps per repeat, relief kept under ink, edge kind)
// Output: Assets/_Game/Resources/Texlib/texlib_slots.json { slots, gel, data: slots × 3 rows × 4 floats, row-major }.
//
// usage: node --import ./Tools/bake/register.mjs Tools/bake/texlib-slots.mjs
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const ROOT = path.resolve(import.meta.dirname, '..', '..');
const REF = path.resolve(process.env.INKWAVE_REF || path.join(ROOT, 'reference', 'inkwave'));
const DIR = path.join(ROOT, 'Assets', '_Game', 'Resources', 'Texlib');
const { STAGE_SURFACES, FIRST_STAGE_SLOT, LAST_STAGE_SLOT } = await import(pathToFileURL(path.join(REF, 'src', 'world', 'stages', 'surfaces.js')).href);
const lib = JSON.parse(fs.readFileSync(path.join(DIR, 'texlib.json'), 'utf8'));

// ---- verbatim from levelMaterial.js createLevelMaterial (USE_TEXLIB branch) ----
const L = lib.layers, M = lib.meta;
const map = ['concrete', 'pavers', 'tiles', 'concrete', 'rubber', 'corrugated', 'boardwalk', 'metalpanel', 'tiles', 'concrete',
  'asphalt', 'metalpanel', 'grate', 'brick', 'rubber', 'glasstile', 'pavers',
  /* 17 planks … 23 render (marina set) */ 'planks', 'hullpaint', 'nonslip', 'gelcoat', 'yard', 'weatherboard', 'render',
  /* 24 treads … 27 gangdeck (stairs + ramps) */ 'treads', 'stonestep', 'rampboard', 'gangdeck',
  /* 28 … LAST_STAGE_SLOT: stage-owned surfaces (stages/<id>/surfaces.js), concrete where a slot is unused */
  ...Array.from({ length: LAST_STAGE_SLOT - FIRST_STAGE_SLOT + 1 }, (_, k) => (STAGE_SURFACES.find((s) => s.slot === FIRST_STAGE_SLOT + k) || { name: 'concrete' }).name),
  /* TL_SIDE */ 'concrete'];
const SIDE = map.length - 1;
const onWall = { 4: SIDE, 10: SIDE, 21: SIDE, 19: 18, 24: 18, 25: 23, 26: 17, 27: 11 };
const onTop = { 20: 17 };
for (const s of STAGE_SURFACES) { if (s.onWall != null) onWall[s.slot] = s.onWall; if (s.onTop != null) onTop[s.slot] = s.onTop; }
const tlRows = [
  map.map((n) => [L[n] ?? 0, 1 / ((M[n] && M[n].scale) || 4), (M[n] && M[n].mode) ?? 1, (M[n] && M[n].sym) ?? 7]),
  map.map((n, i) => [M[n] && M[n].mask ? 2 : (M[n] && M[n].tint === false ? 0 : 1), n === 'grate' ? 0.6 : 1.0, onWall[i] ?? i, onTop[i] ?? i]),
  map.map((n) => { const st = (M[n] && M[n].stair) || [0, 0, 0, 0]; return [st[0], st[1], st[2], st[3]]; }),
];
// ---- end verbatim ----

const data = tlRows.flat(2);
const missing = map.filter((n) => L[n] == null);
if (missing.length) throw new Error(`texlib.json has no layer for: ${missing.join(', ')}`);
fs.writeFileSync(path.join(DIR, 'texlib_slots.json'), JSON.stringify({ slots: map.length, gel: L.gel ?? -1, names: map, data }));
console.log(`texlib_slots.json: ${map.length} slots (gel layer ${L.gel}), ${data.length} floats`);
