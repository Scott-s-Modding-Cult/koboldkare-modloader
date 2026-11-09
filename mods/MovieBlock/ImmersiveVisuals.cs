using UnityEngine;

namespace MovieBlock {
    /// <summary>
    /// The 3D parts of immersive view, all children of the screen's canvas (so in canvas units):
    /// <list type="bullet">
    /// <item>a dark curved canvas the size of the whole screen, with a thin accent edge, so narrow or portrait
    /// videos sit on a solid screen instead of floating in the air,</item>
    /// <item>the video on the same curve, centred above the controls, and</item>
    /// <item>a dark see-through shell around the camera that dims the world.</item>
    /// </list>
    /// All three layers lie on one cylinder whose arc for the full screen width is the configured angle.
    /// Draw order does the trick: the scene renders, the shell darkens it (queue 2990), then the edge (2994), the
    /// dark canvas (2995) and the video (2996), and last the screen's own UI such as the controls (3000), all at
    /// full brightness and still hidden behind anything closer. The controls are flat while the video curves towards
    /// you, so at the ends the video can reach over them; drawing the UI last keeps the controls on top. Everything uses Sprites/Default, which every build
    /// includes: unlit, two-sided, alpha-blended and without depth writes.
    /// </summary>
    internal class ImmersiveVisuals {
        private const int Columns = 64;
        private const float ShellRadius = 0.6f;  // metres around the camera; nearer things aren't dimmed
        private const float EdgeWidth = 6f;      // canvas units of accent edge around the dark canvas

        private static readonly Color CanvasColor = new Color(0.02f, 0.025f, 0.035f, 0.97f);

        private readonly Shader shader;
        private readonly Layer edge, backing, video;

        private readonly GameObject shell;
        private readonly Material shellMaterial;
        private float darkness;

        private float builtScreenW, builtScreenH, builtVideoW, builtVideoH, builtAngle = -1f, builtOffset;

        /// <summary>One curved mesh with its own material.</summary>
        private class Layer {
            public GameObject Object;
            public Mesh Mesh;
            public Material Material;
        }

        public ImmersiveVisuals(Transform screen) {
            shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            if (shader == null) {
                throw new System.InvalidOperationException("neither Sprites/Default nor UI/Default is in this build");
            }

            var accent = KoboldKare.ModKit.Theme.Accent;
            edge = NewLayer(screen, "CurvedEdge", 2994, new Color(accent.r, accent.g, accent.b, 0.55f), 1f);
            backing = NewLayer(screen, "CurvedCanvas", 2995, CanvasColor, 0f);
            video = NewLayer(screen, "CurvedVideo", 2996, Color.white, -2f);

            // Darkening shell: its own object (not scaled with the screen), kept on the camera every frame.
            shell = new GameObject("MovieBlock_Darken");
            shell.AddComponent<MeshFilter>().sharedMesh = BuildSphere();
            var shellRenderer = shell.AddComponent<MeshRenderer>();
            shellRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            shellRenderer.receiveShadows = false;
            shellMaterial = new Material(shader) { renderQueue = 2990, color = new Color(0f, 0f, 0f, 0f) };
            shellRenderer.sharedMaterial = shellMaterial;
            shell.transform.localScale = Vector3.one * ShellRadius;
            shell.SetActive(false);
        }

        private Layer NewLayer(Transform parent, string name, int queue, Color color, float z) {
            var layer = new Layer { Object = new GameObject(name), Mesh = new Mesh { name = name } };
            layer.Object.transform.SetParent(parent, false);
            layer.Object.transform.localPosition = new Vector3(0f, 0f, z);
            layer.Mesh.MarkDynamic();
            layer.Object.AddComponent<MeshFilter>().sharedMesh = layer.Mesh;
            var renderer = layer.Object.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            layer.Material = new Material(shader) { renderQueue = queue, color = color };
            renderer.sharedMaterial = layer.Material;
            layer.Object.SetActive(false);
            return layer;
        }

        /// <summary>
        /// Shows or hides the curved screen. Sizes are canvas units: the dark canvas covers screenW x screenH (centred);
        /// the video is videoW x videoH, centred <paramref name="videoOffset"/> above the middle. <paramref name="angle"/>
        /// is how far the full screen width wraps around, in degrees (0 = flat). <paramref name="showVideo"/> false keeps
        /// the dark canvas but hides the picture (no video yet, or seen from behind).
        /// </summary>
        public void UpdateCurve(bool show, bool showVideo, Texture texture, float screenW, float screenH,
                                float videoW, float videoH, float videoOffset, float angle) {
            SetActive(edge, show);
            SetActive(backing, show);
            SetActive(video, show && showVideo && texture != null);
            if (!show) {
                return;
            }
            if (video.Material.mainTexture != texture) {
                video.Material.mainTexture = texture;
            }
            if (Changed(screenW, builtScreenW) || Changed(screenH, builtScreenH) || Changed(videoW, builtVideoW) ||
                Changed(videoH, builtVideoH) || Changed(angle, builtAngle) || Changed(videoOffset, builtOffset)) {
                builtScreenW = screenW;
                builtScreenH = screenH;
                builtVideoW = videoW;
                builtVideoH = videoH;
                builtAngle = angle;
                builtOffset = videoOffset;

                float theta = Mathf.Clamp(angle, 0f, 150f) * ((float)System.Math.PI / 180f);
                float radius = theta > 0.001f ? screenW / theta : 0f; // one cylinder for every layer
                BuildArc(edge.Mesh, screenW + EdgeWidth * 2f, screenH + EdgeWidth * 2f, 0f, radius);
                BuildArc(backing.Mesh, screenW, screenH, 0f, radius);
                BuildArc(video.Mesh, videoW, videoH, videoOffset, radius);
            }
        }

        /// <summary>Fades the world dimming towards <paramref name="target"/> (0-1) and keeps the shell on the camera.</summary>
        public void UpdateDarkness(Camera camera, float target) {
            darkness = Mathf.MoveTowards(darkness, Mathf.Clamp01(target), Time.unscaledDeltaTime * 2.5f);
            bool show = darkness > 0.005f && camera != null;
            if (shell.activeSelf != show) {
                shell.SetActive(show);
            }
            if (!show) {
                return;
            }
            shell.transform.position = camera.transform.position;
            // Keep the shell outside the near clip plane, or it would be cut away and not darken anything.
            shell.transform.localScale = Vector3.one * Mathf.Max(ShellRadius, camera.nearClipPlane * 2f + 0.05f);
            shellMaterial.color = new Color(0f, 0f, 0f, darkness);
        }

        public void Destroy() {
            foreach (var layer in new[] { edge, backing, video }) {
                if (layer == null) {
                    continue;
                }
                Object.Destroy(layer.Object);
                Object.Destroy(layer.Mesh);
                Object.Destroy(layer.Material);
            }
            if (shell != null) {
                Object.Destroy(shell.GetComponent<MeshFilter>()?.sharedMesh);
                Object.Destroy(shell);
            }
            Object.Destroy(shellMaterial);
        }

        private static bool Changed(float a, float b) => Mathf.Abs(a - b) > 0.05f;

        private static void SetActive(Layer layer, bool active) {
            if (layer.Object.activeSelf != active) {
                layer.Object.SetActive(active);
            }
        }

        /// <summary>
        /// A width x height strip on a cylinder of <paramref name="radius"/> (0 = flat), centred left-right, its middle
        /// <paramref name="yOffset"/> above the canvas centre. Edges come towards the viewer (canvas -Z).
        /// </summary>
        private static void BuildArc(Mesh mesh, float width, float height, float yOffset, float radius) {
            var vertices = new Vector3[(Columns + 1) * 2];
            var uvs = new Vector2[vertices.Length];
            var colors = new Color[vertices.Length];
            var triangles = new int[Columns * 6];
            float halfAngle = radius > 0f ? width / (2f * radius) : 0f;
            for (int i = 0; i <= Columns; i++) {
                float t = i / (float)Columns;
                float x, z;
                if (radius > 0f) {
                    float a = (t * 2f - 1f) * halfAngle;
                    x = radius * Mathf.Sin(a);
                    z = -(radius - radius * Mathf.Cos(a));
                } else {
                    x = (t - 0.5f) * width;
                    z = 0f;
                }
                vertices[i * 2] = new Vector3(x, yOffset - height / 2f, z);
                vertices[i * 2 + 1] = new Vector3(x, yOffset + height / 2f, z);
                uvs[i * 2] = new Vector2(t, 0f);
                uvs[i * 2 + 1] = new Vector2(t, 1f);
                colors[i * 2] = Color.white;
                colors[i * 2 + 1] = Color.white;
            }
            for (int i = 0; i < Columns; i++) {
                int v = i * 2, k = i * 6;
                triangles[k] = v;
                triangles[k + 1] = v + 1;
                triangles[k + 2] = v + 2;
                triangles[k + 3] = v + 2;
                triangles[k + 4] = v + 1;
                triangles[k + 5] = v + 3;
            }
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
        }

        /// <summary>A unit UV sphere with white vertex colours (Sprites/Default multiplies by them).</summary>
        private static Mesh BuildSphere() {
            const int rings = 12, segments = 24;
            var vertices = new Vector3[(rings + 1) * (segments + 1)];
            var colors = new Color[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            for (int r = 0; r <= rings; r++) {
                float phi = (float)System.Math.PI * r / rings;
                for (int s = 0; s <= segments; s++) {
                    float lambda = 2f * (float)System.Math.PI * s / segments;
                    int i = r * (segments + 1) + s;
                    vertices[i] = new Vector3(Mathf.Sin(phi) * Mathf.Cos(lambda), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(lambda));
                    colors[i] = Color.white;
                    uvs[i] = new Vector2(s / (float)segments, r / (float)rings);
                }
            }
            var triangles = new int[rings * segments * 6];
            int k = 0;
            for (int r = 0; r < rings; r++) {
                for (int s = 0; s < segments; s++) {
                    int a = r * (segments + 1) + s, b = a + segments + 1;
                    triangles[k++] = a;
                    triangles[k++] = b;
                    triangles[k++] = a + 1;
                    triangles[k++] = a + 1;
                    triangles[k++] = b;
                    triangles[k++] = b + 1;
                }
            }
            var mesh = new Mesh { name = "MovieBlockDarkenShell" };
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            // Always draw it: the camera sits inside, so normal bounds-based culling would skip it.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
            return mesh;
        }
    }
}
