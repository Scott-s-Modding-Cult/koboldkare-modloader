using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace ScottsTools {
    /// <summary>
    /// Builds the two RPG prefabs at runtime and registers them in the game's Photon prefab pool,
    /// so <c>PhotonNetwork.Instantiate("ScottsRPG")</c> (and the game's own /give) can spawn them.
    /// Every player in a room needs this mod for the RPGs to show up for them.
    /// </summary>
    internal static class RpgPrefabs {
        public const string NormalKey = "ScottsRPG";
        public const string CumKey = "ScottsCumRPG";

        /// <summary>Inactive, never-destroyed parent for templates (children don't wake up).</summary>
        public static Transform Holder { get; private set; }
        private static GameObject normal;
        private static GameObject cum;
        private static PreparePool pool;
        private static float nextCheck;
        private static bool ready;

        /// <summary>Registers the prefabs once the game's pool exists. Cheap to call every frame.</summary>
        public static bool EnsureRegistered(bool force = false) {
            if (!force && Time.unscaledTime < nextCheck) {
                return ready;
            }
            nextCheck = Time.unscaledTime + 2f;
            try {
                ready = Register();
            } catch (System.Exception e) {
                ready = false;
                ScottsToolsPlugin.Log.LogError("Couldn't register the RPGs: " + e);
                nextCheck = Time.unscaledTime + 30f;
            }
            return ready;
        }

        private static bool Register() {

            // PreparePool lives on the GameManager, which only exists after the first scene load.
            // Waiting for it also keeps PhotonNetwork untouched during plugin load (see ModKit notes).
            if (pool == null) {
                pool = Object.FindObjectOfType<PreparePool>();
                if (pool == null) {
                    return false;
                }
            }
            if (!(PhotonNetwork.PrefabPool is DefaultPool photonPool)) {
                return false;
            }

            if (normal == null) {
                RpgAssets.Load();
                var holder = new GameObject("ScottsTools Templates");
                holder.SetActive(false);
                Object.DontDestroyOnLoad(holder);
                Holder = holder.transform;
                normal = Build(NormalKey, false);
                cum = Build(CumKey, true);
                ScottsToolsPlugin.Log.LogInfo("Built the RPG prefabs.");
            }
            // Re-add if the game's mod manager ever rebuilt the pool.
            if (!photonPool.ResourceCache.ContainsKey(NormalKey)) {
                PreparePool.AddPrefab(NormalKey, normal, null);
                ScottsToolsPlugin.Log.LogInfo($"Registered {NormalKey}.");
            }
            if (!photonPool.ResourceCache.ContainsKey(CumKey)) {
                PreparePool.AddPrefab(CumKey, cum, null);
                ScottsToolsPlugin.Log.LogInfo($"Registered {CumKey}.");
            }
            return true;
        }

        public static GameObject Spawn(Kobold near, bool isCum) {
            Transform t = near.hip != null ? near.hip : near.transform;
            Vector3 forward = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) {
                forward = Vector3.forward;
            }
            forward.Normalize();
            Vector3 position = t.position + forward * 1.2f + Vector3.up * 0.4f;
            // Lay it sideways in front of the player, pointing to their right.
            Quaternion rotation = Quaternion.LookRotation(Vector3.Cross(Vector3.up, forward), Vector3.up);
            return PhotonNetwork.Instantiate(isCum ? CumKey : NormalKey, position, rotation);
        }

        private static GameObject Build(string name, bool isCum) {
            int layer = LayerMask.NameToLayer("UsablePickups");
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(Holder, false);

            var body = go.AddComponent<Rigidbody>();
            body.mass = 3f;
            body.drag = 0.05f;
            body.angularDrag = 0.5f;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            // Same network setup as the game's Scanner: takeover ownership, smoothed rigidbody sync.
            var view = go.AddComponent<PhotonView>();
            view.OwnershipTransfer = OwnershipOption.Takeover;
            view.Synchronization = ViewSynchronization.UnreliableOnChange;
            var smooth = go.AddComponent<SmoothRigidbodyPhoton>();
            view.ObservedComponents = new List<Component> { smooth };

            var model = Child(go.transform, "Model", Vector3.zero, layer);
            model.gameObject.AddComponent<MeshFilter>().sharedMesh = RpgAssets.Launcher;
            model.gameObject.AddComponent<MeshRenderer>().sharedMaterial = isCum ? RpgAssets.CumLauncherMaterial : RpgAssets.LauncherMaterial;

            var loaded = Child(go.transform, "LoadedRocket", Vector3.zero, layer);
            loaded.gameObject.AddComponent<MeshFilter>().sharedMesh = RpgAssets.Warhead;
            loaded.gameObject.AddComponent<MeshRenderer>().sharedMaterial = isCum ? RpgAssets.CumWarheadMaterial : RpgAssets.WarheadMaterial;

            Transform barrel = Child(go.transform, "Barrel", new Vector3(0f, 0f, RpgAssets.MuzzleZ), layer);
            // Hold it by the pistol grip area.
            Transform grab = Child(go.transform, "Grab", new Vector3(0f, 0f, RpgAssets.Z(0.6f)), layer);

            // Tube (including the loaded rocket) and the grips below it.
            var tube = go.AddComponent<BoxCollider>();
            float tubeFront = RpgAssets.TipZ;
            float tubeBack = -RpgAssets.Length + RpgAssets.TipZ;
            tube.center = new Vector3(0f, 0f, (tubeFront + tubeBack) * 0.5f);
            tube.size = new Vector3(0.1f, 0.1f, tubeFront - tubeBack);
            var grips = go.AddComponent<BoxCollider>();
            float gripTop = RpgAssets.Y(0.2f), gripBottom = RpgAssets.Y(-0.77f);
            float gripBack = RpgAssets.Z(-0.45f), gripFront = RpgAssets.Z(1.2f);
            grips.center = new Vector3(0f, (gripTop + gripBottom) * 0.5f, (gripBack + gripFront) * 0.5f);
            grips.size = new Vector3(0.05f, gripTop - gripBottom, gripFront - gripBack);

            var weapon = go.AddComponent<RpgWeapon>();
            weapon.isCum = isCum;
            weapon.barrel = barrel;
            weapon.grab = grab;
            weapon.loadedRocket = loaded.gameObject;
            return go;
        }

        private static Transform Child(Transform parent, string name, Vector3 localPosition, int layer) {
            var child = new GameObject(name) { layer = layer }.transform;
            child.SetParent(parent, false);
            child.localPosition = localPosition;
            return child;
        }
    }
}
