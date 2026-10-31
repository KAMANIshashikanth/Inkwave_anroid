// Node module hooks for running INKWAVE's browser modules offline: the bare 'three' / 'three/addons/' specifiers resolve
// to the vendored three.js in the reference clone (the same import map index.html declares). The load hook also exposes
// a few module-private generators to the bakers (EXPOSE: appended `export { … }` lines; the module itself is unchanged
// on disk).
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const REF = path.resolve(process.env.INKWAVE_REF || path.join(import.meta.dirname, '..', '..', 'reference', 'inkwave'));
const THREE = pathToFileURL(path.join(REF, 'vendor', 'three', 'build', 'three.module.js')).href;
const ADDONS = pathToFileURL(path.join(REF, 'vendor', 'three', 'jsm')).href + '/';
const EXPOSE = {
  '/src/game/kits/waddle.js': ['worldGeo'],
};

export async function resolve(spec, ctx, next) {
  if (spec === 'three') return { url: THREE, shortCircuit: true };
  if (spec.startsWith('three/addons/')) return { url: ADDONS + spec.slice('three/addons/'.length), shortCircuit: true };
  return next(spec, ctx);
}

export async function load(url, ctx, next) {
  const key = Object.keys(EXPOSE).find((k) => url.endsWith(k));
  const r = await next(url, ctx);
  if (!key) return r;
  return { ...r, source: String(r.source) + `\nexport { ${EXPOSE[key].join(', ')} };\n`, shortCircuit: true };
}
