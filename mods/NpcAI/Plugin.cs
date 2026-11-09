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
            // Ranges match the panel sliders. BepInEx clamps to them on load and on every set, so a
            // hand-edited .cfg can't push values the AI was never meant to run with.
            RoamRadius = Bind("Roaming", "RoamRadius", 25f, 5f, 80f,
                "How far (in meters) an NPC picks its next wander point from where it stands.");
            IdleMinSeconds = Bind("Roaming", "IdleMinSeconds", 2f, 0f, 30f,
                "Minimum time an NPC stands around between walks.");
            IdleMaxSeconds = Bind("Roaming", "IdleMaxSeconds", 8f, 0f, 60f,
                "Maximum time an NPC stands around between walks.");
            Walk = Config.Bind("Roaming", "Walk", true,
                "true = NPCs stroll at walking pace, false = they run.");

            PairChance = Bind("Pairing", "PairChance", 0.35f, 0f, 1f,
                "Chance (0-1), each time an NPC finishes idling, that it starts looking for a partner.");
            PairMinSeconds = Bind("Pairing", "PairMinSeconds", 25f, 5f, 180f,
                "Minimum length of a pairing.");
            PairMaxSeconds = Bind("Pairing", "PairMaxSeconds", 60f, 5f, 300f,
                "Maximum length of a pairing.");
            PairCooldownSeconds = Bind("Pairing", "PairCooldownSeconds", 45f, 0f, 300f,
                "How long an NPC waits after a pairing before it can pair again.");
            MaxStationDistance = Bind("Pairing", "MaxStationDistance", 80f, 10f, 300f,
                "Only use animation stations within this distance of the pair.");

            InPlaceChance = Bind("InPlace", "InPlaceChance", 0.5f, 0f, 1f,
                "Chance (0-1) that a pair meets where one of them stands, instead of walking to a station's " +
                "placed spot. A furniture-free station set is moved there for the animation and put back " +
                "afterwards. 0 disables. Single player / alone in the room only.");
            InPlaceStations = Config.Bind("InPlace", "InPlaceStations",
                "Generic.DoggystyleStation, Generic.LegUpStation, Generic.MacroStation, Generic.ProneMacroStation, FloorStation, CarpetStation",
                "Comma-separated station set names (partial match, case-insensitive) that may be used in-place. " +
                "Only list poses that don't need furniture. Sets whose name mentions furniture (couch, bed, " +
                "table, chair, wall...) are always skipped, even if listed here.");

            FixOrder(IdleMinSeconds, IdleMaxSeconds);
            FixOrder(PairMinSeconds, PairMaxSeconds);
        }

        private ConfigEntry<float> Bind(string section, string key, float value, float min, float max, string description) {
            var entry = Config.Bind(section, key, value,
                new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
            // The range check lets NaN/Infinity through, so catch those here.
            if (float.IsNaN(entry.Value) || float.IsInfinity(entry.Value)) {
                Log.LogWarning($"Config {section}.{key} was not a number; reset to {value}.");
                entry.Value = value;
            }
            return entry;
        }

        /// <summary>A shortest/longest pair edited by hand into the wrong order: raise the longest to match.</summary>
        private static void FixOrder(ConfigEntry<float> min, ConfigEntry<float> max) {
            if (min.Value > max.Value) {
                Log.LogWarning($"Config {min.Definition.Key} ({min.Value}) was above {max.Definition.Key} ({max.Value}); " +
                               $"set {max.Definition.Key} to {min.Value}.");
                max.Value = min.Value;
            }
        }
    }
}
