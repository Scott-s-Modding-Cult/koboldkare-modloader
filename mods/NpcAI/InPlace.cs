using System;
using System.Collections.Generic;
using System.Linq;
using KoboldKare.ModKit;
using UnityEngine;

namespace KoboldKareNpcAI {
    /// <summary>
    /// In-place pairing: instead of walking to where a station set is placed in the map, the pair
    /// meets where one of them stands and a furniture-free station set is temporarily moved there.
    /// After the animation (and its blend-out) the set is put back exactly where it was.
    ///
    /// Station sets are scene objects whose position isn't network-synced, so this only runs in
    /// single player / when you're alone in the room; other players would see the animation at
    /// the set's original spot.
    /// </summary>
    internal static class InPlace {
        private static string parsedFrom;
        private static string[] names = new string[0];

        public static bool Allowed => NpcAiPlugin.InPlaceChance.Value > 0f && ModKit.IsAlone;

        /// <summary>
        /// Name fragments of station sets that are posed on furniture. Moving one of those to an empty
        /// spot would leave the kobolds animating in mid-air, so they're never used in-place, whatever
        /// InPlaceStations says. ("car" is deliberately missing: it would match CarpetStation.)
        /// </summary>
        private static readonly string[] FurnitureWords = {
            "couch", "sofa", "bed", "table", "chair", "bench", "desk", "stool", "seat", "throne",
            "toilet", "bath", "tub", "shower", "sink", "counter", "wall", "stocks", "pillory",
            "swing", "sling",
        };

        private static readonly HashSet<string> WarnedFurniture = new HashSet<string>();

        public static bool IsCandidate(BreedingStation set) {
            string raw = NpcAiPlugin.InPlaceStations.Value ?? "";
            if (raw != parsedFrom) {
                parsedFrom = raw;
                names = raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            }
            string name = set.name;
            bool listed = false;
            foreach (var n in names) {
                if (name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) {
                    listed = true;
                    break;
                }
            }
            if (!listed) {
                return false;
            }
            string furniture = FurnitureWords.FirstOrDefault(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
            if (furniture != null) {
                if (WarnedFurniture.Add(name)) {
                    NpcAiPlugin.Log.LogWarning(
                        $"InPlaceStations matches '{name}', but it looks like a furniture pose (\"{furniture}\"); " +
                        "it won't be used in-place.");
                }
                return false;
            }
            return true;
        }

        public class Placement {
            public BreedingStation Set;
            public Vector3 OriginalPosition;
            public Quaternion OriginalRotation;
        }

        /// <summary>
        /// Moves <paramref name="set"/> so its slots sit at <paramref name="anchor"/>, at the same height
        /// above the ground as in its original spot, turned to face <paramref name="faceDirection"/>.
        /// </summary>
        public static Placement Move(BreedingStation set, Vector3 anchor, Vector3 faceDirection) {
            var t = set.transform;
            var placement = new Placement { Set = set, OriginalPosition = t.position, OriginalRotation = t.rotation };

            // Pivot = centre of the slots; keep its original height above whatever it stood on.
            Vector3 pivot = Vector3.zero;
            int count = 0;
            foreach (var station in set.GetAnimationStations()) {
                if (station != null) {
                    pivot += station.transform.position;
                    count++;
                }
            }
            pivot = count > 0 ? pivot / count : t.position;
            float height = TryGroundY(pivot, out float originalGround) ? pivot.y - originalGround : 0f;
            float targetGround = TryGroundY(anchor, out float g) ? g : anchor.y;
            Vector3 targetPivot = new Vector3(anchor.x, targetGround + height, anchor.z);

            Quaternion yaw = Quaternion.identity;
            Vector3 from = Flat(t.forward);
            Vector3 to = Flat(faceDirection);
            if (from.sqrMagnitude > 1e-4f && to.sqrMagnitude > 1e-4f) {
                yaw = Quaternion.Euler(0f, Vector3.SignedAngle(from, to, Vector3.up), 0f);
            }

            t.rotation = yaw * placement.OriginalRotation;
            t.position = targetPivot + yaw * (placement.OriginalPosition - pivot);
            return placement;
        }

        public static void Restore(Placement placement) {
            if (placement?.Set == null) {
                return;
            }
            placement.Set.transform.rotation = placement.OriginalRotation;
            placement.Set.transform.position = placement.OriginalPosition;
        }

        private static bool TryGroundY(Vector3 point, out float y) => Nav.TryGroundY(point, out y);

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
