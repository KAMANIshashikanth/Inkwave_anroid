// Bakes INKWAVE's GPU texture library (src/world/texlib.js) for one size (?size=256|512): each map (albedo sRGB, normal,
// orm) as one atlas PNG with the layers on a cols × rows grid (layer i at column i % cols, row i / cols, top row first),
// which Unity imports as a Texture2DArray (TextureImporter flipbook). Each slice is stored top row = v 1, so a Unity uv
// samples the texel three samples at the same uv. Also texlib.json: names, layers (name → index), meta, grid.
import * as THREE from 'three';
import { createTextureLibrary } from '/reference/inkwave/src/world/texlib.js';
import { save, savePng, done, fail } from './save.js';

const size = +(new URLSearchParams(location.search).get('size') || 512);
try {
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = 8;
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: false });
  const lib = await createTextureLibrary(renderer, { size });
  console.log(`texlib ${size}: ${lib.names.length} layers in ${lib.stats.ms | 0} ms (compile ${lib.stats.compileMs | 0} ms)`);
  const N = lib.names.length, cols = Math.ceil(Math.sqrt(N)), rows = Math.ceil(N / cols);
  const rt = lib.normal.renderTarget;   // (texlib sets it on its swapped-in attachments 1 and 2)
  const buf = new Uint8Array(size * size * 4);
  for (const [k, map] of ['albedo', 'normal', 'orm'].entries()) {
    const AW = cols * size, atlas = new Uint8Array(AW * rows * size * 4);
    for (let i = 0; i < N; i++) {
      renderer.setRenderTarget(rt, i);
      renderer.readRenderTargetPixels(rt, 0, 0, size, size, buf, undefined, k);
      const cx = (i % cols) * size, cy = Math.floor(i / cols) * size;
      for (let r = 0; r < size; r++) {   // readPixels row 0 = v 0 (bottom) → the slice's bottom row in the PNG
        const dst = ((cy + size - 1 - r) * AW + cx) * 4;
        atlas.set(buf.subarray(r * size * 4, (r + 1) * size * 4), dst);
      }
    }
    await savePng(`Assets/_Game/Resources/Texlib/texlib_${map}_${size}.png`, AW, rows * size, atlas);
  }
  renderer.setRenderTarget(null);
  await save('Assets/_Game/Resources/Texlib/texlib.json', JSON.stringify({ names: lib.names, layers: lib.layers, meta: lib.meta, cols, rows }, null, 1));
  done();
} catch (e) { fail(e); }
