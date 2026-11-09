using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KoboldKare.ModKit;
using UnityEngine;

namespace ScottsTools {
    /// <summary>
    /// Scott's Tools: a grab bag of toys. Tool #1 is the RPG (and the cum RPG):
    /// grab it, fire it, and the rocket blasts kobolds away (ragdolled) without hurting anything.
    /// The cum RPG's rocket bursts into blobs of cum instead of a fireball.
    /// Spawn with /rpg, /rpg cum, the F8 panel, or the game's /give ScottsRPG / ScottsCumRPG.
    /// </summary>
    [BepInPlugin(Guid, "Scott's Tools", "1.0.0")]
    [BepInDependency(ModKit.Guid)]
    public class ScottsToolsPlugin : BaseUnityPlugin {
        public const string Guid = "koboldkare.scottstools";

        internal static ManualLogSource Log;

        internal static ConfigEntry<float> ReloadSeconds;
        internal static ConfigEntry<float> RocketSpeed;
        internal static ConfigEntry<float> BlastRadius;
        internal static ConfigEntry<float> KnockbackForce;
        internal static ConfigEntry<bool> Ragdoll;
        internal static ConfigEntry<bool> SelfKnockback;
        internal static ConfigEntry<float> CumKnockbackScale;
        internal static ConfigEntry<int> CumBlobs;
        internal static ConfigEntry<float> CumBlobVolume;

        private void Awake() {
            Log = Logger;
            ReloadSeconds = Config.Bind("RPG", "ReloadSeconds", 1.5f, "Seconds before the launcher can fire again.");
            RocketSpeed = Config.Bind("RPG", "RocketSpeed", 35f, "Rocket speed in meters per second.");
            BlastRadius = Config.Bind("RPG", "BlastRadius", 5f, "How far the blast reaches, in meters.");
            KnockbackForce = Config.Bind("RPG", "KnockbackForce", 3000f, "Push strength (the game's bombs use 3000).");
            Ragdoll = Config.Bind("RPG", "Ragdoll", true, "Knock kobolds into ragdoll for a few seconds when the blast hits them.");
            SelfKnockback = Config.Bind("RPG", "SelfKnockback", true, "The blast also pushes the shooter (rocket jumps!). The shooter is never ragdolled.");
            CumKnockbackScale = Config.Bind("CumRPG", "KnockbackScale", 0.5f, "Cum rocket push, as a fraction of KnockbackForce.");
            CumBlobs = Config.Bind("CumRPG", "Blobs", 14, "How many blobs of cum the rocket bursts into.");
            CumBlobVolume = Config.Bind("CumRPG", "BlobVolume", 6f, "Cum volume per blob.");

            SelfCheck.Run(typeof(ScottsToolsPlugin).Assembly, Logger);

            // Prefabs go into the game's pool as soon as its GameManager exists (main menu),
            // so saves and other players' spawns can find them.
            ModKit.Tick += () => RpgPrefabs.EnsureRegistered();

            ModCommands.Register("/rpg", "[cum]  Spawn Scott's rocket launcher (or the cum one) in front of you.", ctx => {
                if (!ModKit.InLevel || ctx.Caller == null) {
                    ctx.Fail("Load a map first.");
                }
                if (!RpgPrefabs.EnsureRegistered(force: true)) {
                    ctx.Fail("The RPG isn't ready yet, try again in a second.");
                }
                bool isCum = string.Equals(ctx.Arg(1), "cum", System.StringComparison.OrdinalIgnoreCase);
                RpgPrefabs.Spawn(ctx.Caller, isCum);
                ctx.Reply(isCum
                    ? "Spawned a <#ffb3d9>cum RPG</color>. Grab it and fire!"
                    : "Spawned an <#ffcc66>RPG</color>. Grab it and fire!");
            }, autocomplete: (index, text) => index == 1
                ? new[] { "cum" }.Where(o => o.StartsWith(text ?? ""))
                : Enumerable.Empty<string>());

            ModPanel.AddPage("Scott's Tools", "ico_lightning", BuildPage, RefreshPage);
        }

        // ------------------------------------------------------------------ panel page

        private static StatusCard status;

        private static void SpawnFromPanel(bool isCum) {
            Kobold me = ModKit.LocalKobold;
            if (!ModKit.InLevel || me == null) {
                CheatsProcessor.AppendText("<#ff9944>Load a map first.</color>\n");
                return;
            }
            if (!RpgPrefabs.EnsureRegistered(force: true)) {
                CheatsProcessor.AppendText("<#ff9944>The RPG isn't ready yet.</color>\n");
                return;
            }
            RpgPrefabs.Spawn(me, isCum);
        }

        private static void BuildPage(PageBuilder page) {
            status = page.Card(120f);

            page.Header("Rocket launcher", "ico_lightning");
            page.Buttons(
                new ButtonSpec("Spawn RPG", "ico_star", () => SpawnFromPanel(false)),
                new ButtonSpec("Spawn cum RPG", "ico_star", () => SpawnFromPanel(true)));

            page.Header("Blast", "ico_gear");
            page.Slider("Blast radius", BlastRadius, 1f, 15f, 0.5f, v => $"{v:0.#} m");
            page.Slider("Knockback", KnockbackForce, 0f, 10000f, 250f, v => $"{v:0}");
            page.Toggle("Ragdoll kobolds that get hit", Ragdoll);
            page.Toggle("Blast pushes the shooter too (rocket jump)", SelfKnockback);

            page.Header("Launcher", "ico_gear");
            page.Slider("Reload time", ReloadSeconds, 0.1f, 5f, 0.1f, v => $"{v:0.0} s");
            page.Slider("Rocket speed", RocketSpeed, 5f, 100f, 1f, v => $"{v:0} m/s");

            page.Header("Cum RPG", "ico_gear");
            page.Slider("Blobs per rocket", CumBlobs, 1, 40);
            page.Slider("Cum per blob", CumBlobVolume, 1f, 30f, 1f, v => $"{v:0}");
            page.Slider("Cum knockback", CumKnockbackScale, 0f, 1f, 0.05f, v => $"{v * 100f:0}%");

            page.Buttons(new ButtonSpec("Reset", "ico_trashcan", () => {
                foreach (ConfigEntryBase e in new ConfigEntryBase[] {
                             ReloadSeconds, RocketSpeed, BlastRadius, KnockbackForce, Ragdoll, SelfKnockback,
                             CumKnockbackScale, CumBlobs, CumBlobVolume }) {
                    e.BoxedValue = e.DefaultValue;
                }
                page.SyncFromConfig();
            }, ButtonStyle.Cancel));
            page.Note("Grab a launcher and use your fire/use button to shoot. Rockets never hurt anyone: they just " +
                      "blow kobolds away. Also spawnable with /rpg, /rpg cum, or /give ScottsRPG. Everyone in a room needs this mod.");
        }

        private static void RefreshPage() {
            bool ready = RpgPrefabs.EnsureRegistered();
            status.Title.text = ready ? $"{Theme.Cyan}●</color> RPGs ready" : "<#ff9944>●</color> Waiting for the game";
            status.Body.text = ModKit.InLevel
                ? "Spawn a launcher below, or type /rpg (or /rpg cum) in chat."
                : "Load a map to spawn launchers.";
        }
    }
}
