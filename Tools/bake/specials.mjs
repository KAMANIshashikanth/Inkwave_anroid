// Bakes the specials' world props (Kraken, Howl Box speaker, Vortex Strike missile, Ink Jet pack, Crab Rig) by running
// INKWAVE's src/game/special-props.js under Node.
// Output: Assets/_Game/Resources/SpecialProps/
//   props.json        per kind: the def with every geometry replaced by its mesh name, vectors as [x, y, z], numbers as-is
//   <kind>/<name>.bytes   (IWK1, see iwk.mjs); nested parts are named by their path: legs_0_body, gun_ink, …
//
// usage: node --import ./Tools/bake/register.mjs Tools/bake/specials.mjs
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { writeMesh } from './iwk.mjs';

const ROOT = path.resolve(import.meta.dirname, '..', '..');
const REF = path.resolve(process.env.INKWAVE_REF || path.join(ROOT, 'reference', 'inkwave'));
const OUT = path.join(ROOT, 'Assets', '_Game', 'Resources', 'SpecialProps');
const imp = (p) => import(pathToFileURL(path.join(REF, 'src', p)).href);
const { G } = await imp('core/ctx.js');
G.settings = { quality: 'low' };
const SP = await imp('game/special-props.js');

const r6 = (x) => +x.toFixed(6);
// clear the old meshes but keep Unity's .meta files (stable GUIDs across re-bakes)
for (const f of fs.existsSync(OUT) ? fs.readdirSync(OUT, { recursive: true }) : []) {
  const p = path.join(OUT, f);
  if (fs.statSync(p).isFile() && !p.endsWith('.meta')) fs.rmSync(p);
}
const man = { version: 1, props: [] };   // (an array: JsonUtility reads no dictionaries)
let bytes = 0, count = 0;

// the def as JSON: geometry → mesh file (named by its key path), Vector3 → [x, y, z], arrays / objects recursed
function walk(kind, v, name) {
  if (v == null) return null;
  if (v.isBufferGeometry) {
    if (!v.attributes.position.count) return null;
    bytes += writeMesh(path.join(OUT, kind, name + '.bytes'), v, false).bytes; count++;
    return name;
  }
  if (v.isVector3) return [r6(v.x), r6(v.y), r6(v.z)];
  if (typeof v === 'number') return r6(v);
  if (typeof v === 'string' || typeof v === 'boolean') return v;
  // a list of vectors flattens to [x, y, z, x, y, z …] (JsonUtility reads no nested arrays)
  if (Array.isArray(v) && v.length && v.every((x) => x && x.isVector3)) return v.flatMap((x) => [r6(x.x), r6(x.y), r6(x.z)]);
  if (Array.isArray(v)) return v.map((x, i) => walk(kind, x, `${name}_${i}`));
  if (typeof v === 'object') {
    const o = {};
    for (const k of Object.keys(v)) { const w = walk(kind, v[k], name ? `${name}_${k}` : k); if (w != null) o[k] = w; }
    return o;
  }
  return null;
}

for (const kind of SP.SPECIAL_PROP_KINDS) man.props.push(walk(kind, SP.getSpecialProp(kind), ''));
fs.writeFileSync(path.join(OUT, 'props.json'), JSON.stringify(man));
console.log(`baked ${SP.SPECIAL_PROP_KINDS.length} special props: ${count} meshes, ${(bytes / 1048576).toFixed(2)} MB → ${path.relative(ROOT, OUT)}`);
