using System;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace KoboldKare.ModKit {
    [BepInPlugin(ModKit.Guid, "KoboldKare ModKit", ModKit.Version)]
    public class ModKitPlugin : BaseUnityPlugin {
        internal static ConfigEntry<string> PanelHotkey;

        /// <summary>Panel hotkey name; falls back to F8 if the config couldn't be read.</summary>
        internal static string PanelHotkeyName => PanelHotkey?.Value ?? DefaultHotkey;

        private const string DefaultHotkey = "F8";

        // Unity's log isn't forwarded to BepInEx in this game, so an exception escaping Awake would be
        // invisible to players. Every step runs on its own and reports to the BepInEx log; a failed step
        // doesn't stop the rest (above all the runner, which drives F8 and the commands). Each step is a
        // separate non-inlined method so that a type/JIT failure inside it is also caught here.
        private void Awake() {
            ModKit.Log = Logger;
            int failed = 0;
            failed += Step("read config", BindConfig);
            failed += Step("run self-check", RunSelfCheck);
            failed += Step("hook CheatsProcessor.Awake (commands and map events won't work)", PatchGame);
            failed += Step("register built-in commands", RegisterCommands);
            failed += Step("create the runner (F8 panel and commands won't work)", CreateRunner);
            if (failed == 0) {
                Logger.LogInfo($"ModKit {ModKit.Version} ready.");
            } else {
                Logger.LogWarning($"ModKit {ModKit.Version} started with {failed} problem(s); see errors above.");
            }
        }

        private int Step(string what, Action action) {
            try {
                action();
                return 0;
            } catch (Exception e) {
                Logger.LogError($"Failed to {what}: {e}");
                return 1;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void BindConfig() {
            PanelHotkey = Config.Bind("Panel", "Hotkey", DefaultHotkey,
                "Key that opens/closes the mod panel while in a map. Any UnityEngine.InputSystem.Key name (F8, F9, Backquote, ...).");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RunSelfCheck() => SelfCheck.Run(typeof(ModKitPlugin).Assembly, Logger);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PatchGame() => new Harmony(ModKit.Guid).PatchAll(typeof(CheatsProcessorAwakePatch));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterCommands() => ModCommands.RegisterBuiltins();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CreateRunner() => ModKitRunner.Ensure();
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
