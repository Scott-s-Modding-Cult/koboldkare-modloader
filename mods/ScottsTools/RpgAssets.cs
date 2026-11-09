using System;
using System.IO;
using UnityEngine;

namespace ScottsTools {
    /// <summary>
    /// The RPG model and materials, built once from resources embedded in this DLL.
    /// Launcher space: +Z = where the rocket points, +Y = up (sights), origin on the bore line.
    /// </summary>
    internal static class RpgAssets {
        // Source model (Simplified RPG) is 9.6 units long along +X with the warhead at +X.
        private const float Scale = 0.135f;      // -> about 1.3 m long
        private const float BoreY = 0.37f;       // OBJ height of the tube's center line
        private const float WarheadStartX = 2.1f; // faces in front of this belong to the rocket
        private const float TipX = 4.81f;
        private const float RearX = -4.81f;

        public static float MuzzleZ => WarheadStartX * Scale;
        public static float TipZ => TipX * Scale;
        public static float Length => (TipX - RearX) * Scale;
        /// <summary>OBJ y converted to launcher space.</summary>
        public static float Y(float objY) => (objY - BoreY) * Scale;
        public static float Z(float objX) => objX * Scale;

        public static Mesh Launcher { get; private set; }
        public static Mesh Warhead { get; private set; }
        public static Material LauncherMaterial { get; private set; }
        public static Material CumLauncherMaterial { get; private set; }
        public static Material WarheadMaterial { get; private set; }
        public static Material CumWarheadMaterial { get; private set; }
        public static Material SmokeMaterial { get; private set; }
        public static AudioClip Whoosh { get; private set; }

        private static bool loaded;

        public static void Load() {
            if (loaded) {
                return;
            }
            // OBJ (x, y, z) -> launcher (z, y - bore, x). Swapping two axes is the mirror ObjLoader expects.
            Func<Vector3, Vector3> toLauncher = p => new Vector3(p.z * Scale, (p.y - BoreY) * Scale, p.x * Scale);
            Mesh[] parts;
            using (Stream s = Open("rpg.obj")) {
                parts = ObjLoader.Load(s, 2, toLauncher, c => c.x >= WarheadStartX ? 1 : 0);
            }
            Launcher = parts[0];
            Launcher.name = "ScottsRPG_Launcher";
            Warhead = parts[1];
            Warhead.name = "ScottsRPG_Warhead";

            Texture2D albedo = LoadTexture("rpg_albedo.jpg", linear: false);
            Texture2D normal = LoadTexture("rpg_normal.jpg", linear: true);
            Texture2D metal = LoadTexture("rpg_metal_smooth.png", linear: true);

            LauncherMaterial = Lit("ScottsRPG", albedo, normal, metal, Color.white);
            WarheadMaterial = Lit("ScottsRPG_Rocket", albedo, normal, metal, Color.white);
            // The cum launcher is tinted pink so the two are easy to tell apart on the floor.
            CumLauncherMaterial = Lit("ScottsCumRPG", albedo, normal, metal, new Color(1f, 0.72f, 0.86f));
            // Its rocket is a glossy cream-white warhead.
            CumWarheadMaterial = Lit("ScottsCumRPG_Rocket", null, normal, null, new Color(0.97f, 0.94f, 0.88f));
            CumWarheadMaterial.SetFloat("_Smoothness", 0.85f);
            CumWarheadMaterial.SetFloat("_Metallic", 0f);

            SmokeMaterial = MakeSmokeMaterial();
            Whoosh = MakeWhoosh();
            loaded = true;
        }

        private static Stream Open(string name) {
            Stream s = typeof(RpgAssets).Assembly.GetManifestResourceStream("ScottsTools." + name);
            if (s == null) {
                throw new FileNotFoundException("Embedded resource missing: " + name);
            }
            return s;
        }

        private static Texture2D LoadTexture(string name, bool linear) {
            byte[] bytes;
            using (Stream s = Open(name))
            using (var ms = new MemoryStream()) {
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = name };
            if (!tex.LoadImage(bytes, false)) {
                throw new InvalidDataException("Couldn't decode " + name);
            }
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.Apply(true, true); // upload with mipmaps, free the CPU copy
            return tex;
        }

        private static Shader FindLitShader() {
            Shader s = Shader.Find("Universal Render Pipeline/Lit");
            if (s != null) {
                return s;
            }
            // Fall back to whatever lit shader the game's own props use.
            foreach (Renderer r in Resources.FindObjectsOfTypeAll<MeshRenderer>()) {
                foreach (Material m in r.sharedMaterials) {
                    if (m != null && m.shader != null && m.HasProperty("_BaseMap") && m.HasProperty("_BumpMap")) {
                        return m.shader;
                    }
                }
            }
            return Shader.Find("Standard");
        }

        private static Material Lit(string name, Texture2D albedo, Texture2D normal, Texture2D metalSmooth, Color tint) {
            var m = new Material(FindLitShader()) { name = name };
            m.SetColor("_BaseColor", tint);
            m.SetColor("_Color", tint);
            if (albedo != null) {
                m.SetTexture("_BaseMap", albedo);
                m.SetTexture("_MainTex", albedo);
            }
            if (normal != null) {
                m.SetTexture("_BumpMap", normal);
                m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");
            }
            if (metalSmooth != null) {
                // R = metalness, A = smoothness.
                m.SetTexture("_MetallicGlossMap", metalSmooth);
                m.EnableKeyword("_METALLICSPECGLOSSMAP");
                m.SetFloat("_Smoothness", 0.9f);
                m.SetFloat("_GlossMapScale", 0.9f);
            } else {
                m.SetFloat("_Smoothness", 0.5f);
            }
            m.SetFloat("_Metallic", 0f);
            // Normals come from the model, so double-sided is a safety net against flipped faces.
            m.SetFloat("_Cull", 0f);
            return m;
        }

        private static Material MakeSmokeMaterial() {
            Shader s = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Unlit/Color");
            return new Material(s) { name = "ScottsRPG_Smoke" };
        }

        /// <summary>A short synthesized "fwoosh" with a low thump for the launch.</summary>
        private static AudioClip MakeWhoosh() {
            const int rate = 44100;
            const float length = 0.9f;
            int samples = (int)(rate * length);
            var data = new float[samples];
            var rng = new System.Random(1234);
            float lp = 0f;
            for (int i = 0; i < samples; i++) {
                float t = i / (float)rate;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                // Low-pass that opens up then closes: a rushing sweep.
                float cutoff = Mathf.Lerp(0.35f, 0.04f, Mathf.Clamp01(t / length));
                lp += (noise - lp) * cutoff;
                float env = Mathf.Min(1f, t / 0.015f) * Mathf.Exp(-t * 4.2f);
                float thump = Mathf.Sin(2f * 3.14159265f * Mathf.Lerp(90f, 40f, t * 4f) * t) * Mathf.Exp(-t * 14f);
                data[i] = Mathf.Clamp(lp * env * 1.6f + thump * 0.8f, -1f, 1f);
            }
            AudioClip clip = AudioClip.Create("ScottsRPG_Whoosh", samples, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
