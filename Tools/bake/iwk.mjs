// IWK1 mesh writer shared by the bakers (layout documented in character.mjs; read by Game/CharacterGeo.cs).
import fs from 'node:fs';
import path from 'node:path';

const VA = { Position: 0, Normal: 1, TexCoord0: 4, TexCoord1: 5, TexCoord2: 6, TexCoord3: 7, TexCoord4: 8, TexCoord5: 9 };
const F32 = 0, F16 = 1;
const _f = new Float32Array(1), _u = new Uint32Array(_f.buffer);
function toHalf(v) {
  _f[0] = v; const x = _u[0];
  const s = (x >>> 16) & 0x8000; let e = ((x >>> 23) & 0xff) - 127 + 15; let m = x & 0x7fffff;
  if (e >= 31) return s | 0x7c00 | (((x >>> 23) & 0xff) === 0xff && m ? 0x200 : 0);
  if (e <= 0) { if (e < -10) return s; m |= 0x800000; const sh = 14 - e; let h = m >> sh; if ((m >> (sh - 1)) & 1) h++; return s | h; }
  let h = s | (e << 10) | (m >> 13); if (m & 0x1000) h++; return h;
}
// weapons: aMat (surface class) rides the ex channel, the coil's aSeg (ring threshold) TexCoord3.x
const V3_NAMES = ['aHead', 'aCloth', 'aHair', 'aEyeS', 'aSq', 'aSeg'];
const KNOWN = new Set(['position', 'normal', 'uv', 'color', 'aEx', 'aTint', 'aMat', 'aFace', 'aOcc', 'skinIndex', 'skinWeight', ...V3_NAMES]);

export function channels(g, skinned = false) {
  const A = g.attributes, ch = [];
  for (const k in A) if (!KNOWN.has(k)) throw new Error('unmapped attribute ' + k);
  ch.push({ va: VA.Position, fmt: F32, dim: 3, get: (i, o) => { o[0] = A.position.getX(i); o[1] = A.position.getY(i); o[2] = A.position.getZ(i); } });
  if (A.normal) ch.push({ va: VA.Normal, fmt: F16, dim: 4, get: (i, o) => { o[0] = A.normal.getX(i); o[1] = A.normal.getY(i); o[2] = A.normal.getZ(i); o[3] = 0; } });
  if (A.uv) ch.push({ va: VA.TexCoord0, fmt: F32, dim: 2, get: (i, o) => { o[0] = A.uv.getX(i); o[1] = A.uv.getY(i); } });
  if (A.color) ch.push({ va: VA.TexCoord1, fmt: F16, dim: 4, get: (i, o) => { o[0] = A.color.getX(i); o[1] = A.color.getY(i); o[2] = A.color.getZ(i); o[3] = 0; } });
  const ex = A.aEx || A.aTint || A.aMat;
  if (ex) ch.push({ va: VA.TexCoord2, fmt: F32, dim: 1, get: (i, o) => { o[0] = ex.getX(i); } });
  const v3 = V3_NAMES.map((n) => A[n]).find(Boolean), occ = A.aOcc;
  const c3 = (i, k) => (v3 && k < v3.itemSize ? v3.getComponent(i, k) : 0);
  if (v3 || occ) ch.push({ va: VA.TexCoord3, fmt: F16, dim: 4, get: (i, o) => { o[0] = c3(i, 0); o[1] = c3(i, 1); o[2] = c3(i, 2); o[3] = occ ? occ.getX(i) : 0; } });
  if (A.aFace) ch.push({ va: VA.TexCoord4, fmt: F16, dim: 4, get: (i, o) => { o[0] = A.aFace.getX(i); o[1] = A.aFace.getY(i); o[2] = A.aFace.getZ(i); o[3] = A.aFace.getW(i); } });
  // skinned meshes: the rest position again (three's vBindPos — Unity hands the vertex shader the skinned position)
  if (skinned) ch.push({ va: VA.TexCoord5, fmt: F32, dim: 3, get: (i, o) => { o[0] = A.position.getX(i); o[1] = A.position.getY(i); o[2] = A.position.getZ(i); } });
  return ch;
}

export function writeMesh(file, g, skinned) {
  const n = g.attributes.position.count, idx = g.index.array, ni = idx.length;
  if (n > 65535) throw new Error(file + ': more than 65535 vertices');
  const ch = channels(g, skinned);
  const stride = ch.reduce((s, c) => s + c.dim * (c.fmt === F32 ? 4 : 2), 0);
  const skinBytes = skinned ? n * 8 : 0, idxBytes = (ni * 2 + 3) & ~3;
  const head = 24 + ch.length * 4;
  const buf = Buffer.alloc(head + n * stride + skinBytes + idxBytes);
  buf.write('IWK1', 0, 'ascii');
  buf.writeUInt32LE(n, 4); buf.writeUInt32LE(ni, 8); buf.writeUInt32LE(skinned ? 1 : 0, 12); buf.writeUInt32LE(stride, 16); buf.writeUInt32LE(ch.length, 20);
  ch.forEach((c, k) => { buf.writeUInt8(c.va, 24 + k * 4); buf.writeUInt8(c.fmt, 25 + k * 4); buf.writeUInt8(c.dim, 26 + k * 4); });
  let o = head; const tmp = [0, 0, 0, 0];
  for (let i = 0; i < n; i++) for (const c of ch) {
    c.get(i, tmp);
    for (let d = 0; d < c.dim; d++) { if (c.fmt === F32) { buf.writeFloatLE(tmp[d], o); o += 4; } else { buf.writeUInt16LE(toHalf(tmp[d]), o); o += 2; } }
  }
  if (skinned) {
    const SI = g.attributes.skinIndex, SW = g.attributes.skinWeight;
    for (let i = 0; i < n; i++) {
      // weights quantised to 1/255 with the rounding remainder on the largest (Builder already sorts them descending)
      const w = [0, 1, 2, 3].map((k) => SW.getComponent(i, k));
      const q = w.map((x) => Math.round(x * 255)); q[0] += 255 - q.reduce((a, b) => a + b, 0);
      for (let k = 0; k < 4; k++) { buf.writeUInt8(q[k] > 0 ? SI.getComponent(i, k) : 0, o + k); buf.writeUInt8(Math.max(0, q[k]), o + n * 4 + k); }
      o += 4;
    }
    o += n * 4;
  }
  for (let i = 0; i < ni; i++) { buf.writeUInt16LE(idx[i], o); o += 2; }
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, buf);
  return { verts: n, tris: ni / 3, bytes: buf.length };
}
