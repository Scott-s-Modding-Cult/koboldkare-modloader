using System.Collections;
using Photon.Pun;
using UnityEngine;

namespace ScottsTools {
    /// <summary>
    /// The held launcher. Fire (the game's "use" while holding it) goes to the holder's client, which
    /// aims and broadcasts a launch; everyone flies a rocket, but only the shooter's rocket decides
    /// where it blows up, then tells everyone to explode there.
    /// </summary>
    public class RpgWeapon : GenericWeapon, IGrabbable {
        // Set on the template; Instantiate copies them and remaps the child references.
        public bool isCum;
        public Transform barrel;
        public Transform grab;
        public GameObject loadedRocket;

        private float readyAt;
        private Coroutine reloadRoutine;
        private static readonly RaycastHit[] AimHits = new RaycastHit[32];

        private void Awake() {
            // Instantiate copies these from the template; look them up in case it didn't.
            if (barrel == null) barrel = transform.Find("Barrel");
            if (grab == null) grab = transform.Find("Grab");
            if (loadedRocket == null) loadedRocket = transform.Find("LoadedRocket")?.gameObject;
            if (name.StartsWith(RpgPrefabs.CumKey)) isCum = true;
        }

        public override Transform GetWeaponBarrelTransform() => barrel;

        // Off to the right and a little low, so the tube doesn't cover the screen.
        public override Vector3 GetWeaponHoldPosition() => new Vector3(0.3f, -0.22f, -0.1f);

        [PunRPC]
        protected override void OnFireRPC(int playerID) {
            base.OnFireRPC(playerID);
            if (!photonView.IsMine || Time.time < readyAt) {
                return;
            }
            readyAt = Time.time + ScottsToolsPlugin.ReloadSeconds.Value;

            Kobold shooter = PhotonNetwork.GetPhotonView(playerID)?.GetComponentInParent<Kobold>();
            Vector3 origin = barrel.position;
            Vector3 direction = Aim(shooter, origin);
            int rocketId = Random.Range(1, int.MaxValue);
            photonView.RPC(nameof(ScottsRpgLaunch), RpcTarget.All, origin, direction, playerID, rocketId,
                ScottsToolsPlugin.RocketSpeed.Value);
        }

        /// <summary>Aim where the camera looks (crosshair), not just down the wobbly barrel.</summary>
        private Vector3 Aim(Kobold shooter, Vector3 origin) {
            Vector3 fallback = barrel.forward;
            if (shooter == null || shooter != KoboldKare.ModKit.ModKit.LocalKobold) {
                return fallback;
            }
            Camera cam = OrbitCamera.GetCamera();
            if (cam == null) {
                return fallback;
            }
            Vector3 camPos = cam.transform.position;
            Vector3 camForward = OrbitCamera.GetPlayerIntendedRotation() * Vector3.forward;
            Vector3 target = camPos + camForward * 200f;
            int hits = Physics.RaycastNonAlloc(camPos, camForward, AimHits, 200f, RocketMask(), QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < hits; i++) {
                RaycastHit h = AimHits[i];
                if (h.distance < best && h.distance > 0.5f && !IsOwnCollider(h.collider, shooter)) {
                    best = h.distance;
                    target = h.point;
                }
            }
            Vector3 dir = target - origin;
            // Don't shoot backwards if the target is behind the muzzle (point blank).
            if (dir.sqrMagnitude < 0.25f || Vector3.Dot(dir, fallback) < 0f) {
                return camForward;
            }
            return dir.normalized;
        }

        private bool IsOwnCollider(Collider c, Kobold shooter) {
            if (c.transform.IsChildOf(transform)) {
                return true;
            }
            return shooter != null && c.GetComponentInParent<Kobold>() == shooter;
        }

        internal static int RocketMask() {
            int mask = GameManager.instance != null ? (int)GameManager.instance.waterSprayHitMask : ~(1 << 2); // everything but Ignore Raycast
            int nocollide = LayerMask.NameToLayer("PlayerNocollide");
            if (nocollide >= 0) {
                mask &= ~(1 << nocollide);
            }
            return mask;
        }

        [PunRPC]
        private void ScottsRpgLaunch(Vector3 origin, Vector3 direction, int shooterViewID, int rocketId, float speed, PhotonMessageInfo info) {
            readyAt = Time.time + ScottsToolsPlugin.ReloadSeconds.Value;
            if (reloadRoutine != null) {
                StopCoroutine(reloadRoutine);
            }
            loadedRocket.SetActive(false);
            reloadRoutine = StartCoroutine(Reload(ScottsToolsPlugin.ReloadSeconds.Value));

            Kobold shooter = PhotonNetwork.GetPhotonView(shooterViewID)?.GetComponentInParent<Kobold>();
            bool authority = info.Sender == null || info.Sender.IsLocal;
            Rocket.Spawn(this, origin, direction, speed, shooter, shooterViewID, rocketId, authority);
            RpgEffects.PlayLaunch(origin);

            // A bit of kick for whoever holds it.
            if (photonView.IsMine && TryGetComponent(out Rigidbody body)) {
                body.AddForce(-direction * 6f, ForceMode.Impulse);
            }
        }

        private IEnumerator Reload(float seconds) {
            yield return new WaitForSeconds(Mathf.Max(0.1f, seconds));
            loadedRocket.SetActive(true);
            reloadRoutine = null;
        }

        /// <summary>Called by the shooter's own rocket when it hits something (or runs out of time).</summary>
        internal void RocketDetonated(Rocket rocket, Vector3 position, Vector3 normal) {
            float force = ScottsToolsPlugin.KnockbackForce.Value * (isCum ? ScottsToolsPlugin.CumKnockbackScale.Value : 1f);
            photonView.RPC(nameof(ScottsRpgExplode), RpcTarget.All, position, normal, rocket.Id, rocket.ShooterViewID,
                ScottsToolsPlugin.BlastRadius.Value, force, ScottsToolsPlugin.Ragdoll.Value, ScottsToolsPlugin.SelfKnockback.Value);
            if (isCum) {
                RpgEffects.CumBurst(position, normal, rocket.Shooter);
            }
        }

        [PunRPC]
        private void ScottsRpgExplode(Vector3 position, Vector3 normal, int rocketId, int shooterViewID, float radius, float force,
                                      bool ragdoll, bool hitShooter) {
            Rocket.Remove(rocketId);
            RpgEffects.Explode(position, isCum, radius, force, ragdoll, shooterViewID, hitShooter);
        }

        // ------------------------------------------------------------------ IGrabbable

        public bool CanGrab(Kobold kobold) => true;

        [PunRPC]
        public void OnGrabRPC(int koboldID) {
        }

        [PunRPC]
        public void OnReleaseRPC(int koboldID, Vector3 velocity) {
        }

        public Transform GrabTransform() => grab;
    }
}
