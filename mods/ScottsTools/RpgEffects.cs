using System.Collections.Generic;
using System.Reflection;
using NetStack.Quantization;
using NetStack.Serialization;
using Photon.Pun;
using UnityEngine;

namespace ScottsTools {
    /// <summary>Launch sound, the (harmless) blast, and the cum burst.</summary>
    internal static class RpgEffects {
        private static GameObject explosionTemplate;
        private static Material scorchDecal;
        private static bool explosionLookupFailed;
        private static string fluidProjectileKey;
        private static readonly Collider[] Overlap = new Collider[128];

        public static void PlayLaunch(Vector3 position) {
            if (RpgAssets.Whoosh != null && GameManager.instance != null) {
                GameManager.instance.SpawnAudioClipInWorld(RpgAssets.Whoosh, position, 0.9f);
            }
        }

        /// <summary>
        /// Runs on every client. Visuals everywhere; pushes only what this client owns (its own kobold,
        /// NPCs/items it hosts), since physics on someone else's object gets overwritten by their sync.
        /// No damage: nothing is hurt or destroyed.
        /// </summary>
        /// <param name="hitShooter">Push the shooter too (never ragdolled, so they keep the launcher: rocket jumps).</param>
        public static void Explode(Vector3 position, bool isCum, float radius, float force, bool ragdoll, int shooterViewID, bool hitShooter) {
            if (!isCum) {
                SpawnFireball(position, radius);
            }
            Push(position, radius, force, ragdoll, shooterViewID, hitShooter);
        }

        private static void Push(Vector3 position, float radius, float force, bool ragdoll, int shooterViewID, bool hitShooter) {
            if (force <= 0f || radius <= 0f) {
                return;
            }
            var pushedKobolds = new HashSet<Kobold>();
            foreach (Kobold k in Object.FindObjectsOfType<Kobold>()) {
                if (k == null || k.photonView == null || !k.photonView.IsMine) {
                    continue;
                }
                bool isShooter = shooterViewID != 0 && k.photonView.ViewID == shooterViewID;
                if (isShooter && !hitShooter) {
                    continue;
                }
                Vector3 center = k.hip != null ? k.hip.position : k.transform.position;
                if (Vector3.Distance(center, position) > radius) {
                    continue;
                }
                pushedKobolds.Add(k);
                // Same recipe as the game's bomb, minus the damage.
                Ragdoller ragdoller = k.GetRagdoller();
                if (ragdoller != null && !isShooter) {
                    foreach (Rigidbody r in ragdoller.GetRagdollBodies()) {
                        r.AddExplosionForce(force, position, radius, 0.6f);
                    }
                }
                if (k.body != null) {
                    k.body.AddExplosionForce(force, position, radius, 0.6f);
                }
                if (ragdoll && !isShooter) {
                    k.StartCoroutine(k.ThrowRoutine());
                }
            }

            int count = Physics.OverlapSphereNonAlloc(position, radius, Overlap, ~0, QueryTriggerInteraction.Ignore);
            var pushedBodies = new HashSet<Rigidbody>();
            for (int i = 0; i < count; i++) {
                Rigidbody r = Overlap[i].attachedRigidbody;
                if (r == null || r.isKinematic || !pushedBodies.Add(r)) {
                    continue;
                }
                Kobold owner = r.GetComponentInParent<Kobold>();
                if (owner != null) {
                    continue; // handled above
                }
                PhotonView view = r.GetComponentInParent<PhotonView>();
                if (view != null && !view.IsMine) {
                    continue;
                }
                r.AddExplosionForce(force, position, radius, 0.6f);
            }
        }

        /// <summary>The game's bomb explosion (fireball, sound, scorch mark), without its damage/physics.</summary>
        private static void SpawnFireball(Vector3 position, float radius) {
            if (!TryGetExplosionTemplate()) {
                return;
            }
            GameObject fx = Object.Instantiate(explosionTemplate, position, Quaternion.identity);
            fx.name = "ScottsRPG Explosion";
            Object.Destroy(fx, 6f);
            if (scorchDecal != null && GameManager.instance != null) {
                try {
                    SkinnedMeshDecals.PaintDecal.RenderDecalInBox(Vector3.one * Mathf.Clamp(radius * 0.8f, 1f, 8f), position, scorchDecal,
                        Quaternion.FromToRotation(Vector3.forward, Vector3.down), GameManager.instance.decalHitMask);
                } catch (System.Exception e) {
                    ScottsToolsPlugin.Log.LogWarning("Scorch decal failed: " + e.Message);
                    scorchDecal = null;
                }
            }
        }

        private static bool TryGetExplosionTemplate() {
            if (explosionTemplate != null) {
                return true;
            }
            if (explosionLookupFailed || !(PhotonNetwork.PrefabPool is DefaultPool pool) || RpgPrefabs.Holder == null) {
                return false;
            }
            GameObject prefab = null;
            if (!pool.ResourceCache.TryGetValue("ExplosionDontSave", out prefab) || prefab == null) {
                foreach (var pair in pool.ResourceCache) {
                    if (pair.Value != null && pair.Value.GetComponentInChildren<Explosion>(true) != null) {
                        prefab = pair.Value;
                        break;
                    }
                }
            }
            if (prefab == null) {
                explosionLookupFailed = true;
                ScottsToolsPlugin.Log.LogWarning("No explosion prefab found; RPG blasts will have no fireball.");
                return false;
            }

            // Copy it under the inactive holder (so nothing wakes up), then strip the networked/damage parts.
            GameObject copy = Object.Instantiate(prefab, RpgPrefabs.Holder);
            foreach (Explosion ex in copy.GetComponentsInChildren<Explosion>(true)) {
                FieldInfo field = typeof(Explosion).GetField("scorchDecal", BindingFlags.Instance | BindingFlags.NonPublic);
                scorchDecal = field?.GetValue(ex) as Material;
                Object.DestroyImmediate(ex);
            }
            foreach (MonoBehaviourPun mb in copy.GetComponentsInChildren<MonoBehaviourPun>(true)) {
                Object.DestroyImmediate(mb);
            }
            foreach (PhotonView view in copy.GetComponentsInChildren<PhotonView>(true)) {
                Object.DestroyImmediate(view);
            }
            explosionTemplate = copy;
            return true;
        }

        /// <summary>Shooter only: throw networked blobs of cum (the bucket's fluid projectile) everywhere.</summary>
        public static void CumBurst(Vector3 position, Vector3 normal, Kobold shooter) {
            if (!ReagentDatabase.TryGetAsset("Cum", out ScriptableReagent cum)) {
                ScottsToolsPlugin.Log.LogWarning("No Cum reagent found.");
                return;
            }
            string key = FluidProjectileKey();
            if (key == null) {
                ScottsToolsPlugin.Log.LogWarning("No fluid projectile prefab found.");
                return;
            }
            if (normal.sqrMagnitude < 0.01f) {
                normal = Vector3.up;
            }
            normal.Normalize();
            KoboldGenes genes = shooter != null ? shooter.GetGenes() : null;
            int blobs = Mathf.Clamp(ScottsToolsPlugin.CumBlobs.Value, 1, 64);
            float volume = ScottsToolsPlugin.CumBlobVolume.Value;
            Vector3 spawn = position + normal * 0.25f;
            for (int i = 0; i < blobs; i++) {
                Vector3 dir = RandomDirection();
                if (Vector3.Dot(dir, normal) < 0f) {
                    dir -= 2f * Vector3.Dot(dir, normal) * normal; // Vector3.Reflect is stripped
                }
                dir = (dir + normal * 0.4f + Vector3.up * 0.35f).normalized;
                Vector3 velocity = dir * Random.Range(4f, 10f);

                var contents = new ReagentContents();
                contents.AddMix(cum.GetReagent(volume));
                var buffer = new BitBuffer(16);
                buffer.AddReagentContents(contents);
                buffer.AddUShort(HalfPrecision.Quantize(velocity.x));
                buffer.AddUShort(HalfPrecision.Quantize(velocity.y));
                buffer.AddUShort(HalfPrecision.Quantize(velocity.z));
                buffer.AddKoboldGenes(genes);
                PhotonNetwork.Instantiate(key, spawn, Quaternion.LookRotation(velocity), 0, new object[] { buffer });
            }
        }

        private static string FluidProjectileKey() {
            if (fluidProjectileKey != null) {
                return fluidProjectileKey;
            }
            if (!(PhotonNetwork.PrefabPool is DefaultPool pool)) {
                return null;
            }
            if (pool.ResourceCache.TryGetValue("FluidProjectile", out GameObject p) && p != null) {
                return fluidProjectileKey = "FluidProjectile";
            }
            foreach (var pair in pool.ResourceCache) {
                if (pair.Value != null && pair.Value.GetComponent<Projectile>() != null) {
                    return fluidProjectileKey = pair.Key;
                }
            }
            return null;
        }

        // Random.onUnitSphere may be stripped from this build; roll our own.
        private static Vector3 RandomDirection() {
            float z = Random.Range(-1f, 1f);
            float a = Random.Range(0f, 2f * 3.14159265f);
            float r = Mathf.Sqrt(1f - z * z);
            return new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
        }
    }
}
