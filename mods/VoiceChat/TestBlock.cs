using System;
using KoboldKare.ModKit;
using UnityEngine;

namespace VoiceChat {
    /// <summary>
    /// A local-only cube that plays your own mic back in 3D, so you can test voice chat alone: talk,
    /// walk around it and away from it, and hear the direction and distance falloff other players
    /// would hear. It pulses while audio plays through it. Nobody else sees it, and a map load removes it.
    /// </summary>
    internal static class TestBlock {
        private static GameObject block;
        private static Material material;
        private static float level;
        private static float spin;

        public static Transform Transform => block != null ? block.transform : null;
        public static bool Exists => block != null;

        /// <summary>Places the block in front of you (or moves it there). Returns false outside a map.</summary>
        public static bool Place() {
            var kobold = ModKit.LocalKobold;
            if (!ModKit.InLevel || kobold == null) {
                return false;
            }
            if (block == null) {
                Create();
            }

            Transform body = kobold.hip != null ? kobold.hip : kobold.transform;
            var camera = Camera.main;
            Vector3 forward = camera != null ? camera.transform.forward : kobold.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) {
                forward = Vector3.forward;
            }
            forward.Normalize();
            block.transform.position = body.position + forward * 2.5f + Vector3.up * 0.4f;
            return true;
        }

        public static void Remove() {
            if (block != null) {
                UnityEngine.Object.Destroy(block);
            }
            block = null;
        }

        /// <summary>Main thread, every frame: pulse with the audio level.</summary>
        public static void Update(float peak) {
            if (block == null) {
                return;
            }
            level = Math.Max(peak, level - Time.unscaledDeltaTime * 3f);
            float l = Math.Min(1f, level * 2.5f);
            spin += Time.unscaledDeltaTime * (20f + 200f * l);
            block.transform.rotation = Quaternion.Euler(0f, spin, 0f);
            block.transform.localScale = Vector3.one * (0.45f * (1f + 0.5f * l));
            if (material != null) {
                var idle = new Color(0.10f, 0.45f, 0.55f, 1f);
                var loud = new Color(0.45f, 1f, 0.95f, 1f);
                var color = Color.Lerp(idle, loud, l);
                material.color = color;
                material.SetColor("_BaseColor", color);
            }
        }

        private static void Create() {
            block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = "VoiceChat_TestBlock";
            // Visual marker only: no collider, so it can't be grabbed, pushed or block anyone.
            var collider = block.GetComponent<Collider>();
            if (collider != null) {
                UnityEngine.Object.Destroy(collider);
            }
            var renderer = block.GetComponent<Renderer>();
            var shader = FindShader();
            if (renderer != null && shader != null) {
                material = new Material(shader);
                renderer.sharedMaterial = material;
            } else {
                material = renderer != null ? renderer.material : null;
            }
            level = 0f;
        }

        /// <summary>The game uses URP; take the first unlit-ish shader that survived the build.</summary>
        private static Shader FindShader() {
            foreach (string name in new[] {
                         "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Simple Lit",
                         "Universal Render Pipeline/Lit", "Sprites/Default", "UI/Default",
                     }) {
                var shader = Shader.Find(name);
                if (shader != null) {
                    return shader;
                }
            }
            return null;
        }
    }
}
