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
                // Each step is guarded so one failure can't leave NPCs stuck or station sets moved.
                foreach (var pairing in Pairings.ToArray()) {
                    ForceEndPairing(pairing, "AI disabled");
                }
                foreach (var brain in Brains.Values) {
                    try {
                        brain.Shutdown();
                    } catch (System.Exception e) {
                        LogError("releasing an NPC", e);
                    }
                }
                Brains.Clear();
                // Put any borrowed in-place station sets back right away.
                foreach (var restore in PendingRestores) {
                    try {
                        InPlace.Restore(restore.Placement);
                    } catch (System.Exception e) {
                        LogError("putting a borrowed station set back", e);
                    }
                }
                PendingRestores.Clear();
                ReservedSets.Clear();
                Quarantine.Clear();
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

            // Every phase and every NPC is isolated: one broken kobold, station or pairing (a modded
            // rig, a workshop station, another mod poking at the same objects) gets dropped and
            // logged instead of throwing every frame and freezing all the other NPCs.
            float now = Time.time;
            if (now >= nextScanTime) {
                nextScanTime = now + 2f;
                Guard("scanning for NPCs", Rescan);
            }
            if (pendingRoleDump) {
                pendingRoleDump = false;
                Guard("listing station roles", DumpStationRoles);
            }

            TickList.Clear();
            TickList.AddRange(Brains.Values);
            foreach (var brain in TickList) {
                try {
                    brain.Tick();
                } catch (System.Exception e) {
                    DropBrain(brain, e);
                }
            }

            UpdatePairings();
            Guard("releasing orphaned NPCs", ReleaseOrphans);
            ProcessRestores();

            if (now >= nextMatchTime) {
                nextMatchTime = now + 1.5f;
                Guard("matching partners", TryMatch);
            }
        }

        // ---------------------------------------------------------------- error isolation

        /// <summary>Kobolds whose brain threw; left alone until the time stored here.</summary>
        private static readonly Dictionary<Kobold, float> Quarantine = new Dictionary<Kobold, float>();
        private const float QuarantineSeconds = 30f;

        private static readonly Dictionary<string, float> ErrorLogTimes = new Dictionary<string, float>();

        private static void Guard(string what, System.Action action) {
            try {
                action();
            } catch (System.Exception e) {
                LogError(what, e);
            }
        }

        /// <summary>Logs an error, but the same error from the same place at most once a minute.</summary>
        private static void LogError(string what, System.Exception e) {
            string key = what + "|" + e.GetType().FullName + "|" + e.Message;
            if (ErrorLogTimes.TryGetValue(key, out float last) && Time.time < last + 60f) {
                return;
            }
            ErrorLogTimes[key] = Time.time;
            NpcAiPlugin.Log.LogError($"Error while {what} (skipped; the rest of the AI keeps running): {e}");
        }

        /// <summary>Stops driving a kobold whose brain threw and leaves it alone for a while.</summary>
        private static void DropBrain(NpcBrain brain, System.Exception e) {
            LogError($"driving {SafeName(brain)}", e);
            var pairing = FindPairing(brain);
            if (pairing != null) {
                ForceEndPairing(pairing, "a member hit an error");
            }
            try {
                brain.Shutdown();
            } catch {
                // Already broken; just stop tracking it.
            }
            foreach (var key in Brains.Where(kv => kv.Value == brain).Select(kv => kv.Key).ToArray()) {
                Brains.Remove(key);
                if (key != null) {
                    Quarantine[key] = Time.time + QuarantineSeconds;
                }
            }
        }

        /// <summary>Ends a pairing, falling back to just forgetting it if the normal clean-up throws too.</summary>
        private static void ForceEndPairing(Pairing p, string reason) {
            try {
                EndPairing(p, reason);
                return;
            } catch (System.Exception e) {
                LogError("ending a pairing", e);
            }
            Pairings.Remove(p);
            if (p.Placement != null) {
                try {
                    InPlace.Restore(p.Placement);
                } catch (System.Exception e) {
                    LogError("putting a borrowed station set back", e);
                }
            }
            if (p.Set != null) {
                ReservedSets.Remove(p.Set);
            }
        }

        /// <summary>
        /// An NPC heading to, waiting at or animating on a station with no pairing behind it would
        /// otherwise stand there forever (e.g. matchmaking threw halfway). Send it back to idling.
        /// </summary>
        private static void ReleaseOrphans() {
            foreach (var brain in Brains.Values.ToArray()) {
                var state = brain.State;
                if (state != NpcState.ToStation && state != NpcState.WaitingAtStation && state != NpcState.Paired) {
                    continue;
                }
                if (FindPairing(brain) != null) {
                    continue;
                }
                try {
                    brain.FinishPairing(state == NpcState.Paired);
                    NpcAiPlugin.Log.LogInfo($"{brain.Name} was {state} without a pairing; released it.");
                } catch (System.Exception e) {
                    DropBrain(brain, e);
                }
            }
        }

        private static string SafeName(NpcBrain brain) {
            try {
                return brain?.Name ?? "an NPC";
            } catch {
                return "an NPC";
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
                bool stillNpc;
                try {
                    stillNpc = IsNpc(pair.Key);
                } catch (System.Exception e) {
                    DropBrain(pair.Value, e);
                    continue;
                }
                if (!stillNpc) {
                    var pairing = FindPairing(pair.Value);
                    if (pairing != null) {
                        ForceEndPairing(pairing, "member no longer an NPC");
                    }
                    try {
                        pair.Value.Shutdown();
                    } catch (System.Exception e) {
                        LogError("releasing an NPC", e);
                    }
                    Brains.Remove(pair.Key);
                }
            }

            float now = Time.time;
            foreach (var k in Quarantine.Where(kv => kv.Key == null || now >= kv.Value).Select(kv => kv.Key).ToArray()) {
                Quarantine.Remove(k);
            }
            foreach (var k in Object.FindObjectsOfType<Kobold>()) {
                if (Brains.ContainsKey(k) || Quarantine.ContainsKey(k)) {
                    continue;
                }
                try {
                    if (IsNpc(k)) {
                        Brains[k] = new NpcBrain(k);
                    }
                } catch (System.Exception e) {
                    LogError("taking control of " + (k != null ? k.name : "a kobold"), e);
                    Quarantine[k] = now + QuarantineSeconds;
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
                ADick = aDick, BDick = bDick,
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
                ADick = aDick, BDick = bDick,
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
            public bool ADick, BDick;
            public InPlace.Placement Placement; // set once the station set has been moved
        }

        /// <summary>
        /// Soft to fully hard in about 3 s, a little faster than the player's erection key (0.3/s),
        /// so it's done by the time a pair that meets up close starts animating.
        /// </summary>
        private const float ArousalPerSecond = 0.35f;

        /// <summary>
        /// The game only raises arousal (which sizes the dick) from the player's erection key, being
        /// grabbed, or a dick that is already penetrating, so an NPC's dick would stay soft and never
        /// reach its partner. Pump it for every pair member with a dick, from the moment the pair is
        /// matched until it ends. Any positive pump also holds off the game's slow decay; once the
        /// pairing ends, the game's own decay (0.01/s after 10 s) softens it again. NPCs are owned by
        /// the host and arousal is network-synced by the owner, so other players see it too.
        /// </summary>
        private static void Arouse(Pairing p) {
            float amount = Time.deltaTime * ArousalPerSecond;
            if (p.ADick) {
                p.A.Kobold.PumpUpDick(amount);
            }
            if (p.BDick) {
                p.B.Kobold.PumpUpDick(amount);
            }
        }

        private class PendingRestore {
            public InPlace.Placement Placement;
            public float Time;
        }

        private static readonly List<PendingRestore> PendingRestores = new List<PendingRestore>();

        private static void ProcessRestores() {
            for (int i = PendingRestores.Count - 1; i >= 0; i--) {
                var r = PendingRestores[i];
                try {
                    if (ProcessRestore(r)) {
                        PendingRestores.RemoveAt(i);
                    }
                } catch (System.Exception e) {
                    LogError("putting a borrowed station set back", e);
                    // Try once more the plain way, then forget it so it can't throw every frame.
                    try {
                        InPlace.Restore(r.Placement);
                    } catch {
                        // Nothing else to do; reloading the map resets it.
                    }
                    if (r.Placement?.Set != null) {
                        ReservedSets.Remove(r.Placement.Set);
                    }
                    PendingRestores.RemoveAt(i);
                }
            }
        }

        /// <returns>true when the restore is done (or no longer needed).</returns>
        private static bool ProcessRestore(PendingRestore r) {
            if (r.Placement?.Set == null) {
                return true;
            }
            if (Time.time < r.Time) {
                return false;
            }
            // Someone (e.g. the player) is using the set where we left it; try again later.
            if (FreeSlots(r.Placement.Set) < r.Placement.Set.GetAnimationStations().Count) {
                r.Time = Time.time + 5f;
                return false;
            }
            InPlace.Restore(r.Placement);
            ReservedSets.Remove(r.Placement.Set);
            return true;
        }

        private static Pairing FindPairing(NpcBrain brain) => Pairings.FirstOrDefault(p => p.A == brain || p.B == brain);

        private static void UpdatePairings() {
            foreach (var p in Pairings.ToArray()) {
                if (!Pairings.Contains(p)) {
                    continue; // already ended while handling an earlier one
                }
                try {
                    UpdatePairing(p);
                } catch (System.Exception e) {
                    LogError($"running the pairing {SafeName(p.A)} + {SafeName(p.B)}", e);
                    ForceEndPairing(p, "it hit an error");
                }
            }
        }

        private static void UpdatePairing(Pairing p) {
            if (p.Set == null || p.A.Kobold == null || p.B.Kobold == null) {
                EndPairing(p, "something despawned");
                return;
            }

            Arouse(p);

            if (!p.Started) {
                if (p.A.State != NpcState.ToStation && p.A.State != NpcState.WaitingAtStation ||
                    p.B.State != NpcState.ToStation && p.B.State != NpcState.WaitingAtStation) {
                    EndPairing(p, "a member was interrupted");
                    return;
                }
                bool bothThere = p.A.State == NpcState.WaitingAtStation && p.B.State == NpcState.WaitingAtStation;
                if (bothThere || Time.time > p.Deadline) {
                    // If time ran out, only start if both at least got close.
                    bool closeEnough = p.InPlace
                        ? p.B.IsNear(p.A.Position, 4f)
                        : p.A.IsNear(p.Set.transform.position, 6f) && p.B.IsNear(p.Set.transform.position, 6f);
                    if (!bothThere && !closeEnough) {
                        EndPairing(p, p.InPlace ? "couldn't reach the partner in time" : "couldn't reach the station in time");
                        return;
                    }
                    StartPairing(p);
                }
                return;
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
