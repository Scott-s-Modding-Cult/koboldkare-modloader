using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace ScottsTools {
    /// <summary>
    /// A fading smoke trail on a LineRenderer (this game build has TrailRenderer stripped).
    /// Records world positions while emitting; once detached it fades out and deletes itself.
    /// </summary>
    internal class SmokeTrail : MonoBehaviour {
        public float life = 0.8f;
        public bool emitting = true;

        private struct Point {
            public Vector3 position;
            public float time;
        }

        private readonly List<Point> points = new List<Point>();
        private Vector3[] buffer = new Vector3[64];
        private LineRenderer line;
        private Material material;
        private float baseAlpha;
        private float releasedAt = -1f;

        public static SmokeTrail Create(Transform parent, Vector3 localPosition, float life, Color head, Color tail) {
            var go = new GameObject("Trail");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = RpgAssets.SmokeMaterial;
            line.useWorldSpace = true;
            line.positionCount = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            // Index 0 is the newest point (at the rocket), the end is the oldest.
            line.widthCurve = new AnimationCurve(new Keyframe(0f, 0.08f), new Keyframe(0.3f, 0.3f), new Keyframe(1f, 0.55f));
            // colorGradient is stripped in this build: tint a per-trail material instead, fade it on release.
            var material = new Material(RpgAssets.SmokeMaterial);
            tail.a = 0.65f;
            material.color = Color.Lerp(head, tail, 0.6f);
            line.sharedMaterial = material;
            var trail = go.AddComponent<SmokeTrail>();
            trail.line = line;
            trail.life = life;
            trail.material = material;
            trail.baseAlpha = material.color.a;
            return trail;
        }

        /// <summary>Stop emitting, leave the parent, and fade away.</summary>
        public void Release() {
            emitting = false;
            releasedAt = Time.time;
            transform.SetParent(null, true);
        }

        private void LateUpdate() {
            float now = Time.time;
            if (emitting) {
                Vector3 p = transform.position;
                if (points.Count == 0 || (points[0].position - p).sqrMagnitude > 0.01f) {
                    points.Insert(0, new Point { position = p, time = now });
                } else {
                    points[0] = new Point { position = p, time = points[0].time };
                }
            }
            while (points.Count > 0 && now - points[points.Count - 1].time > life) {
                points.RemoveAt(points.Count - 1);
            }
            if (!emitting && points.Count < 2) {
                if (material != null) {
                    Destroy(material);
                }
                Destroy(gameObject);
                return;
            }
            if (buffer.Length < points.Count) {
                buffer = new Vector3[points.Count * 2];
            }
            for (int i = 0; i < points.Count; i++) {
                buffer[i] = points[i].position;
            }
            line.positionCount = points.Count;
            // Only the NativeArray overload of SetPositions survived stripping.
            var native = new NativeArray<Vector3>(points.Count, Allocator.Temp);
            for (int i = 0; i < points.Count; i++) {
                native[i] = buffer[i];
            }
            line.SetPositions(native);
            native.Dispose();
            if (releasedAt >= 0f && material != null) {
                Color c = material.color;
                c.a = baseAlpha * Mathf.Clamp01(1f - (now - releasedAt) / life);
                material.color = c;
            }
        }
    }
}
