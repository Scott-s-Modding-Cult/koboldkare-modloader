using System.Collections.Generic;
using UnityEngine;

namespace ScottsTools {
    /// <summary>
    /// A flying rocket. Purely local: every client flies its own copy from the same launch RPC.
    /// Only the shooter's copy (authority) detonates; the others vanish when the explode RPC arrives.
    /// </summary>
    internal class Rocket : MonoBehaviour {
        private const float Radius = 0.07f;
        private const float Lifetime = 6f;
        private const float Gravity = 1.5f;

        private static readonly Dictionary<int, Rocket> Live = new Dictionary<int, Rocket>();
        private static readonly RaycastHit[] Hits = new RaycastHit[32];

        public int Id { get; private set; }
        public int ShooterViewID { get; private set; }
        public Kobold Shooter { get; private set; }

        private RpgWeapon launcher;
        private bool authority;
        private bool isCum;
        private Vector3 velocity;
        private float dieAt;
        private bool done;
        private readonly HashSet<Collider> ignore = new HashSet<Collider>();
        private SmokeTrail trail;
        private int mask;

        public static void Spawn(RpgWeapon launcher, Vector3 origin, Vector3 direction, float speed, Kobold shooter,
                                 int shooterViewID, int id, bool authority) {
            Vector3 up = Vector3.ProjectOnPlane(launcher.transform.up, direction);
            if (up.sqrMagnitude < 0.001f) {
                up = Vector3.up;
            }
            Quaternion rotation = Quaternion.LookRotation(direction, up);
            // Root sits at the warhead's tip; origin is where the warhead's back sits in the tube.
            float length = RpgAssets.TipZ - RpgAssets.MuzzleZ;
            var go = new GameObject("ScottsRocket");
            go.transform.SetPositionAndRotation(origin + direction * length, rotation);

            var mesh = new GameObject("Mesh").transform;
            mesh.SetParent(go.transform, false);
            mesh.localPosition = new Vector3(0f, 0f, -RpgAssets.TipZ);
            mesh.gameObject.AddComponent<MeshFilter>().sharedMesh = RpgAssets.Warhead;
            mesh.gameObject.AddComponent<MeshRenderer>().sharedMaterial =
                launcher.isCum ? RpgAssets.CumWarheadMaterial : RpgAssets.WarheadMaterial;

            var rocket = go.AddComponent<Rocket>();
            rocket.Id = id;
            rocket.launcher = launcher;
            rocket.authority = authority;
            rocket.isCum = launcher.isCum;
            rocket.Shooter = shooter;
            rocket.ShooterViewID = shooterViewID;
            rocket.velocity = direction * speed;
            rocket.dieAt = Time.time + Lifetime;
            rocket.mask = RpgWeapon.RocketMask();
            foreach (Collider c in launcher.GetComponentsInChildren<Collider>()) {
                rocket.ignore.Add(c);
            }
            if (shooter != null) {
                foreach (Collider c in shooter.GetComponentsInChildren<Collider>()) {
                    rocket.ignore.Add(c);
                }
            }
            rocket.MakeTrail(length);

            if (Live.TryGetValue(id, out Rocket old) && old != null) {
                old.Finish();
            }
            Live[id] = rocket;
        }

        /// <summary>The explode RPC arrived: drop this client's copy.</summary>
        public static void Remove(int id) {
            if (Live.TryGetValue(id, out Rocket r) && r != null) {
                r.Finish();
            }
            Live.Remove(id);
        }

        private void MakeTrail(float length) {
            Color head = isCum ? new Color(1f, 1f, 0.97f) : new Color(1f, 0.75f, 0.35f);
            Color tail = isCum ? new Color(0.95f, 0.93f, 0.88f) : new Color(0.55f, 0.55f, 0.55f);
            trail = SmokeTrail.Create(transform, new Vector3(0f, 0f, -length), isCum ? 0.5f : 0.8f, head, tail);
        }

        private void Update() {
            if (done) {
                return;
            }
            float dt = Time.deltaTime;
            velocity += Vector3.down * Gravity * dt;
            float distance = velocity.magnitude * dt;
            Vector3 dir = velocity.normalized;

            int hits = Physics.SphereCastNonAlloc(transform.position, Radius, dir, Hits, distance, mask, QueryTriggerInteraction.Ignore);
            int closest = -1;
            float best = float.MaxValue;
            for (int i = 0; i < hits; i++) {
                RaycastHit h = Hits[i];
                if (ignore.Contains(h.collider) || h.point == Vector3.zero) {
                    continue;
                }
                if (h.distance < best) {
                    best = h.distance;
                    closest = i;
                }
            }

            if (closest >= 0) {
                RaycastHit h = Hits[closest];
                transform.position = h.point;
                Detonate(h.point + h.normal * 0.15f, h.normal);
                return;
            }

            transform.position += velocity * dt;
            transform.rotation = Quaternion.LookRotation(dir, transform.up);
            if (Time.time > dieAt) {
                Detonate(transform.position, -dir);
            }
        }

        private void Detonate(Vector3 position, Vector3 normal) {
            if (authority) {
                if (launcher != null && launcher.photonView != null) {
                    launcher.RocketDetonated(this, position, normal);
                } else {
                    // Launcher was deleted mid-flight: at least blow up locally.
                    float force = ScottsToolsPlugin.KnockbackForce.Value * (isCum ? ScottsToolsPlugin.CumKnockbackScale.Value : 1f);
                    RpgEffects.Explode(position, isCum, ScottsToolsPlugin.BlastRadius.Value, force, ScottsToolsPlugin.Ragdoll.Value,
                        ShooterViewID, ScottsToolsPlugin.SelfKnockback.Value);
                    if (isCum) {
                        RpgEffects.CumBurst(position, normal, Shooter);
                    }
                }
                Live.Remove(Id);
            }
            // Non-authority copies wait (hidden) for the explode RPC, which removes them.
            Finish();
        }

        private void Finish() {
            done = true;
            if (trail != null) {
                // Let the smoke fade out on its own.
                trail.Release();
                trail = null;
            }
            if (this != null) {
                Destroy(gameObject);
            }
        }
    }
}
