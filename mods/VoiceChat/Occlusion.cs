using System;
using KoboldKare.ModKit;
using Photon.Pun;
using UnityEngine;

namespace VoiceChat {
    /// <summary>
    /// Works out how much map geometry sits between your ears (the active AudioListener) and a voice.
    /// Four rays go from the listener to points around the speaker: the share that's blocked says how
    /// hidden the voice is (partly, around a corner, or fully), and the number of separate colliders on
    /// the centre ray says how many walls it goes through. Only static world geometry counts: kobolds,
    /// triggers and loose physics props don't block sound.
    /// </summary>
    internal static class Occlusion {
        private static readonly RaycastHit[] Hits = new RaycastHit[16];
        private static readonly Vector3[] Targets = new Vector3[4];
        private static int mask;
        private static bool maskReady;
        private static AudioListener listener;
        private static float nextListenerSearch;

        public static void Reset() {
            maskReady = false;
            listener = null;
        }

        /// <summary>Where you hear from, or null if there's no active listener.</summary>
        public static Transform Listener {
            get {
                if ((listener == null || !listener.isActiveAndEnabled) && Time.unscaledTime >= nextListenerSearch) {
                    nextListenerSearch = Time.unscaledTime + 1f;
                    listener = FindListener();
                }
                return listener != null && listener.isActiveAndEnabled ? listener.transform : null;
            }
        }

        /// <param name="blocked">Share of rays blocked, 0 (clear line of sight) to 1 (fully hidden).</param>
        /// <param name="walls">Separate colliders crossed by the centre ray (0 to 3).</param>
        public static bool Measure(Vector3 target, out float blocked, out int walls) {
            blocked = 0f;
            walls = 0;
            var ears = Listener;
            if (ears == null) {
                return false;
            }
            EnsureMask();

            Vector3 from = ears.position;
            Vector3 toTarget = target - from;
            float distance = toTarget.magnitude;
            if (distance < 0.5f) {
                return true;
            }
            Vector3 dir = toTarget / distance;
            Vector3 side = Vector3.Cross(dir, Vector3.up);
            if (side.sqrMagnitude < 0.01f) {
                side = Vector3.right;
            }
            side.Normalize();

            Targets[0] = target;
            Targets[1] = target + Vector3.up * 0.45f;
            Targets[2] = target + side * 0.5f;
            Targets[3] = target - side * 0.5f;

            int blockedRays = 0;
            for (int i = 0; i < Targets.Length; i++) {
                int count = CountBlockers(from, Targets[i]);
                if (count > 0) {
                    blockedRays++;
                }
                if (i == 0) {
                    walls = Math.Min(count, 3);
                }
            }
            blocked = blockedRays / (float)Targets.Length;
            if (walls == 0 && blockedRays > 0) {
                walls = 1; // only the edge rays are blocked: around a corner
            }
            return true;
        }

        private static int CountBlockers(Vector3 from, Vector3 to) {
            Vector3 delta = to - from;
            float length = delta.magnitude;
            if (length < 0.01f) {
                return 0;
            }
            int n = Physics.RaycastNonAlloc(from, delta / length, Hits, length - 0.05f, mask, QueryTriggerInteraction.Ignore);
            int count = 0;
            for (int i = 0; i < n; i++) {
                var collider = Hits[i].collider;
                if (collider == null) {
                    continue;
                }
                var body = collider.attachedRigidbody;
                if (body != null && !body.isKinematic) {
                    continue; // loose props (buckets, fruit, ...) don't muffle
                }
                if (collider.GetComponentInParent<Kobold>() != null) {
                    continue; // nor do other kobolds
                }
                count++;
            }
            return count;
        }

        private static void EnsureMask() {
            if (maskReady) {
                return;
            }
            int world = LayerMask.GetMask("World", "Default", "Terrain");
            try {
                if (GameManager.instance != null) {
                    world |= GameManager.instance.walkableGroundMask;
                }
            } catch {
                // keep the named layers
            }
            int players = LayerMask.GetMask("Player", "LocalPlayer", "PlayerNocollide", "Hitbox", "MirrorReflection",
                "UsablePickups", "UserGrabber", "AnimationSet");
            mask = world & ~players;
            maskReady = true;
        }

        private static AudioListener FindListener() {
            // The game keeps the listener on the local kobold (see VolumetricSound); fall back to any active one.
            if (PhotonNetwork.LocalPlayer?.TagObject is Kobold kobold && kobold != null) {
                foreach (var candidate in kobold.GetComponentsInChildren<AudioListener>()) {
                    if (candidate.isActiveAndEnabled) {
                        return candidate;
                    }
                }
            }
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<AudioListener>()) {
                if (candidate.isActiveAndEnabled) {
                    return candidate;
                }
            }
            return null;
        }
    }
}
