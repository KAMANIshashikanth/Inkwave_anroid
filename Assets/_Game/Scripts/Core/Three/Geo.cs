// A minimal three.js BufferGeometry stand-in: position / normal / uv (+ optional colour, uv2) streams and an index,
// built and transformed in three space, then turned into a Unity Mesh. Vertex order, UVs and winding follow
// three.js r186 exactly (the generators below are ports of its geometry classes, MIT), so procedural code that
// indexes vertices or relies on UV layout behaves identically. Meshes go under the mirrored world root
// (ThreeSpace.root), which is what makes three's counter-clockwise front faces render correctly in Unity.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Inkwave
{
    public class Geo
    {
        public readonly List<Vector3> position = new List<Vector3>();
        public readonly List<Vector3> normal = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public List<Vector2> uv1;          // three's 'uv1' (second channel), allocated on demand
        public List<Color> color;          // vertex colours, allocated on demand
        public readonly List<int> index = new List<int>();

        public int vertexCount => position.Count;

        public Geo ensureColor(Color fill)
        {
            if (color == null) color = new List<Color>(position.Count);
            while (color.Count < position.Count) color.Add(fill);
            return this;
        }

        public Geo ensureUv1()
        {
            if (uv1 == null) uv1 = new List<Vector2>(position.Count);
            while (uv1.Count < position.Count) uv1.Add(Vector2.zero);
            return this;
        }

        // ---------------------------------------------------------------- transforms (three-space, in place)
        public Geo applyMatrix4(Matrix4x4 m)
        {
            // normals by the inverse-transpose, renormalised (three's Matrix3.getNormalMatrix)
            Matrix4x4 nm = m.inverse.transpose;
            for (int i = 0; i < position.Count; i++) position[i] = m.MultiplyPoint3x4(position[i]);
            for (int i = 0; i < normal.Count; i++) normal[i] = nm.MultiplyVector(normal[i]).normalized;
            // like three, a mirroring matrix does NOT re-wind the triangles: callers that mirror call flipWinding()
            return this;
        }
        public Geo translate(float x, float y, float z) => applyMatrix4(Matrix4x4.Translate(new Vector3(x, y, z)));
        public Geo rotateX(float a) => applyMatrix4(Matrix4x4.Rotate(Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.right)));
        public Geo rotateY(float a) => applyMatrix4(Matrix4x4.Rotate(Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.up)));
        public Geo rotateZ(float a) => applyMatrix4(Matrix4x4.Rotate(Quaternion.AngleAxis(a * Mathf.Rad2Deg, Vector3.forward)));
        public Geo scale(float x, float y, float z) => applyMatrix4(Matrix4x4.Scale(new Vector3(x, y, z)));

        public void flipWinding()
        {
            for (int i = 0; i + 2 < index.Count; i += 3) { int t = index[i + 1]; index[i + 1] = index[i + 2]; index[i + 2] = t; }
        }

        // append another geometry (BufferGeometryUtils.mergeGeometries for two)
        public Geo merge(Geo g)
        {
            int o = position.Count;
            if (g.color != null || color != null) { ensureColor(Color.white); g.ensureColor(Color.white); }
            if (g.uv1 != null || uv1 != null) { ensureUv1(); g.ensureUv1(); }
            position.AddRange(g.position);
            normal.AddRange(g.normal);
            uv.AddRange(g.uv);
            if (color != null) color.AddRange(g.color);
            if (uv1 != null) uv1.AddRange(g.uv1);
            for (int i = 0; i < g.index.Count; i++) index.Add(g.index[i] + o);
            return this;
        }

        public Geo clone()
        {
            var g = new Geo();
            g.merge(this);
            return g;
        }

        // ---------------------------------------------------------------- Unity mesh
        public Mesh toMesh(string name = "geo", Mesh into = null)
        {
            var mesh = into ?? new Mesh();
            mesh.Clear();
            mesh.name = name;
            if (position.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(position);
            if (normal.Count == position.Count) mesh.SetNormals(normal);
            if (uv.Count == position.Count) mesh.SetUVs(0, uv);
            if (uv1 != null && uv1.Count == position.Count) mesh.SetUVs(1, uv1);
            if (color != null && color.Count == position.Count) mesh.SetColors(color);
            mesh.SetTriangles(index, 0, true);
            if (normal.Count != position.Count) mesh.RecalculateNormals();
            return mesh;
        }

        // ================================================================ generators (three.js r186 ports)

        // BoxGeometry(width, height, depth, widthSegments, heightSegments, depthSegments)
        public static Geo box(float width = 1, float height = 1, float depth = 1, int widthSegments = 1, int heightSegments = 1, int depthSegments = 1)
        {
            var g = new Geo();
            g.buildPlane(2, 1, 0, -1, -1, depth, height, width, depthSegments, heightSegments);   // px
            g.buildPlane(2, 1, 0, 1, -1, depth, height, -width, depthSegments, heightSegments);   // nx
            g.buildPlane(0, 2, 1, 1, 1, width, depth, height, widthSegments, depthSegments);      // py
            g.buildPlane(0, 2, 1, 1, -1, width, depth, -height, widthSegments, depthSegments);    // ny
            g.buildPlane(0, 1, 2, 1, -1, width, height, depth, widthSegments, heightSegments);    // pz
            g.buildPlane(0, 1, 2, -1, -1, width, height, -depth, widthSegments, heightSegments);  // nz
            return g;
        }

        // SphereGeometry(radius, widthSegments, heightSegments, phiStart, phiLength, thetaStart, thetaLength)
        public static Geo sphere(float radius = 1, int widthSegments = 32, int heightSegments = 16, float phiStart = 0, float phiLength = Mathf.PI * 2, float thetaStart = 0, float thetaLength = Mathf.PI)
        {
            var g = new Geo();
            widthSegments = Mathf.Max(3, widthSegments);
            heightSegments = Mathf.Max(2, heightSegments);
            float thetaEnd = Mathf.Min(thetaStart + thetaLength, Mathf.PI);
            int index = 0;
            var grid = new List<int[]>();
            for (int iy = 0; iy <= heightSegments; iy++)
            {
                var verticesRow = new int[widthSegments + 1];
                float v = (float)iy / heightSegments;
                // special case for the poles
                float uOffset = 0;
                if (iy == 0 && thetaStart == 0) uOffset = 0.5f / widthSegments;
                else if (iy == heightSegments && thetaEnd == Mathf.PI) uOffset = -0.5f / widthSegments;
                for (int ix = 0; ix <= widthSegments; ix++)
                {
                    float u = (float)ix / widthSegments;
                    var vertex = new Vector3(
                        -radius * Mathf.Cos(phiStart + u * phiLength) * Mathf.Sin(thetaStart + v * thetaLength),
                        radius * Mathf.Cos(thetaStart + v * thetaLength),
                        radius * Mathf.Sin(phiStart + u * phiLength) * Mathf.Sin(thetaStart + v * thetaLength));
                    g.position.Add(vertex);
                    g.normal.Add(vertex.normalized);
                    g.uv.Add(new Vector2(u + uOffset, 1 - v));
                    verticesRow[ix] = index++;
                }
                grid.Add(verticesRow);
            }
            for (int iy = 0; iy < heightSegments; iy++)
                for (int ix = 0; ix < widthSegments; ix++)
                {
                    int a = grid[iy][ix + 1], b = grid[iy][ix], c = grid[iy + 1][ix], d = grid[iy + 1][ix + 1];
                    if (iy != 0 || thetaStart > 0) { g.index.Add(a); g.index.Add(b); g.index.Add(d); }
                    if (iy != heightSegments - 1 || thetaEnd < Mathf.PI) { g.index.Add(b); g.index.Add(c); g.index.Add(d); }
                }
            return g;
        }

        // CylinderGeometry(radiusTop, radiusBottom, height, radialSegments, heightSegments, openEnded, thetaStart, thetaLength)
        public static Geo cylinder(float radiusTop = 1, float radiusBottom = 1, float height = 1, int radialSegments = 32, int heightSegments = 1, bool openEnded = false, float thetaStart = 0, float thetaLength = Mathf.PI * 2)
        {
            var g = new Geo();
            int index = 0;
            float halfHeight = height / 2;
            // torso
            var indexArray = new List<int[]>();
            float slope = (radiusBottom - radiusTop) / height;
            for (int y = 0; y <= heightSegments; y++)
            {
                var indexRow = new int[radialSegments + 1];
                float v = (float)y / heightSegments;
                float radius = v * (radiusBottom - radiusTop) + radiusTop;
                for (int x = 0; x <= radialSegments; x++)
                {
                    float u = (float)x / radialSegments;
                    float theta = u * thetaLength + thetaStart;
                    float sinTheta = Mathf.Sin(theta), cosTheta = Mathf.Cos(theta);
                    g.position.Add(new Vector3(radius * sinTheta, -v * height + halfHeight, radius * cosTheta));
                    g.normal.Add(new Vector3(sinTheta, slope, cosTheta).normalized);
                    g.uv.Add(new Vector2(u, 1 - v));
                    indexRow[x] = index++;
                }
                indexArray.Add(indexRow);
            }
            for (int x = 0; x < radialSegments; x++)
                for (int y = 0; y < heightSegments; y++)
                {
                    int a = indexArray[y][x], b = indexArray[y + 1][x], c = indexArray[y + 1][x + 1], d = indexArray[y][x + 1];
                    if (radiusTop > 0 || y != 0) { g.index.Add(a); g.index.Add(b); g.index.Add(d); }
                    if (radiusBottom > 0 || y != heightSegments - 1) { g.index.Add(b); g.index.Add(c); g.index.Add(d); }
                }
            // caps
            void cap(bool top)
            {
                int centerIndexStart = index;
                float radius = top ? radiusTop : radiusBottom, sign = top ? 1 : -1;
                for (int x = 1; x <= radialSegments; x++)
                {
                    g.position.Add(new Vector3(0, halfHeight * sign, 0)); g.normal.Add(new Vector3(0, sign, 0)); g.uv.Add(new Vector2(0.5f, 0.5f));
                    index++;
                }
                int centerIndexEnd = index;
                for (int x = 0; x <= radialSegments; x++)
                {
                    float u = (float)x / radialSegments;
                    float theta = u * thetaLength + thetaStart;
                    float cosTheta = Mathf.Cos(theta), sinTheta = Mathf.Sin(theta);
                    g.position.Add(new Vector3(radius * sinTheta, halfHeight * sign, radius * cosTheta)); g.normal.Add(new Vector3(0, sign, 0));
                    g.uv.Add(new Vector2(cosTheta * 0.5f + 0.5f, sinTheta * 0.5f * sign + 0.5f));
                    index++;
                }
                for (int x = 0; x < radialSegments; x++)
                {
                    int c = centerIndexStart + x, i = centerIndexEnd + x;
                    if (top) { g.index.Add(i); g.index.Add(i + 1); g.index.Add(c); }
                    else { g.index.Add(i + 1); g.index.Add(i); g.index.Add(c); }
                }
            }
            if (!openEnded)
            {
                if (radiusTop > 0) cap(true);
                if (radiusBottom > 0) cap(false);
            }
            return g;
        }

        // u/v/w are axis indices (0 = x, 1 = y, 2 = z)
        void buildPlane(int u, int v, int w, float udir, float vdir, float width, float height, float depth, int gridX, int gridY)
        {
            float segmentWidth = width / gridX, segmentHeight = height / gridY;
            float widthHalf = width / 2, heightHalf = height / 2, depthHalf = depth / 2;
            int gridX1 = gridX + 1, gridY1 = gridY + 1;
            int start = position.Count;
            for (int iy = 0; iy < gridY1; iy++)
            {
                float y = iy * segmentHeight - heightHalf;
                for (int ix = 0; ix < gridX1; ix++)
                {
                    float x = ix * segmentWidth - widthHalf;
                    var vec = Vector3.zero;
                    vec[u] = x * udir; vec[v] = y * vdir; vec[w] = depthHalf;
                    position.Add(vec);
                    var n = Vector3.zero;
                    n[w] = depth > 0 ? 1 : -1;
                    normal.Add(n);
                    uv.Add(new Vector2((float)ix / gridX, 1 - ((float)iy / gridY)));
                }
            }
            for (int iy = 0; iy < gridY; iy++)
                for (int ix = 0; ix < gridX; ix++)
                {
                    int a = start + ix + gridX1 * iy;
                    int b = start + ix + gridX1 * (iy + 1);
                    int c = start + (ix + 1) + gridX1 * (iy + 1);
                    int d = start + (ix + 1) + gridX1 * iy;
                    index.Add(a); index.Add(b); index.Add(d);
                    index.Add(b); index.Add(c); index.Add(d);
                }
        }
    }
}
