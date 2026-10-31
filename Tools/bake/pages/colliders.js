// Bakes a stage's prop colliders (?stage=<id>&mode=turf|zones): main.js _buildWorldNow adds the set dressing first and
// the solid props hand back collision boxes that become part of the level (physics, nav, paint, the lightmap layout).
// Output: Assets/_Game/Resources/Stages/<id>/colliders.<mode>.json — { hash, colliders: [{ min, max } | { obox, center,
// size, rotY }, + roof / rail / perch] } with hash = the layout checksum of the level built with them (main.js uses it
// to accept the baked AO).
import * as THREE from 'three';
import { PropKit } from '/reference/inkwave/src/world/props.js';
import { dressingFor } from '/reference/inkwave/src/world/dressing.js';
import { inMode, layoutFor } from '/reference/inkwave/src/world/variants.js';
import { MAP_LAYOUTS } from '/reference/inkwave/src/world/maps.js';
import { Level } from '/reference/inkwave/src/world/level.js';
import { save, done, fail } from './save.js';

const q = new URLSearchParams(location.search), id = q.get('stage') || 'tidewater', mode = q.get('mode') || 'turf';
const v3 = (v) => (Array.isArray(v) ? v : v ? [v.x, v.y, v.z] : undefined);   // (props hand back plain [x, y, z] arrays)
try {
  const kit = new PropKit(new THREE.Scene(), { castShadow: false, quality: 'high' });
  const colliders = [];
  for (const it of dressingFor(id)) {
    if (!inMode(it, mode)) continue;
    const r = kit.add(it.type, it);
    if (r && r.colliders) colliders.push(...r.colliders);
  }
  const level = new Level(layoutFor(MAP_LAYOUTS[id], mode), colliders);
  const meta = await (await fetch(`/reference/inkwave/assets/lightmaps/${id}.json`)).json().catch(() => null);
  if (meta) level.layoutLightmap(meta.ppm, meta.size);
  console.log(`${id} ${mode}: ${colliders.length} colliders, ${level.blocks.length} blocks, ${level.faces.length} faces, hash ${level.layoutHash} (bake ${meta && meta.hash})`);
  const out = colliders.map((c) => ({ min: v3(c.min), max: v3(c.max), obox: !!c.obox, center: v3(c.center), size: v3(c.size), rotY: c.rotY || 0,
    roof: !!c.roof, rail: !!c.rail, perch: !!c.perch }));
  await save(`Assets/_Game/Resources/Stages/${id}/colliders.${mode}.json`, JSON.stringify({ id, mode, hash: level.layoutHash, colliders: out }));
  done();
} catch (e) { fail(e); }
