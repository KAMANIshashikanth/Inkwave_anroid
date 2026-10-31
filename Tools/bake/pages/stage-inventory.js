// Inventory of a stage's dressing as main.js builds it (?stage=<id>&time=day|dusk&quality=low|medium|high): what the
// prop kit, decor and environment put in the scene — object kinds, material kinds (custom shader chunks), triangles and
// instances — to scope the stage exporter (Task 7). Prints to the console; saves nothing.
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
import { done, fail } from './save.js';

const q = new URLSearchParams(location.search), id = q.get('stage') || 'tidewater', time = q.get('time') || 'day', quality = q.get('quality') || 'low';
// main.js _footprint
const footprint = (level) => level.blocks.filter((b) => (b.aligned || Math.abs(b.axes[1].y) > 0.9999) && b.aabbMax.y < 0.01 && b.aabbMax.y > -2.5 && b.aabbMin.y < -1)
  .map((b) => ({ minX: b.aabbMin.x, maxX: b.aabbMax.x, minZ: b.aabbMin.z, maxZ: b.aabbMax.z,
    ...(b.aligned ? {} : { cx: b.center.x, cz: b.center.z, hx: b.half.x, hz: b.half.z, ax: b.axes[0].x, az: b.axes[0].z }) }));
try {
  const canvas = document.createElement('canvas'); canvas.width = 64; canvas.height = 64;
  const renderer = new THREE.WebGLRenderer({ canvas });
  G.renderer = renderer;
  G.settings = { quality };
  const scene = new THREE.Scene(); G.scene = scene;
  const murals = await createMuralTexture();
  const kit = new PropKit(scene, { castShadow: true, quality });
  const colliders = [];
  for (const it of dressingFor(id)) { if (!inMode(it, 'turf')) continue; const r = kit.add(it.type, it); if (r && r.colliders) colliders.push(...r.colliders); }
  kit.build();
  const level = new Level(layoutFor(MAP_LAYOUTS[id], 'turf'), colliders);
  G.level = level;
  murals.userData.setStage?.(id);
  const decor = new Decor(scene, level);
  const map = MAPS.find((m) => m.id === id);
  const theme = mapTheme(map, time);
  const env = new Environment(renderer, scene, { bounds: level.bounds, theme, shadowSize: 1024, footprint: footprint(level), forceDeckField: true });
  console.log(`stage ${id} ${time} → theme ${theme}; murals ${murals.image?.width}x${murals.image?.height}, ${murals.userData.murals?.length ?? 0} murals`);
  const top = {};
  scene.children.forEach((c) => { top[c.name || c.type] = (top[c.name || c.type] || 0) + 1; });
  console.log('top-level:', JSON.stringify(top));
  const kinds = {}, mats = new Map();
  let tris = 0;
  scene.traverse((o) => {
    const k = o.isInstancedMesh ? 'InstancedMesh' : o.isSkinnedMesh ? 'SkinnedMesh' : o.isMesh ? 'Mesh' : o.isPoints ? 'Points' : o.isLine ? 'Line' : o.isSprite ? 'Sprite' : o.isLight ? o.type : null;
    if (!k) return;
    kinds[k] = (kinds[k] || 0) + 1;
    if (!o.geometry) return;
    const g = o.geometry, n = (g.index ? g.index.count : g.attributes.position.count) / 3;
    const inst = o.isInstancedMesh ? o.count : 1;
    tris += n * inst;
    for (const m of Array.isArray(o.material) ? o.material : [o.material]) {
      if (!m) continue;
      const key = m.uuid;
      if (!mats.has(key)) mats.set(key, { type: m.type, name: m.name, obc: !!m.onBeforeCompile && m.onBeforeCompile.toString().length > 40, map: !!m.map, emissiveMap: !!m.emissiveMap, vc: !!m.vertexColors,
        transparent: !!m.transparent, side: m.side, objs: 0, tris: 0, attrs: Object.keys(g.attributes).join(','), owner: o.name || o.parent?.name || '' });
      const r = mats.get(key); r.objs++; r.tris += n * inst;
    }
  });
  console.log('objects:', JSON.stringify(kinds), 'tris', tris | 0);
  const byType = {};
  for (const m of mats.values()) { const k = `${m.type}${m.obc ? '+obc' : ''}${m.map ? '+map' : ''}${m.transparent ? '+T' : ''}`; byType[k] = (byType[k] || 0) + 1; }
  console.log('materials:', mats.size, JSON.stringify(byType));
  for (const m of [...mats.values()].sort((a, b) => b.tris - a.tris).slice(0, 60))
    console.log(`  ${m.type}${m.obc ? ' OBC' : ''}${m.map ? ' map' : ''}${m.vc ? ' vc' : ''}${m.transparent ? ' transp' : ''} side${m.side} '${m.name}' owner '${m.owner}' objs ${m.objs} tris ${m.tris | 0} [${m.attrs}]`);
  done();
} catch (e) { fail(e); }
