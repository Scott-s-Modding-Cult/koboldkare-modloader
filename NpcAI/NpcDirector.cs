using System.Collections.Generic;
using System.Linq;
using System.Text;
using Photon.Pun;
using UnityEngine;
using Vilar.AnimationStation;

namespace KoboldKareNpcAI {
    /// <summary>
    /// Owns one <see cref="NpcBrain"/> per NPC kobold, matches idle NPCs into pairs and
    /// hands each pair a free animation station set to walk to.
    /// </summary>
    internal static class NpcDirector {
        public static bool Enabled { get; private set; }

        private static readonly Dictionary<Kobold, NpcBrain> Brains = new Dictionary<Kobold, NpcBrain>();
        private static readonly List<NpcBrain> TickList = new List<NpcBrain>();
        private static readonly HashSet<BreedingStation> ReservedSets = new HashSet<BreedingStation>();
        private static readonly List<Pairing> Pairings = new List<Pairing>();
        private static BreedingStation[] stationCache = new BreedingStation[0];

        private static float nextScanTime;
        private static float nextMatchTime;
        private static float nextStationCacheTime;

        public static void SetEnabled(bool enabled) {
            if (Enabled == enabled) {
                return;
            }
            Enabled = enabled;
            if (!enabled) {
                foreach (var pairing in Pairings.ToArray()) {
                    EndPairing(pairing, "AI disabled");
                }
                foreach (var brain in Brains.Values) {
                    brain.Shutdown();
                }
                Brains.Clear();
                // Put any borrowed in-place station sets back right away.
                foreach (var restore in PendingRestores) {
                    InPlace.Restore(restore.Placement);
                }
                PendingRestores.Clear();
                ReservedSets.Clear();
            }
            nextScanTime = 0f;
            nextStationCacheTime = 0f;
            pendingRoleDump = enabled;
            NpcAiPlugin.Log.LogInfo($"NPC AI {(enabled ? "enabled" : "disabled")}.");
        }

        private static bool pendingRoleDump;

        private static void DumpStationRoles() {
            var sb = new StringBuilder("Station roles on this map:\n");
            foreach (var set in GetStations().OrderBy(s => s.name)) {
                if (set == null) {
                    continue;
                }
                try {
                    sb.Append("    ").Append(StationRoles.DescribeDetailed(set)).Append('\n');
                } catch (System.Exception e) {
                    sb.Append("    ").Append(set.name).Append(": failed to analyse (").Append(e.GetType().Name).Append(")\n");
                }
            }
            NpcAiPlugin.Log.LogInfo(sb.ToString().TrimEnd());
        }

        public static void Tick() {
            if (!Enabled) {
                return;
            }
            // Only the host owns and drives NPCs; if we lost host status, stand down.
            if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient) {
                SetEnabled(false);
                return;
            }

            float now = Time.time;
            if (now >= nextScanTime) {
                nextScanTime = now + 2f;
                Rescan();
            }
            if (pendingRoleDump) {
                pendingRoleDump = false;
                DumpStationRoles();
            }

            TickList.Clear();
            TickList.AddRange(Brains.Values);
            foreach (var brain in TickList) {
                brain.Tick();
            }

            UpdatePairings();
            ProcessRestores();

            if (now >= nextMatchTime) {
                nextMatchTime = now + 1.5f;
                TryMatch();
            }
        }

        public class Snapshot {
            public bool Enabled;
            public int Npcs;
            public int WithDick;
            public int Pairs;
            public int InPlacePairs;
            public Dictionary<NpcState, int> States = new Dictionary<NpcState, int>();
        }

        public static Snapshot GetSnapshot() {
            var s = new Snapshot { Enabled = Enabled };
            if (!Enabled) {
                return s;
            }
            s.Npcs = Brains.Count;
            s.WithDick = Brains.Keys.Count(Anatomy.HasDick);
            s.Pairs = Pairings.Count;
            s.InPlacePairs = Pairings.Count(p => p.InPlace);
            foreach (var brain in Brains.Values) {
                s.States.TryGetValue(brain.State, out int n);
                s.States[brain.State] = n + 1;
            }
            return s;
        }

        /// <summary>Stops every NPC pairing right now (they go back to roaming after cooldown).</summary>
        public static int EndAllPairings() {
            int count = Pairings.Count;
            foreach (var p in Pairings.ToArray()) {
                EndPairing(p, "ended from the admin panel");
            }
            return count;
        }

        public static string GetStatus() {
            var sb = new StringBuilder();
            sb.Append($"NPC AI: {(Enabled ? "enabled" : "disabled")}\n");
            if (!Enabled) {
                return sb.ToString();
            }
            int withDick = Brains.Keys.Count(Anatomy.HasDick);
            sb.Append($"\tNPCs controlled: {Brains.Count} ({withDick} with a dick), active pairs: {Pairings.Count} ({Pairings.Count(p => p.InPlace)} in-place)\n");
            sb.Append($"\tIn-place pairing: {(InPlace.Allowed ? $"on ({NpcAiPlugin.InPlaceChance.Value:P0} chance)" : "off (disabled or other players present)")}\n");
            foreach (var group in Brains.Values.GroupBy(b => b.State)) {
                sb.Append($"\t{group.Key}: {group.Count()}\n");
            }
            int freeSets = GetStations().Count(s => s != null && !ReservedSets.Contains(s) && FreeSlots(s) >= 2);
            sb.Append($"\tFree 2+ slot stations: {freeSets}\n");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- NPC discovery

        public static bool IsNpc(Kobold k) {
            if (k == null || k.photonView == null || !k.photonView.IsMine) {
                return false;
            }
            foreach (var player in PhotonNetwork.PlayerList) {
                if ((Kobold)player.TagObject == k) {
                    return false;
                }
            }
            if ((Kobold)PhotonNetwork.LocalPlayer.TagObject == k) {
                return false;
            }
            var descriptor = k.GetComponent<CharacterDescriptor>();
            return descriptor != null && descriptor.GetPlayerControlled() == CharacterDescriptor.ControlType.AIPlayer;
        }

        private static void Rescan() {
            // Drop brains for kobolds that died, despawned or got possessed by a player.
            foreach (var pair in Brains.ToArray()) {
                if (!IsNpc(pair.Key)) {
                    var pairing = FindPairing(pair.Value);
                    if (pairing != null) {
                        EndPairing(pairing, "member no longer an NPC");
                    }
                    pair.Value.Shutdown();
                    Brains.Remove(pair.Key);
                }
            }
            foreach (var k in Object.FindObjectsOfType<Kobold>()) {
                if (!Brains.ContainsKey(k) && IsNpc(k)) {
                    Brains[k] = new NpcBrain(k);
                }
            }
        }

        // ---------------------------------------------------------------- stations

        private static BreedingStation[] GetStations() {
            if (Time.time >= nextStationCacheTime) {
                nextStationCacheTime = Time.time + 10f;
                stationCache = Object.FindObjectsOfType<BreedingStation>();
            }
            return stationCache;
        }

        private static int FreeSlots(BreedingStation set) {
            var stations = set.GetAnimationStations();
            if (stations == null) {
                return 0;
            }
            int free = 0;
            foreach (var station in stations) {
                if (station != null && station.info.user == null) {
                    free++;
                }
            }
            return free;
        }

        private static bool IsUsable(BreedingStation set, Kobold probe) {
            if (set == null || !set.isActiveAndEnabled || set.photonView == null) {
                return false;
            }
            // BreedingStation.CanUse checks that it has been built and has a free slot.
            if (!set.CanUse(probe)) {
                return false;
            }
            var stations = set.GetAnimationStations();
            return stations != null && stations.Count >= 2 && FreeSlots(set) >= 2;
        }

        // ---------------------------------------------------------------- matchmaking

        private static void TryMatch() {
            var candidates = Brains.Values.Where(b => b.WantsPartner).ToList();
            if (candidates.Count < 2) {
                return;
            }
            Shuffle(candidates);

            var a = candidates[0];
            // Prefer the closest willing partner, with a little randomness.
            var b = candidates.Skip(1)
                .OrderBy(c => Vector3.Distance(c.Position, a.Position) * Random.Range(0.7f, 1.3f))
                .First();

            bool aDick = Anatomy.HasDick(a.Kobold);
            bool bDick = Anatomy.HasDick(b.Kobold);
            float maxDistance = NpcAiPlugin.MaxStationDistance.Value;

            // In-place: meet where A stands and bring a furniture-free station set to them.
            if (InPlace.Allowed && Random.value < NpcAiPlugin.InPlaceChance.Value &&
                Vector3.Distance(a.Position, b.Position) <= maxDistance) {
                var template = GetStations()
                    .Where(s => s != null && InPlace.IsCandidate(s) && !ReservedSets.Contains(s) &&
                                IsUsable(s, a.Kobold) && PlanSlots(s, aDick, bDick) != null)
                    .OrderBy(_ => Random.value)
                    .FirstOrDefault();
                if (template != null && TryStartInPlace(a, b, template, aDick, bDick)) {
                    return;
                }
            }

            Vector3 mid = (a.Position + b.Position) * 0.5f;
            SlotPlan plan = null;
            var set = GetStations()
                .Where(s => s != null && !ReservedSets.Contains(s) && IsUsable(s, a.Kobold))
                .Select(s => new { s, d = Vector3.Distance(s.transform.position, mid) })
                .Where(x => x.d <= maxDistance)
                .OrderBy(x => x.d)
                .Select(x => x.s)
                .Where(s => PlanSlots(s, aDick, bDick) != null) // must suit this pair's anatomy
                .Take(3)
                .OrderBy(_ => Random.value)
                .FirstOrDefault();
            if (set == null) {
                return;
            }
            plan = PlanSlots(set, aDick, bDick);
            if (plan == null) {
                return;
            }

            var stations = set.GetAnimationStations();
            var pairing = new Pairing {
                Set = set,
                A = a, SlotA = plan.SlotA,
                B = b, SlotB = plan.SlotB,
                Deadline = Time.time + 60f,
            };
            if (!a.AssignStation(stations[pairing.SlotA].transform.position) ||
                !b.AssignStation(stations[pairing.SlotB].transform.position)) {
                a.CancelStation();
                b.CancelStation();
                return;
            }
            ReservedSets.Add(set);
            Pairings.Add(pairing);
            NpcAiPlugin.Log.LogInfo(
                $"Pairing {a.Name}{(aDick ? " (dick)" : "")} -> slot {plan.SlotA}{(plan.APenetrates ? " [penetrator]" : "")}, " +
                $"{b.Name}{(bDick ? " (dick)" : "")} -> slot {plan.SlotB}{(plan.BPenetrates ? " [penetrator]" : "")} " +
                $"at {set.name} ({StationRoles.Describe(set)}).");
        }

        private static bool TryStartInPlace(NpcBrain a, NpcBrain b, BreedingStation set, bool aDick, bool bDick) {
            var plan = PlanSlots(set, aDick, bDick);
            if (plan == null) {
                return false;
            }
            // B walks up to about a meter from A, approaching from where it is now.
            Vector3 offset = b.Position - a.Position;
            offset.y = 0f;
            if (offset.sqrMagnitude < 0.01f) {
                offset = a.Kobold.transform.forward;
                offset.y = 0f;
            }
            Vector3 meetPoint = a.Position + offset.normalized * Mathf.Max(0.6f, a.Kobold.transform.lossyScale.x);
            if (!b.AssignStation(meetPoint)) {
                return false;
            }
            a.HoldPosition();

            ReservedSets.Add(set);
            Pairings.Add(new Pairing {
                Set = set,
                A = a, SlotA = plan.SlotA,
                B = b, SlotB = plan.SlotB,
                Deadline = Time.time + 45f,
                InPlace = true,
            });
            NpcAiPlugin.Log.LogInfo(
                $"In-place pairing {a.Name}{(aDick ? " (dick)" : "")} -> slot {plan.SlotA}{(plan.APenetrates ? " [penetrator]" : "")}, " +
                $"{b.Name}{(bDick ? " (dick)" : "")} -> slot {plan.SlotB}{(plan.BPenetrates ? " [penetrator]" : "")} " +
                $"using {set.name} ({StationRoles.Describe(set)}); {b.Name} walks to {a.Name}.");
            return true;
        }

        private class SlotPlan {
            public int SlotA, SlotB;
            public bool APenetrates, BPenetrates;
        }

        /// <summary>
        /// Picks two free slots in <paramref name="set"/> for kobolds A and B so that a penetrator
        /// slot always goes to a kobold with a dick. Returns null if the set doesn't suit the pair
        /// (e.g. it has a penetrator role but neither has a dick).
        /// </summary>
        private static SlotPlan PlanSlots(BreedingStation set, bool aDick, bool bDick) {
            var stations = set.GetAnimationStations();
            var roles = StationRoles.GetPenetratorSlots(set);
            var freePen = new List<int>();
            var freeOther = new List<int>();
            for (int i = 0; i < stations.Count; i++) {
                if (stations[i] == null || stations[i].info.user != null) {
                    continue;
                }
                if (i < roles.Length && roles[i]) {
                    freePen.Add(i);
                } else {
                    freeOther.Add(i);
                }
            }
            Shuffle(freePen);
            Shuffle(freeOther);

            if (!StationRoles.HasRoles(roles)) {
                // Role-free set: any two free slots, any order.
                if (freeOther.Count < 2) {
                    return null;
                }
                return new SlotPlan { SlotA = freeOther[0], SlotB = freeOther[1] };
            }

            if (!aDick && !bDick) {
                return null; // this position needs a penetrator and neither has a dick
            }

            if (freePen.Count == 0) {
                return null;
            }
            // The kobold with a dick takes a penetrator slot (random if both have one). The partner
            // takes a receiving slot if there is one; in face-to-face poses where both slots can
            // penetrate, it simply takes the other one.
            bool aPenetrates = aDick && (!bDick || Random.value < 0.5f);
            int penSlot = freePen[0];
            int partnerSlot;
            if (freeOther.Count > 0) {
                partnerSlot = freeOther[0];
            } else if (freePen.Count > 1) {
                partnerSlot = freePen[1];
            } else {
                return null;
            }
            return aPenetrates
                ? new SlotPlan { SlotA = penSlot, SlotB = partnerSlot, APenetrates = true }
                : new SlotPlan { SlotA = partnerSlot, SlotB = penSlot, BPenetrates = true };
        }

        private class Pairing {
            public BreedingStation Set;
            public NpcBrain A, B;
            public int SlotA, SlotB;
            public float Deadline;
            public bool Started;
            public float EndTime;
            public float StartedAt;
            public bool InPlace;
            public InPlace.Placement Placement; // set once the station set has been moved
        }

        private class PendingRestore {
            public InPlace.Placement Placement;
            public float Time;
        }

        private static readonly List<PendingRestore> PendingRestores = new List<PendingRestore>();

        private static void ProcessRestores() {
            for (int i = PendingRestores.Count - 1; i >= 0; i--) {
                var r = PendingRestores[i];
                if (r.Placement.Set == null) {
                    PendingRestores.RemoveAt(i);
                    continue;
                }
                if (Time.time < r.Time) {
                    continue;
                }
                // Someone (e.g. the player) is using the set where we left it; try again later.
                if (FreeSlots(r.Placement.Set) < r.Placement.Set.GetAnimationStations().Count) {
                    r.Time = Time.time + 5f;
                    continue;
                }
                InPlace.Restore(r.Placement);
                ReservedSets.Remove(r.Placement.Set);
                PendingRestores.RemoveAt(i);
            }
        }

        private static Pairing FindPairing(NpcBrain brain) => Pairings.FirstOrDefault(p => p.A == brain || p.B == brain);

        private static void UpdatePairings() {
            foreach (var p in Pairings.ToArray()) {
                if (p.Set == null || p.A.Kobold == null || p.B.Kobold == null) {
                    EndPairing(p, "something despawned");
                    continue;
                }

                if (!p.Started) {
                    if (p.A.State != NpcState.ToStation && p.A.State != NpcState.WaitingAtStation ||
                        p.B.State != NpcState.ToStation && p.B.State != NpcState.WaitingAtStation) {
                        EndPairing(p, "a member was interrupted");
                        continue;
                    }
                    bool bothThere = p.A.State == NpcState.WaitingAtStation && p.B.State == NpcState.WaitingAtStation;
                    if (bothThere || Time.time > p.Deadline) {
                        // If time ran out, only start if both at least got close.
                        bool closeEnough = p.InPlace
                            ? p.B.IsNear(p.A.Position, 4f)
                            : p.A.IsNear(p.Set.transform.position, 6f) && p.B.IsNear(p.Set.transform.position, 6f);
                        if (!bothThere && !closeEnough) {
                            EndPairing(p, p.InPlace ? "couldn't reach the partner in time" : "couldn't reach the station in time");
                            continue;
                        }
                        StartPairing(p);
                    }
                    continue;
                }

                // Running: end it when the timer is up, or if either kobold got pulled out
                // (player grabbed them, used the station, ran out of energy...).
                bool grace = Time.time < p.StartedAt + 3f;
                if (!grace && (!p.A.IsAnimating || !p.B.IsAnimating)) {
                    EndPairing(p, "animation was interrupted");
                } else if (Time.time >= p.EndTime) {
                    EndPairing(p, null);
                }
            }
        }

        private static void StartPairing(Pairing p) {
            var stations = p.Set.GetAnimationStations();
            var userA = stations[p.SlotA].info.user;
            var userB = stations[p.SlotB].info.user;
            if (userA != null && userA != p.A.Kobold || userB != null && userB != p.B.Kobold) {
                EndPairing(p, "someone else took the station");
                return;
            }
            if (p.InPlace && p.Placement == null) {
                // Bring the station set to the anchor, facing the way the partner walked in.
                p.Placement = InPlace.Move(p.Set, p.A.Position, p.A.Position - p.B.Position);
            }
            int viewId = p.Set.photonView.ViewID;
            p.A.BeginAnimation(viewId, p.SlotA);
            p.B.BeginAnimation(viewId, p.SlotB);
            p.Started = true;
            p.StartedAt = Time.time;
            p.EndTime = Time.time + Random.Range(NpcAiPlugin.PairMinSeconds.Value, NpcAiPlugin.PairMaxSeconds.Value);
        }

        private static void EndPairing(Pairing p, string reason) {
            Pairings.Remove(p);
            if (p.Placement != null) {
                // Keep the borrowed set reserved until the 1s blend-out is over, then move it back.
                PendingRestores.Add(new PendingRestore { Placement = p.Placement, Time = Time.time + 2.5f });
            } else if (p.Set != null) {
                ReservedSets.Remove(p.Set);
            }
            p.A?.FinishPairing(p.Started);
            p.B?.FinishPairing(p.Started);
            if (reason != null) {
                NpcAiPlugin.Log.LogInfo($"Pairing {p.A?.Name} + {p.B?.Name} ended: {reason}.");
            }
        }

        private static void Shuffle<T>(IList<T> list) {
            for (int i = list.Count - 1; i > 0; i--) {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
