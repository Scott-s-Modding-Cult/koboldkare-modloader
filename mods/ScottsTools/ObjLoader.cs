using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ScottsTools {
    /// <summary>
    /// Minimal Wavefront OBJ reader (positions, UVs, normals, n-gon faces). Each face goes into one of
    /// several meshes picked by <c>pick</c>, so one model can be split into parts (launcher / warhead).
    /// </summary>
    internal static class ObjLoader {
        /// <param name="convert">Maps an OBJ position/direction into mesh space. It must be a mirror
        /// (OBJ is right-handed, Unity left-handed), so triangle winding is reversed here.</param>
        /// <param name="pick">Gets a face's centroid in OBJ space and returns the mesh index for it.</param>
        public static Mesh[] Load(Stream stream, int meshCount, Func<Vector3, Vector3> convert, Func<Vector3, int> pick) {
            var positions = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var builders = new Builder[meshCount];
            for (int i = 0; i < meshCount; i++) {
                builders[i] = new Builder();
            }

            var corners = new List<(int v, int t, int n)>();
            using (var reader = new StreamReader(stream)) {
                string line;
                while ((line = reader.ReadLine()) != null) {
                    if (line.Length < 2) {
                        continue;
                    }
                    string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) {
                        continue;
                    }
                    switch (parts[0]) {
                        case "v":
                            positions.Add(new Vector3(F(parts[1]), F(parts[2]), F(parts[3])));
                            break;
                        case "vt":
                            uvs.Add(new Vector2(F(parts[1]), F(parts[2])));
                            break;
                        case "vn":
                            normals.Add(new Vector3(F(parts[1]), F(parts[2]), F(parts[3])));
                            break;
                        case "f":
                            corners.Clear();
                            Vector3 centroid = Vector3.zero;
                            for (int i = 1; i < parts.Length; i++) {
                                string[] idx = parts[i].Split('/');
                                int v = Index(idx[0], positions.Count);
                                int t = idx.Length > 1 && idx[1].Length > 0 ? Index(idx[1], uvs.Count) : -1;
                                int n = idx.Length > 2 && idx[2].Length > 0 ? Index(idx[2], normals.Count) : -1;
                                corners.Add((v, t, n));
                                centroid += positions[v];
                            }
                            centroid /= corners.Count;
                            Builder b = builders[Mathf.Clamp(pick(centroid), 0, meshCount - 1)];
                            // Fan-triangulate; reversed winding because convert() mirrors.
                            for (int i = 1; i + 1 < corners.Count; i++) {
                                b.Triangles.Add(b.Vertex(corners[0], positions, uvs, normals, convert));
                                b.Triangles.Add(b.Vertex(corners[i + 1], positions, uvs, normals, convert));
                                b.Triangles.Add(b.Vertex(corners[i], positions, uvs, normals, convert));
                            }
                            break;
                    }
                }
            }

            var meshes = new Mesh[meshCount];
            for (int i = 0; i < meshCount; i++) {
                meshes[i] = builders[i].Build();
            }
            return meshes;
        }

        private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static int Index(string s, int count) {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            return i < 0 ? count + i : i - 1;
        }

        private class Builder {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<int> Triangles = new List<int>();
            private readonly Dictionary<(int, int, int), int> lookup = new Dictionary<(int, int, int), int>();
            private bool missingNormals;

            public int Vertex((int v, int t, int n) c, List<Vector3> p, List<Vector2> t, List<Vector3> n, Func<Vector3, Vector3> convert) {
                if (lookup.TryGetValue(c, out int index)) {
                    return index;
                }
                index = Positions.Count;
                Positions.Add(convert(p[c.v]));
                Uvs.Add(c.t >= 0 ? t[c.t] : Vector2.zero);
                if (c.n >= 0) {
                    Normals.Add(convert(n[c.n]).normalized);
                } else {
                    Normals.Add(Vector3.up);
                    missingNormals = true;
                }
                lookup.Add(c, index);
                return index;
            }

            public Mesh Build() {
                var mesh = new Mesh();
                if (Positions.Count > 65000) {
                    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                }
                mesh.SetVertices(Positions);
                mesh.SetUVs(0, Uvs);
                mesh.SetNormals(Normals);
                mesh.SetTriangles(Triangles, 0);
                if (missingNormals) {
                    mesh.RecalculateNormals();
                }
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
