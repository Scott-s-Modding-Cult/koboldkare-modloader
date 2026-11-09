using BepInEx.Configuration;
using KoboldKare.ModKit;

namespace KoboldKareNpcAI {
    /// <summary>NPC AI's tab in the ModKit panel.</summary>
    internal static class NpcAiPage {
        private static PageBuilder page;
        private static StatusCard status;
        private static PanelButton toggle;
        private static PanelButton endAll;

        public static void Build(PageBuilder p) {
            page = p;
            status = p.Card();
            toggle = p.Button("NPC AI", "ico_toggleoff", ToggleAi);
            p.Space(8f);

            p.Header("Roaming", "ico_feets");
            p.Slider("Wander distance", NpcAiPlugin.RoamRadius, 5f, 80f, 1f, v => $"{v:0} m");
            p.Slider("Idle time (shortest)", NpcAiPlugin.IdleMinSeconds, 0f, 30f, 0.5f, v => $"{v:0.#} s",
                () => KeepOrdered(NpcAiPlugin.IdleMinSeconds, NpcAiPlugin.IdleMaxSeconds, minChanged: true));
            p.Slider("Idle time (longest)", NpcAiPlugin.IdleMaxSeconds, 0f, 60f, 0.5f, v => $"{v:0.#} s",
                () => KeepOrdered(NpcAiPlugin.IdleMinSeconds, NpcAiPlugin.IdleMaxSeconds, minChanged: false));
            p.Toggle("Stroll at walking pace", NpcAiPlugin.Walk);
            p.Space(12f);

            p.Header("Pairing", "ico_breedingmount");
            p.Slider("Chance to look for a partner", NpcAiPlugin.PairChance, 0f, 1f, 0.05f, v => $"{v * 100f:0}%");
            p.Slider("Pairing length (shortest)", NpcAiPlugin.PairMinSeconds, 5f, 180f, 1f, v => $"{v:0} s",
                () => KeepOrdered(NpcAiPlugin.PairMinSeconds, NpcAiPlugin.PairMaxSeconds, minChanged: true));
            p.Slider("Pairing length (longest)", NpcAiPlugin.PairMaxSeconds, 5f, 300f, 1f, v => $"{v:0} s",
                () => KeepOrdered(NpcAiPlugin.PairMinSeconds, NpcAiPlugin.PairMaxSeconds, minChanged: false));
            p.Slider("Rest after pairing", NpcAiPlugin.PairCooldownSeconds, 0f, 300f, 5f, v => $"{v:0} s");
            p.Slider("Furthest station to walk to", NpcAiPlugin.MaxStationDistance, 10f, 300f, 5f, v => $"{v:0} m");
            p.Space(12f);

            p.Header("In place", "ico_pinned");
            p.Slider("Chance to pair where they stand", NpcAiPlugin.InPlaceChance, 0f, 1f, 0.05f, v => $"{v * 100f:0}%");
            p.Note("Single player only. Station sets used: " + NpcAiPlugin.InPlaceStations.Value);

            var buttons = p.Buttons(
                new ButtonSpec("End all pairs", "ico_stop", EndAll, ButtonStyle.Cancel),
                new ButtonSpec("Reset to defaults", Theme.FirstAvailable("ico_refresh", "ico_save", "ico_rounded_diamond"),
                    ResetDefaults, ButtonStyle.Okay));
            endAll = buttons[0];
        }

        public static void Refresh() {
            var s = NpcDirector.GetSnapshot();
            bool host = ModKit.IsHost;

            if (!host) {
                status.Title.text = $"{Theme.Red}●</color> Host only";
                status.Body.text = "Only the host of this game can control the NPCs.";
            } else if (!s.Enabled) {
                status.Title.text = $"{Theme.Red}●</color> Stopped";
                status.Body.text = "NPC kobolds stand around as usual.\nTurn the AI on to let them roam and pair up.";
            } else {
                int Count(NpcState st) => s.States.TryGetValue(st, out int n) ? n : 0;
                string inPlace = InPlace.Allowed
                    ? "available"
                    : NpcAiPlugin.InPlaceChance.Value <= 0f ? "off (chance is 0)" : "off (other players here)";
                status.Title.text = $"{Theme.Cyan}●</color> Running";
                status.Body.text =
                    $"NPCs: {Theme.Cyan}{s.Npcs}</color>   ({s.WithDick} with a dick)\n" +
                    $"Pairs: {Theme.Cyan}{s.Pairs}</color>   ({s.InPlacePairs} in place)\n" +
                    $"Roaming {Count(NpcState.Roaming)} · Idle {Count(NpcState.Idle)} · " +
                    $"Meeting {Count(NpcState.ToStation) + Count(NpcState.WaitingAtStation)} · " +
                    $"Paired {Count(NpcState.Paired)} · Busy {Count(NpcState.Busy)}\n" +
                    $"In-place pairing: {inPlace}";
            }

            toggle.Interactable = host;
            toggle.SetIcon(s.Enabled ? "ico_toggleon" : "ico_toggleoff");
            toggle.SetLabel(s.Enabled ? "NPC AI is on  -  click to stop" : "NPC AI is off  -  click to start");
            endAll.Interactable = host && s.Enabled && s.Pairs > 0;
        }

        private static void ToggleAi() {
            if (!ModKit.IsHost) {
                return;
            }
            NpcDirector.SetEnabled(!NpcDirector.Enabled);
            Refresh();
        }

        private static void EndAll() {
            NpcDirector.EndAllPairings();
            Refresh();
        }

        private static void ResetDefaults() {
            foreach (var entry in new ConfigEntryBase[] {
                NpcAiPlugin.RoamRadius, NpcAiPlugin.IdleMinSeconds, NpcAiPlugin.IdleMaxSeconds, NpcAiPlugin.Walk,
                NpcAiPlugin.PairChance, NpcAiPlugin.PairMinSeconds, NpcAiPlugin.PairMaxSeconds, NpcAiPlugin.PairCooldownSeconds,
                NpcAiPlugin.MaxStationDistance, NpcAiPlugin.InPlaceChance, NpcAiPlugin.InPlaceStations,
            }) {
                entry.BoxedValue = entry.DefaultValue;
            }
            page.SyncFromConfig();
            Refresh();
        }

        // Keep shortest <= longest.
        private static void KeepOrdered(ConfigEntry<float> min, ConfigEntry<float> max, bool minChanged) {
            if (min.Value <= max.Value) {
                return;
            }
            if (minChanged) {
                max.Value = min.Value;
            } else {
                min.Value = max.Value;
            }
            page.SyncFromConfig();
        }
    }
}
