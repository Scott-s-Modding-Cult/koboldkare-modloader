using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.AI;

namespace KoboldKare.ModKit {
    /// <summary>
    /// NavMesh queries for mods. KoboldKare ships a stripped UnityEngine.AIModule without
    /// NavMeshPath/CalculatePath; the full copy in BepInEx/unstripped_corlib restores it. If that copy
    /// is missing, these calls return "no result" (and log one warning) instead of throwing, so callers
    /// can fall back to straight lines.
    /// </summary>
    public static class Nav {
        private static bool unavailable;
        private static bool warned;

        /// <summary>False once the NavMesh API turned out to be missing.</summary>
        public static bool Available => !unavailable;

        /// <summary>Path corners from <paramref name="from"/> to <paramref name="to"/>, or null.</summary>
        /// <param name="startSearch">How far from <paramref name="from"/> to look for the navmesh.</param>
        /// <param name="requireComplete">Reject partial paths (destination not reachable).</param>
        /// <param name="hasNavMesh">Whether there is navmesh near <paramref name="from"/> at all.</param>
        public static Vector3[] TryGetPath(Vector3 from, Vector3 to, float startSearch, bool requireComplete, out bool hasNavMesh) {
            hasNavMesh = false;
            if (unavailable) {
                return null;
            }
            try {
                return Query(from, to, startSearch, requireComplete, out hasNavMesh);
            } catch (Exception e) when (IsMissingApi(e)) {
                MarkUnavailable(e);
                return null;
            }
        }

        /// <summary>Nearest navmesh point within <paramref name="radius"/>.</summary>
        public static bool TrySample(Vector3 point, float radius, out Vector3 result) {
            result = point;
            if (unavailable) {
                return false;
            }
            try {
                return Sample(point, radius, out result);
            } catch (Exception e) when (IsMissingApi(e)) {
                MarkUnavailable(e);
                return false;
            }
        }

        /// <summary>Height of the walkable ground under <paramref name="point"/>, ignoring kobolds.</summary>
        public static bool TryGroundY(Vector3 point, out float y) {
            y = 0f;
            // (Physics.DefaultRaycastLayers is stripped in this game; ~0 = all layers.)
            int mask = GameManager.instance != null ? (int)GameManager.instance.walkableGroundMask : ~0;
            var hits = Physics.RaycastAll(point + Vector3.up * 1.5f, Vector3.down, 8f, mask, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            foreach (var hit in hits) {
                if (hit.collider == null || hit.collider.GetComponentInParent<Kobold>() != null) {
                    continue;
                }
                if (hit.distance < best) {
                    best = hit.distance;
                    y = hit.point.y;
                }
            }
            if (!float.IsPositiveInfinity(best)) {
                return true;
            }
            if (TrySample(point, 3f, out var navPoint)) {
                y = navPoint.y;
                return true;
            }
            return false;
        }

        private static bool IsMissingApi(Exception e) =>
            e is TypeLoadException || e is MissingMethodException || e is MissingFieldException;

        private static void MarkUnavailable(Exception e) {
            unavailable = true;
            if (!warned) {
                warned = true;
                ModKit.Log.LogWarning(
                    "NavMesh API unavailable (is BepInEx/unstripped_corlib/UnityEngine.AIModule.dll installed?). " +
                    e.GetType().Name + ": " + e.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Sample(Vector3 point, float radius, out Vector3 result) {
            bool ok = NavMesh.SamplePosition(point, out var hit, radius, NavMesh.AllAreas);
            result = ok ? hit.position : point;
            return ok;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3[] Query(Vector3 from, Vector3 to, float startSearch, bool requireComplete, out bool hasNavMesh) {
            hasNavMesh = NavMesh.SamplePosition(from, out var startHit, startSearch, NavMesh.AllAreas);
            if (!hasNavMesh || !NavMesh.SamplePosition(to, out var endHit, 6f, NavMesh.AllAreas)) {
                return null;
            }
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, path)) {
                return null;
            }
            if (path.status == NavMeshPathStatus.PathInvalid || path.corners.Length == 0) {
                return null;
            }
            if (requireComplete && path.status != NavMeshPathStatus.PathComplete) {
                return null;
            }
            return path.corners;
        }
    }
}
