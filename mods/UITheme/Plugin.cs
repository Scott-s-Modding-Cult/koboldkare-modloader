using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using KoboldKare.ModKit;
using UnityEngine.SceneManagement;

namespace UITheme {
    /// <summary>
    /// UI themes for KoboldKare. Themes are folders (BepInEx/themes/&lt;Theme&gt;/theme.json) that recolour
    /// the game's UI by role, optionally swap fonts and sprites, and keep the game's icons. Pick one in
    /// Options > Graphics > UI Theme, the "Themes" tab of the F8 mod panel, or /theme.
    /// </summary>
    [BepInPlugin(Guid, "UI Theme", "1.0.0")]
    [BepInDependency(ModKit.Guid)]
    public class UIThemePlugin : BaseUnityPlugin {
        public const string Guid = "koboldkare.uitheme";

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> SelectedTheme;
        internal static ConfigEntry<string> OptionsGroup;
        internal static ConfigEntry<float> ScanInterval;

        private static bool appliedInitial;

        private void Awake() {
            Log = Logger;
            SelectedTheme = Config.Bind("General", "Theme", "",
                "Folder name of the active theme (empty = the game's own look). Normally set from the Options menu.");
            OptionsGroup = Config.Bind("Options", "Group", "GraphicsGroup",
                "Which Options tab shows the UI Theme dropdown: GraphicsGroup, GameplayGroup, AudioGroup, ControlsGroup, MultiplayerGroup.");
            ScanInterval = Config.Bind("Advanced", "ScanInterval", 1.5f,
                "Seconds between checks for newly created UI to restyle (menus are also restyled immediately when opened).");

            SelfCheck.Run(typeof(UIThemePlugin).Assembly, Logger);
            ThemeLibrary.Reload();

            var harmony = new Harmony(Guid);
            try {
                OptionsIntegration.Install(harmony);
                harmony.PatchAll(typeof(MenuOpenedPatch));
            } catch (Exception e) {
                Log.LogError("Couldn't hook the Options menu; themes still work from the mod panel and /theme: " + e);
            }

            SceneManager.sceneLoaded += (scene, mode) => Restyler.RequestScan();
            ModKit.MapLoaded += Restyler.RequestScan;
            ModKit.Tick += Tick;

            ModPanel.AddPage("Themes", "ico_star", ThemesPage.Build, ThemesPage.Refresh, order: 90);
            ThemeLibrary.ThemesReloaded += () => {
                ThemeSetting.Sync();
                ModPanel.AddPage("Themes", "ico_star", ThemesPage.Build, ThemesPage.Refresh, order: 90); // rebuild the list
            };
            ThemeImporter.ResultChanged += ThemeImportSetting.ResetToIdle; // show the result in the Options row

            ModCommands.Register("/theme", "[list|reload|import [path]|<name>]  Switch or import UI themes.", RunCommand,
                (index, text) => index == 1
                    ? new[] { "list", "reload", "import" }.Concat(ThemeLibrary.Themes.Where(t => !t.IsDefault).Select(t => t.Id))
                        .Where(o => o.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                    : Enumerable.Empty<string>());

            Log.LogInfo($"UI Theme loaded ({ThemeLibrary.Themes.Count - 1} theme(s) found).");
        }

        private static void Tick() {
            if (!appliedInitial) {
                appliedInitial = true;
                ThemeLibrary.Select(ThemeLibrary.Find(SelectedTheme.Value) ?? ThemeLibrary.Themes[0]);
            }
            ThemeImporter.Tick();
            Restyler.Tick();
        }

        private static void RunCommand(CommandContext ctx) {
            string arg = ctx.Args.Length > 1 ? string.Join(" ", ctx.Args.Skip(1).ToArray()).Trim() : "list";
            if (arg.Equals("import", StringComparison.OrdinalIgnoreCase)) {
                ThemeImporter.ImportFromDialog();
                ctx.Reply("Pick a theme .zip or theme.json in the file picker (it may open behind the game in exclusive fullscreen).");
                return;
            }
            if (arg.StartsWith("import ", StringComparison.OrdinalIgnoreCase)) {
                string path = arg.Substring("import ".Length);
                if (!ThemeImporter.Import(path)) {
                    ctx.Fail(ThemeImporter.LastResult);
                }
                ctx.Reply(ThemeImporter.LastResult);
                return;
            }
            switch (arg.ToLowerInvariant()) {
                case "list":
                    foreach (var t in ThemeLibrary.Themes) {
                        string marker = t == ThemeLibrary.Current ? $"{Theme.Cyan}●</color> " : "   ";
                        ctx.Reply($"{marker}{t.Name}{(t.IsDefault ? "" : $"  ({t.Id})")}");
                    }
                    ctx.Reply("Use /theme <name> to switch.");
                    return;
                case "reload":
                    ThemeLibrary.Reload();
                    ThemeLibrary.Reapply();
                    ctx.Reply($"Reloaded {ThemeLibrary.Themes.Count - 1} theme(s).");
                    return;
                case "default":
                case "none":
                    ThemeLibrary.Select(ThemeLibrary.Themes[0]);
                    ctx.Reply("Theme: game default.");
                    return;
            }
            var theme = ThemeLibrary.Find(arg);
            if (theme == null) {
                ctx.Fail($"No theme called '{arg}'. Use /theme list.");
            }
            ThemeLibrary.Select(theme);
            ctx.Reply($"Theme: {theme.Name}");
        }

        /// <summary>Restyle menus the moment they open instead of on the next periodic scan.</summary>
        [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.ShowMenuStatic))]
        private static class MenuOpenedPatch {
            [HarmonyPostfix]
            private static void Postfix() => Restyler.RequestScan();
        }
    }
}
