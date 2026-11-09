using Photon.Pun;
using KoboldKare.ModKit;
using UnityEngine;

namespace KoboldKareNpcAI {
    internal enum NpcState {
        Idle,
        Roaming,
        ToStation,
        WaitingAtStation,
        Paired,
        Busy, // grabbed, ragdolled, or put into an animation by someone else
    }

    /// <summary>
    /// Drives a single NPC kobold by feeding its KoboldCharacterController the same
    /// inputs the player's possession script would (inputDir / inputWalking / inputJump).
    /// Paths come from the map's baked NavMesh when there is one, otherwise straight lines.
    /// </summary>
    internal class NpcBrain {
        public readonly Kobold Kobold;
        public NpcState State { get; private set; } = NpcState.Idle;

        private readonly KoboldCharacterController controller;
        private readonly CharacterControllerAnimator animator;

        private Vector3[] path;
        private int corner;
        private float stateUntil;
        private bool wantsPair;
        private float pairCooldownUntil;
        private bool jumpQueued;

        private Vector3 progressPos;
        private float progressCheckTime;
        private int stuckStrikes;

        public NpcBrain(Kobold kobold) {
            Kobold = kobold;
            controller = kobold.GetComponent<KoboldCharacterController>();
            animator = kobold.GetComponent<CharacterControllerAnimator>();
            // Stagger start times so a crowd doesn't all move at once.
            EnterIdle(Random.Range(0.5f, 4f));
            pairCooldownUntil = Time.time + Random.Range(5f, 20f);
        }

        public string Name => Kobold == null ? "<gone>"
            : Kobold.photonView != null ? $"Kobold#{Kobold.photonView.ViewID}" : Kobold.name;
        public Vector3 Position => Kobold.body != null ? Kobold.body.position : Kobold.transform.position;
        private float Scale => Mathf.Max(0.2f, Kobold.transform.lossyScale.x);
        public bool IsAnimating => animator != null && animator.IsAnimating();

        public bool WantsPartner =>
            Kobold != null &&
            wantsPair &&
            (State == NpcState.Idle || State == NpcState.Roaming) &&
            Time.time >= pairCooldownUntil &&
            Kobold.GetEnergy() > 0f;

        public bool IsNear(Vector3 point, float meters) => FlatDistance(Position, point) <= meters * Scale;

        // ------------------------------------------------------------------ per frame

        public void Tick() {
            if (Kobold == null || controller == null || animator == null) {
                return;
            }

            if (IsBusy()) {
                if (State != NpcState.Busy && State != NpcState.Paired) {
                    StopMoving();
                    State = NpcState.Busy;
                }
                return;
            }
            if (State == NpcState.Busy) {
                EnterIdle(Random.Range(1f, 3f));
            }

            switch (State) {
                case NpcState.Idle:
                    StopMoving();
                    if (Time.time >= stateUntil) {
                        if (!wantsPair && Time.time >= pairCooldownUntil) {
                            wantsPair = Random.value < NpcAiPlugin.PairChance.Value;
                        }
                        if (!TryStartRoam()) {
                            EnterIdle(Random.Range(1f, 3f));
                        }
                    }
                    break;

                case NpcState.Roaming:
                    if (FollowPath(NpcAiPlugin.Walk.Value) || Time.time >= stateUntil) {
                        EnterIdle(Random.Range(NpcAiPlugin.IdleMinSeconds.Value, NpcAiPlugin.IdleMaxSeconds.Value));
                    }
                    break;

                case NpcState.ToStation:
                    // Hurry a little when meeting a partner.
                    if (FollowPath(false)) {
                        StopMoving();
                        State = NpcState.WaitingAtStation;
                    }
                    break;

                case NpcState.WaitingAtStation:
                case NpcState.Paired:
                    StopMoving();
                    break;
            }
        }

        private bool IsBusy() {
            if (Kobold.grabbed) {
                return true;
            }
            var ragdoller = Kobold.GetRagdoller();
            if (ragdoller != null && ragdoller.ragdolled) {
                return true;
            }
            // Animating without us having started it (e.g. the player used them).
            if (State != NpcState.Paired && animator.IsAnimating()) {
                return true;
            }
            // Controller is switched off while carried or blending out of an animation.
            return State != NpcState.Paired && !controller.enabled;
        }

        // ------------------------------------------------------------------ director API

        public bool AssignStation(Vector3 stationPosition) {
            if (!SetDestination(stationPosition, requireReachable: false)) {
                return false;
            }
            wantsPair = false;
            State = NpcState.ToStation;
            return true;
        }

        /// <summary>In-place pairing anchor: stop where we are and wait for the partner.</summary>
        public void HoldPosition() {
            StopMoving();
            path = null;
            wantsPair = false;
            State = NpcState.WaitingAtStation;
        }

        public void CancelStation() {
            if (State == NpcState.ToStation || State == NpcState.WaitingAtStation) {
                EnterIdle(Random.Range(1f, 3f));
            }
        }

        public void BeginAnimation(int stationSetViewId, int slot) {
            StopMoving();
            Kobold.photonView.RPC(nameof(CharacterControllerAnimator.BeginAnimationRPC), RpcTarget.All, stationSetViewId, slot);
            State = NpcState.Paired;
        }

        public void FinishPairing(bool started) {
            if (Kobold == null) {
                return;
            }
            if (started && animator.IsAnimating()) {
                Kobold.photonView.RPC(nameof(CharacterControllerAnimator.StopAnimationRPC), RpcTarget.All);
            }
            wantsPair = false;
            if (started) {
                pairCooldownUntil = Time.time + NpcAiPlugin.PairCooldownSeconds.Value * Random.Range(0.75f, 1.5f);
            }
            // Give the stop-animation blend (1s) time to hand control back before walking off.
            EnterIdle(Random.Range(2f, 5f));
        }

        public void Shutdown() {
            if (Kobold != null && controller != null) {
                StopMoving();
            }
        }

        // ------------------------------------------------------------------ movement

        private void EnterIdle(float seconds) {
            State = NpcState.Idle;
            stateUntil = Time.time + seconds;
            path = null;
        }

        private bool TryStartRoam() {
            float radius = NpcAiPlugin.RoamRadius.Value * Mathf.Lerp(1f, Scale, 0.5f);
            for (int attempt = 0; attempt < 8; attempt++) {
                // (Random.insideUnitCircle is stripped from this game's CoreModule.)
                // Mathf.PI/Cos/Sin are stripped too, so use System.Math.
                double angle = Random.Range(0f, 1f) * System.Math.PI * 2.0;
                float distance = Random.Range(radius * 0.25f, radius);
                Vector3 target = Position + new Vector3((float)System.Math.Cos(angle), 0f, (float)System.Math.Sin(angle)) * distance;
                if (SetDestination(target, requireReachable: true)) {
                    State = NpcState.Roaming;
                    stateUntil = Time.time + 40f; // give up on a walk that takes forever
                    return true;
                }
            }
            return false;
        }

        private bool SetDestination(Vector3 destination, bool requireReachable) {
            stuckStrikes = 0;
            progressPos = Position;
            progressCheckTime = Time.time + 2.5f;

            var corners = Nav.TryGetPath(Position, destination, 4f * Scale, requireReachable, out bool hasNavMesh);
            if (corners != null) {
                // Append the real destination so we finish on the station, not the navmesh edge.
                path = new Vector3[corners.Length + (requireReachable ? 0 : 1)];
                corners.CopyTo(path, 0);
                if (!requireReachable) {
                    path[path.Length - 1] = destination;
                }
                corner = 0;
                return true;
            }
            if (hasNavMesh && requireReachable) {
                // We're on a navmesh but that roam point isn't reachable; pick another.
                return false;
            }

            // No navmesh here (custom map?) - walk in a straight line and rely on stuck detection.
            path = new[] { destination };
            corner = 0;
            return true;
        }

        /// <returns>true once the end of the path is reached (or the walk is abandoned).</returns>
        private bool FollowPath(bool walk) {
            if (path == null) {
                StopMoving();
                return true;
            }

            Vector3 pos = Position;
            float reach = 0.6f * Scale;
            while (corner < path.Length && FlatDistance(pos, path[corner]) < (corner == path.Length - 1 ? reach * 1.5f : reach)) {
                corner++;
            }
            if (corner >= path.Length) {
                StopMoving();
                path = null;
                return true;
            }

            Vector3 dir = path[corner] - pos;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) {
                corner++;
                return false;
            }
            dir.Normalize();

            controller.inputDir = dir;
            controller.inputWalking = walk;
            controller.inputJump = jumpQueued;
            jumpQueued = false;
            // Facing only follows inputDir while running, so steer it ourselves when walking.
            animator.SetFacingDirection(dir);

            // Stuck detection: hop once, then give up on this path.
            if (Time.time >= progressCheckTime) {
                if (FlatDistance(pos, progressPos) < 0.4f * Scale) {
                    stuckStrikes++;
                    jumpQueued = true;
                    if (stuckStrikes >= 3) {
                        StopMoving();
                        path = null;
                        return true;
                    }
                } else {
                    stuckStrikes = 0;
                }
                progressPos = pos;
                progressCheckTime = Time.time + 2.5f;
            }
            return false;
        }

        private void StopMoving() {
            controller.inputDir = Vector3.zero;
            controller.inputWalking = false;
            controller.inputJump = false;
            jumpQueued = false;
        }

        private static float FlatDistance(Vector3 a, Vector3 b) {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
