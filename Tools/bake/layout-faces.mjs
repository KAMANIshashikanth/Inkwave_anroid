// The web side of Editor/LayoutCheck.cs: builds a stage's Level from INKWAVE's own modules under Node, runs
// layoutLightmap(ppm, size) and prints the layout checksum; writes the hashed face values (×100, rounded) to
// Builds/layout-<id>.web.txt for a line-by-line diff against Builds/layout-<id>.txt.
// usage: node --import ./Tools/bake/register.mjs Tools/bake/layout-faces.mjs tidewater [ppm] [size]
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const ROOT = path.resolve(import.meta.dirname, '..', '..');
const REF = path.join(ROOT, 'reference', 'inkwave');
const imp = (p) => import(pathToFileURL(path.join(REF, 'src', p)).href);
const [id = 'tidewater', ppm = '8', size = '2048'] = process.argv.slice(2);
const { Level } = await imp('world/level.js');
const { MAP_LAYOUTS } = await imp('world/maps.js');
const { layoutFor } = await imp('world/variants.js');
if (!MAP_LAYOUTS[id]) { console.error('stage not found:', id); process.exit(1); }
// (main.js also passes the solid props' colliders; without them this is the bare layout's checksum)
const level = new Level(layoutFor(MAP_LAYOUTS[id], 'turf'));
level.layoutLightmap(+ppm, +size);
console.log(`web layout ${id}: hash ${level.layoutHash}, ${level.faces.length} faces`);
fs.mkdirSync(path.join(ROOT, 'Builds'), { recursive: true });
fs.writeFileSync(path.join(ROOT, 'Builds', `layout-${id}.web.txt`), level.faces.map((f) => [f.origin.x, f.origin.y, f.origin.z, f.su, f.sv].map((v) => Math.round(v * 100)).join(' ') + ' b' + f.block).join('\n') + '\n');
fs.writeFileSync(path.join(ROOT, 'Builds', `blocks-${id}.web.txt`), level.blocks.map((b) => [b.center.x, b.center.y, b.center.z, b.half.x, b.half.y, b.half.z].map((v) => Math.round(v * 100)).join(' ') + (b.solid ? ' S' : ' -') + (b.hidden ? 'H' : '')).join('\n') + '\n');
