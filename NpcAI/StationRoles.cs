using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Vilar.AnimationStation;

namespace KoboldKareNpcAI {
    /// <summary>
    /// Works out which slots of an animation station set can be the penetrating role, so the
    /// NPC with a dick is put in one of them.
    ///
    /// 1. If the set's author ticked AnimationStationInfo.needsPenetrator on any slot, trust that.
    /// 2. Otherwise use the pose: a slot is penetrator-capable when its pelvis (hips IK target's
    ///    forward axis) points at its partner's hips. In doggystyle only the back slot qualifies;
    ///    in face-to-face poses both do (whoever has the dick takes the role). If no slot's pelvis
    ///    faces its partner, the set is role-free and anyone can take any slot.
    ///
    /// This only reads station data, so it works the same for imported/modded character rigs and
    /// for workshop stations.
    /// </summary>
    internal static class StationRoles {
        private const int HipsIndex = 1; // IKTargetSet.parts.HIPS
        private const float MinScore = 0.5f;   // pelvis must point clearly toward the partner
        private const float MaxBehindBest = 0.35f; // capable slots must be close to the best score

        private class RoleInfo {
            public bool[] Penetrator;
            public float[] Score;
            public bool FromFlags;
        }

        private static readonly Dictionary<int, RoleInfo> Cache = new Dictionary<int, RoleInfo>();

        /// <returns>One flag per slot: true = can be the penetrator. All false = role-free set.</returns>
        public static bool[] GetPenetratorSlots(BreedingStation set) => Get(set).Penetrator;

        public static bool HasRoles(bool[] roles) {
            foreach (bool r in roles) {
                if (r) {
                    return true;
                }
            }
            return false;
        }

        public static string Describe(BreedingStation set) {
            var roles = GetPenetratorSlots(set);
            if (!HasRoles(roles)) {
                return "role-free";
            }
            var slots = new List<string>();
            for (int i = 0; i < roles.Length; i++) {
                if (roles[i]) {
                    slots.Add(i.ToString());
                }
            }
            return "penetrator slot " + string.Join("/", slots.ToArray());
        }

        /// <summary>One line per set with the raw numbers, for checking the heuristic in-game.</summary>
        public static string DescribeDetailed(BreedingStation set) {
            var info = Get(set);
            var sb = new StringBuilder();
            sb.Append(set.name).Append(": ").Append(Describe(set));
            sb.Append(info.FromFlags ? " [author flags]" : " [pose]");
            sb.Append(" scores=");
            for (int i = 0; i < info.Score.Length; i++) {
                if (i > 0) {
                    sb.Append(", ");
                }
                sb.Append(float.IsNaN(info.Score[i]) ? "n/a" : info.Score[i].ToString("+0.00;-0.00"));
            }
            return sb.ToString();
        }

        private static RoleInfo Get(BreedingStation set) {
            int key = set.GetInstanceID();
            if (!Cache.TryGetValue(key, out var info)) {
                info = Compute(set.GetAnimationStations());
                Cache[key] = info;
            }
            return info;
        }

        private static RoleInfo Compute(IList<AnimationStation> stations) {
            int n = stations?.Count ?? 0;
            var info = new RoleInfo { Penetrator = new bool[n], Score = new float[n] };
            for (int i = 0; i < n; i++) {
                info.Score[i] = float.NaN;
            }
            if (n == 0) {
                return info;
            }

            // Pose geometry (always computed so it can be logged).
            var hips = new Vector3[n];
            var hipForward = new Vector3[n];
            var valid = new bool[n];
            for (int i = 0; i < n; i++) {
                var station = stations[i];
                if (station == null || station.loops == null || station.loops.Count == 0) {
                    continue;
                }
                var loop = station.loops[0];
                if (loop.targetPositions == null || loop.targetPositions.Length <= HipsIndex ||
                    loop.targetRotations == null || loop.targetRotations.Length <= HipsIndex) {
                    continue;
                }
                hips[i] = station.transform.TransformPoint(loop.targetPositions[HipsIndex]);
                hipForward[i] = station.transform.rotation * loop.targetRotations[HipsIndex] * Vector3.forward;
                valid[i] = true;
            }

            float best = float.NegativeInfinity;
            for (int i = 0; i < n; i++) {
                if (!valid[i]) {
                    continue;
                }
                // Partner = the slot whose hips are closest to ours.
                int partner = -1;
                float partnerDist = float.PositiveInfinity;
                for (int j = 0; j < n; j++) {
                    if (j == i || !valid[j]) {
                        continue;
                    }
                    float d = (hips[j] - hips[i]).sqrMagnitude;
                    if (d < partnerDist) {
                        partnerDist = d;
                        partner = j;
                    }
                }
                if (partner < 0 || partnerDist < 1e-6f) {
                    continue;
                }
                info.Score[i] = Vector3.Dot((hips[partner] - hips[i]).normalized, hipForward[i].normalized);
                if (info.Score[i] > best) {
                    best = info.Score[i];
                }
            }

            // 1. Explicit flags from the station's author win.
            for (int i = 0; i < n; i++) {
                if (stations[i] != null && stations[i].info != null && stations[i].info.needsPenetrator) {
                    info.Penetrator[i] = true;
                    info.FromFlags = true;
                }
            }
            if (info.FromFlags) {
                return info;
            }

            // 2. Every slot whose pelvis clearly faces its partner can penetrate.
            if (best >= MinScore) {
                for (int i = 0; i < n; i++) {
                    if (!float.IsNaN(info.Score[i]) && info.Score[i] >= MinScore && info.Score[i] >= best - MaxBehindBest) {
                        info.Penetrator[i] = true;
                    }
                }
            }
            return info;
        }
    }

    internal static class Anatomy {
        /// <summary>
        /// True if the kobold currently has a dick: either equipped through the game's dick system,
        /// or any active PenetrationTech penetrator on the character (covers modded rigs that have
        /// one built into the model).
        /// </summary>
        public static bool HasDick(Kobold k) {
            if (k == null) {
                return false;
            }
            if (k.activeDicks != null && k.activeDicks.Count > 0) {
                return true;
            }
            return k.GetComponentInChildren<PenetrationTech.Penetrator>(false) != null;
        }
    }
}
