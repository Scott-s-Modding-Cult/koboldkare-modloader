using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KoboldKare.ModKit;

namespace KoboldKareNpcAI {
    /// <summary>
    /// NPC AI: NPC kobolds roam the map and pair up at animation stations (or in place), with the
    /// penetrating role going to a kobold with a dick. Built on ModKit for its command, panel tab
    /// and per-frame tick.
    /// </summary>
    [BepInPlugin(Guid, "NPC AI", "2.0.0")]
    [BepInDependency(ModKit.Guid)]
    public class NpcAiPlugin : BaseUnityPlugin {
        // Same GUID as the standalone 1.x versions, so the existing config file carries over.
        public const string Guid = "koboldkare.npcai";

        internal static ManualLogSource Log;

        internal static ConfigEntry<float> RoamRadius;
        internal static ConfigEntry<float> IdleMinSeconds;
        internal static ConfigEntry<float> IdleMaxSeconds;
        internal static ConfigEntry<bool> Walk;
        internal static ConfigEntry<float> PairChance;
        internal static ConfigEntry<float> PairMinSeconds;
        internal static ConfigEntry<float> PairMaxSeconds;
        internal static ConfigEntry<float> PairCooldownSeconds;
        internal static ConfigEntry<float> MaxStationDistance;
        internal static ConfigEntry<float> InPlaceChance;
        internal static ConfigEntry<string> InPlaceStations;

        private void Awake() {
            Log = Logger;
            BindConfig();
            SelfCheck.Run(typeof(NpcAiPlugin).Assembly, Logger);

            ModKit.Tick += NpcDirector.Tick;
            ModCommands.Register("/npc-ai", "[on|off|status]  NPC kobolds roam and pair up (host only).", RunCommand,
                (index, text) => index == 1
                    ? new[] { "on", "off", "status" }.Where(o => o.StartsWith(text, System.StringComparison.OrdinalIgnoreCase))
                    : Enumerable.Empty<string>());
            ModPanel.AddPage("NPC AI", "ico_kobold_paperdoll", NpcAiPage.Build, NpcAiPage.Refresh, order: 10);

            Log.LogInfo("NPC AI loaded. Use /npc-ai or the mod panel to control it.");
        }

        private static void RunCommand(CommandContext ctx) {
            ctx.RequireHost();
            bool newState;
            switch (ctx.Arg(1)?.ToLowerInvariant()) {
                case null:
                    newState = !NpcDirector.Enabled;
                    break;
                case "on": case "1": case "true":
                    newState = true;
                    break;
                case "off": case "0": case "false":
                    newState = false;
                    break;
                case "status":
                    ctx.Output.Append(NpcDirector.GetStatus());
                    return;
                default:
                    ctx.Fail("Usage: /npc-ai [on|off|status]");
                    return;
            }
            NpcDirector.SetEnabled(newState);
            ctx.Reply(newState ? "NPC AI enabled: NPC kobolds will roam and pair up." : "NPC AI disabled.");
        }

        private void BindConfig() {
            RoamRadius = Config.Bind("Roaming", "RoamRadius", 25f,
                "How far (in meters) an NPC picks its next wander point from where it stands.");
            IdleMinSeconds = Config.Bind("Roaming", "IdleMinSeconds", 2f,
                "Minimum time an NPC stands around between walks.");
            IdleMaxSeconds = Config.Bind("Roaming", "IdleMaxSeconds", 8f,
                "Maximum time an NPC stands around between walks.");
            Walk = Config.Bind("Roaming", "Walk", true,
                "true = NPCs stroll at walking pace, false = they run.");

            PairChance = Config.Bind("Pairing", "PairChance", 0.35f,
                "Chance (0-1), each time an NPC finishes idling, that it starts looking for a partner.");
            PairMinSeconds = Config.Bind("Pairing", "PairMinSeconds", 25f,
                "Minimum length of a pairing.");
            PairMaxSeconds = Config.Bind("Pairing", "PairMaxSeconds", 60f,
                "Maximum length of a pairing.");
            PairCooldownSeconds = Config.Bind("Pairing", "PairCooldownSeconds", 45f,
                "How long an NPC waits after a pairing before it can pair again.");
            MaxStationDistance = Config.Bind("Pairing", "MaxStationDistance", 80f,
                "Only use animation stations within this distance of the pair.");

            InPlaceChance = Config.Bind("InPlace", "InPlaceChance", 0.5f,
                "Chance (0-1) that a pair meets where one of them stands, instead of walking to a station's " +
                "placed spot. A furniture-free station set is moved there for the animation and put back " +
                "afterwards. 0 disables. Single player / alone in the room only.");
            InPlaceStations = Config.Bind("InPlace", "InPlaceStations",
                "Generic.DoggystyleStation, Generic.LegUpStation, Generic.MacroStation, Generic.ProneMacroStation, FloorStation, CarpetStation",
                "Comma-separated station set names (partial match, case-insensitive) that may be used in-place. " +
                "Only list poses that don't need furniture (couch, table, bed...).");
        }
    }
}
