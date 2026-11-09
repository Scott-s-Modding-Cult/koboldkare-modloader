using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using KoboldKare.ModKit;
using UnityEngine;

namespace ExampleMod {
    /// <summary>
    /// A small, complete ModKit mod to copy from. It shows:
    ///  - a chat command with arguments and autocomplete (/example),
    ///  - a tab in the F8 mod panel with live status, config-bound controls and buttons,
    ///  - reacting to map loads and running per-frame work via ModKit's events.
    /// Create your own copy with:  powershell -File ModSource\new-mod.ps1 -Name MyMod
    /// </summary>
    [BepInPlugin(Guid, "Example Mod", "1.0.0")]
    [BepInDependency(ModKit.Guid)]               // load after ModKit
    public class ExampleModPlugin : BaseUnityPlugin {
        public const string Guid = "koboldkare.example";

        // Config lives in BepInEx/config/koboldkare.example.cfg; the panel edits it live.
        private static ConfigEntry<bool> greetOnMapLoad;
        private static ConfigEntry<float> scanInterval;

        private static int koboldCount;
        private static int npcCount;
        private static float nextScan;

        private void Awake() {
            greetOnMapLoad = Config.Bind("General", "GreetOnMapLoad", true, "Print a hello line in chat when a map loads.");
            scanInterval = Config.Bind("General", "ScanInterval", 2f, "Seconds between kobold counts.");

            // Catches anything this game stripped that the mod relies on, with a readable error.
            SelfCheck.Run(typeof(ExampleModPlugin).Assembly, Logger);

            // 1) A chat command. ctx.Arg(1) is the first word after the command.
            ModCommands.Register("/example", "[count|hello <name>]  ModKit example command.", ctx => {
                switch (ctx.Arg(1)) {
                    case "count":
                        ctx.Reply($"{koboldCount} kobolds on the map ({npcCount} NPCs).");
                        break;
                    case "hello":
                        ctx.Reply($"Hello, {ctx.Arg(2) ?? ctx.Caller.name}!");
                        break;
                    default:
                        ctx.Fail("Usage: /example [count|hello <name>]");
                        break;
                }
            }, autocomplete: (index, text) => index == 1
                ? new[] { "count", "hello" }.Where(o => o.StartsWith(text))
                : Enumerable.Empty<string>());

            // 2) A tab in the shared F8 panel.
            ModPanel.AddPage("Example", "ico_star", BuildPage, RefreshPage);

            // 3) Lifecycle events. Handlers are isolated: if one throws, ModKit logs it once and carries on.
            ModKit.MapLoaded += () => {
                if (greetOnMapLoad.Value) {
                    CheatsProcessor.AppendText("<#7dfff0>Example mod</color> says hi! Press F8 for the mod panel.\n");
                }
            };
            ModKit.Tick += CountKobolds;
        }

        private static void CountKobolds() {
            if (!ModKit.InLevel || Time.unscaledTime < nextScan) {
                return;
            }
            nextScan = Time.unscaledTime + scanInterval.Value;
            var kobolds = Object.FindObjectsOfType<Kobold>();
            koboldCount = kobolds.Length;
            npcCount = kobolds.Count(k => k.GetComponent<CharacterDescriptor>()?.GetPlayerControlled() == CharacterDescriptor.ControlType.AIPlayer);
        }

        // ------------------------------------------------------------------ panel page

        private static StatusCard status;

        private static void BuildPage(PageBuilder page) {
            status = page.Card(140f);

            page.Header("Settings", "ico_gear");
            page.Toggle("Say hi when a map loads", greetOnMapLoad);
            page.Slider("Count kobolds every", scanInterval, 0.5f, 10f, 0.5f, v => $"{v:0.#} s");

            page.Header("Actions", "ico_lightning");
            page.Buttons(
                new ButtonSpec("Say hi now", "ico_speaker", () => CheatsProcessor.AppendText("Hi from the mod panel!\n")),
                new ButtonSpec("Reset", "ico_trashcan", () => {
                    greetOnMapLoad.BoxedValue = greetOnMapLoad.DefaultValue;
                    scanInterval.BoxedValue = scanInterval.DefaultValue;
                    page.SyncFromConfig();
                }, ButtonStyle.Cancel));
            page.Note("This page is the ModKit example. Copy ModSource/ExampleMod (or run new-mod.ps1) to start your own mod.");
        }

        // Called about every 0.4s while this tab is visible.
        private static void RefreshPage() {
            status.Title.text = $"{Theme.Cyan}●</color> {koboldCount} kobolds";
            status.Body.text = $"{npcCount} of them are NPCs.\nYou are {(ModKit.IsHost ? "the host" : "a guest")}.";
        }
    }
}
