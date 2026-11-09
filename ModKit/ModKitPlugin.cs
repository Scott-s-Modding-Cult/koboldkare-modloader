using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace KoboldKare.ModKit {
    [BepInPlugin(ModKit.Guid, "KoboldKare ModKit", ModKit.Version)]
    public class ModKitPlugin : BaseUnityPlugin {
        internal static ConfigEntry<string> PanelHotkey;

        private void Awake() {
            ModKit.Log = Logger;

            PanelHotkey = Config.Bind("Panel", "Hotkey", "F8",
                "Key that opens/closes the mod panel while in a map. Any UnityEngine.InputSystem.Key name (F8, F9, Backquote, ...).");

            SelfCheck.Run(typeof(ModKitPlugin).Assembly, Logger);

            try {
                new Harmony(ModKit.Guid).PatchAll(typeof(CheatsProcessorAwakePatch));
            } catch (Exception e) {
                Logger.LogError("Failed to hook CheatsProcessor.Awake; commands and map events won't work: " + e);
            }

            ModCommands.RegisterBuiltins();
            ModKitRunner.Ensure();
            Logger.LogInfo($"ModKit {ModKit.Version} ready.");
        }
    }

    /// <summary>
    /// CheatsProcessor is created with every map. Hooking its Awake is the one reliable signal that a
    /// map is up, and keeps working even though the game destroys BepInEx's manager object (and any
    /// DontDestroyOnLoad helpers) during loading.
    /// </summary>
    [HarmonyPatch(typeof(CheatsProcessor), "Awake")]
    internal static class CheatsProcessorAwakePatch {
        [HarmonyPostfix]
        private static void Postfix() {
            ModCommands.SyncWithGame();
            ModKitRunner.Ensure();
            ModKit.RaiseMapLoaded();
        }
    }

    /// <summary>Per-frame driver. Recreated on every map load if the game destroyed it.</summary>
    internal class ModKitRunner : MonoBehaviour {
        private static ModKitRunner instance;

        public static void Ensure() {
            if (instance != null) {
                return;
            }
            try {
                var go = new GameObject("ModKit_Runner");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<ModKitRunner>();
            } catch (Exception e) {
                ModKit.Log.LogError("Failed to create the ModKit runner: " + e);
            }
        }

        private void Update() {
            ModCommands.SyncWithGame();
            try {
                ModPanel.Update();
            } catch (Exception e) {
                ModPanel.ReportError(e);
            }
            ModKit.RaiseTick();
        }

        private void OnDestroy() {
            if (instance == this) {
                instance = null;
            }
        }
    }
}
