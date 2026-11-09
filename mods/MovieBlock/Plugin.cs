using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KoboldKare.ModKit;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MovieBlock {
    /// <summary>
    /// A floating video screen that browses e621 or rule34.xxx by tag and plays videos in the world.
    /// /movieblock e621 | r34 opens it in front of you; F7 switches between using the screen and playing.
    /// </summary>
    [BepInPlugin(Guid, "Movie Block", "1.0.0")]
    [BepInDependency(ModKit.Guid)]
    public class MovieBlockPlugin : BaseUnityPlugin {
        public const string Guid = "koboldkare.movieblock";

        internal static ManualLogSource Log;

        internal static ConfigEntry<string> FocusKey;
        internal static ConfigEntry<float> Size;
        internal static ConfigEntry<float> Distance;
        internal static ConfigEntry<bool> FaceCamera;
        internal static ConfigEntry<bool> Follow;
        internal static ConfigEntry<float> Volume;
        internal static ConfigEntry<bool> SpatialAudio;
        internal static ConfigEntry<bool> Loop;
        internal static ConfigEntry<bool> AutoNext;
        internal static ConfigEntry<string> Quality;
        internal static ConfigEntry<int> MaxCacheMB;
        internal static ConfigEntry<string> Blacklist;
        internal static ConfigEntry<string> E621DefaultTags;
        internal static ConfigEntry<string> E621User;
        internal static ConfigEntry<string> E621ApiKey;
        internal static ConfigEntry<string> R34DefaultTags;
        internal static ConfigEntry<string> R34UserId;
        internal static ConfigEntry<string> R34ApiKey;

        private static string parsedKeyName;
        private static Key? parsedKey;
        private static bool videoModuleOk;

        private static readonly string[] Subcommands = {
            "e621", "r34", "close", "here", "focus", "follow", "search", "quality", "volume", "size", "distance",
            "r34key", "e621key", "cache", "status",
        };

        private void Awake() {
            Log = Logger;

            FocusKey = Config.Bind("General", "FocusKey", "F7",
                "Switches between using the screen (free cursor) and playing. Any UnityEngine.InputSystem.Key name.");
            Size = Config.Bind("General", "Size", 2.4f, new ConfigDescription("Screen width in metres.", new AcceptableValueRange<float>(0.5f, 10f)));
            Distance = Config.Bind("General", "Distance", 2.5f,
                new ConfigDescription("How far in front of you the screen appears, in metres.", new AcceptableValueRange<float>(0.5f, 20f)));
            FaceCamera = Config.Bind("General", "FaceCamera", true, "Keep the screen turned towards you.");
            Follow = Config.Bind("General", "Follow", false, "Drift back in front of you when you walk away or turn.");
            Volume = Config.Bind("Playback", "Volume", 0.8f, new ConfigDescription("Video volume.", new AcceptableValueRange<float>(0f, 1f)));
            SpatialAudio = Config.Bind("Playback", "SpatialAudio", true, "3D sound that comes from the screen. Off = same volume everywhere.");
            Loop = Config.Bind("Playback", "Loop", true, "Loop the current video.");
            AutoNext = Config.Bind("Playback", "AutoNext", false, "When Loop is off, play the next result when a video ends.");
            Quality = Config.Bind("Playback", "Quality", "720p",
                new ConfigDescription("e621 video quality.", new AcceptableValueList<string>("480p", "720p", "original")));
            MaxCacheMB = Config.Bind("Playback", "MaxCacheMB", 1500,
                "Downloaded videos are kept in %TEMP%\\KoboldKare-MovieBlock up to this size, oldest removed first.");
            Blacklist = Config.Bind("Search", "Blacklist", "gore scat watersports",
                "Tags never shown (space separated). Content involving minors is always excluded.");
            E621DefaultTags = Config.Bind("e621", "DefaultTags", "kobold", "Search run when the screen opens on e621.");
            E621User = Config.Bind("e621", "Username", "", "Optional e621 login (with ApiKey) so your account's settings apply.");
            E621ApiKey = Config.Bind("e621", "ApiKey", "", "Optional e621 API key (e621 > Account > Manage API Access).");
            R34DefaultTags = Config.Bind("rule34", "DefaultTags", "kobold", "Search run when the screen opens on rule34.");
            R34UserId = Config.Bind("rule34", "UserId", "", "rule34.xxx user id for the API (set with /movieblock r34key).");
            R34ApiKey = Config.Bind("rule34", "ApiKey", "", "rule34.xxx API key (set with /movieblock r34key).");

            SelfCheck.Run(typeof(MovieBlockPlugin).Assembly, Logger);
            videoModuleOk = CheckVideoModule();

            ModCommands.Register("/movieblock", "[e621|r34|close|here|focus|follow|search <tags>|quality|volume|size|r34key|cache]  Floating video screen.",
                Run, Autocomplete);
            ModPanel.AddPage("Movie Block", "ico_camera", BuildPage, RefreshPage);
            ModKit.Tick += Tick;
            ModKit.MapLoaded += () => RateLimit.Clear();
            SelfTest.StartIfRequested();
        }

        /// <summary>The game ships a stripped VideoModule; make sure the full one from unstripped_corlib loaded.</summary>
        private bool CheckVideoModule() {
            try {
                var type = typeof(UnityEngine.Video.VideoPlayer);
                bool ok = type.GetProperty("url") != null && type.GetProperty("targetTexture") != null;
                if (!ok) {
                    Logger.LogError("UnityEngine.VideoModule is the game's stripped copy. Copy UnityEngine.VideoModule.dll from a Unity 2021.3.45f2 " +
                                    "install (Editor\\Data\\PlaybackEngines\\windowsstandalonesupport\\Variations\\win64_player_nondevelopment_mono\\Data\\Managed) " +
                                    "into BepInEx\\unstripped_corlib.");
                }
                return ok;
            } catch (Exception e) {
                Logger.LogError("Video module check failed: " + e.Message);
                return false;
            }
        }

        private const string VideoModuleHelp =
            "Movie block needs the full UnityEngine.VideoModule.dll (Unity 2021.3.45f2) in BepInEx\\unstripped_corlib. See the mod's README.";

        private static void Tick() {
            Net.Pump();
            RateLimit.Pump(Time.unscaledTime);
            if (!MovieScreen.Exists) {
                return;
            }
            var keyboard = Keyboard.current;
            var key = GetKey();
            if (keyboard != null && key != null && keyboard[key.Value].wasPressedThisFrame) {
                MovieScreen.ToggleFocus();
            }
        }

        private static Key? GetKey() {
            string name = FocusKey.Value;
            if (name != parsedKeyName) {
                parsedKeyName = name;
                parsedKey = Enum.TryParse((name ?? "").Trim(), true, out Key k) && k != Key.None ? k : (Key?)null;
                if (parsedKey == null) {
                    Log.LogWarning($"FocusKey '{name}' isn't a valid key name.");
                }
            }
            return parsedKey;
        }

        // ------------------------------------------------------------------ command

        private static void Run(CommandContext ctx) {
            string sub = (ctx.Arg(1) ?? "").ToLowerInvariant();
            string rest = string.Join(" ", ctx.Args.Skip(2).ToArray()).Trim();
            switch (sub) {
                case "e621":
                case "e6":
                    Open(ctx, new E621Source());
                    break;
                case "r34":
                case "rule34":
                    Open(ctx, new Rule34Source());
                    break;
                case "close":
                case "off":
                    MovieScreen.Close();
                    ctx.Reply("Movie block closed.");
                    break;
                case "here":
                    ctx.Reply(MovieScreen.BringHere() ? "Movie block moved in front of you." : "No movie block. Use /movieblock e621 or /movieblock r34.");
                    break;
                case "focus":
                    if (!MovieScreen.Exists) {
                        ctx.Fail("No movie block. Use /movieblock e621 or /movieblock r34.");
                    }
                    MovieScreen.ToggleFocus();
                    break;
                case "follow":
                    Follow.Value = rest.Length == 0 ? !Follow.Value : rest == "on" || rest == "1" || rest == "true";
                    ctx.Reply($"Follow {(Follow.Value ? "on" : "off")}.");
                    break;
                case "search":
                    if (!MovieScreen.SearchFor(rest)) {
                        ctx.Fail("No movie block. Use /movieblock e621 or /movieblock r34 first.");
                    }
                    break;
                case "quality":
                    if (rest != "480p" && rest != "720p" && rest != "original") {
                        ctx.Fail($"Quality is {Quality.Value}. Use /movieblock quality 480p|720p|original (e621 only).");
                    }
                    Quality.Value = rest;
                    ctx.Reply($"Quality {rest}. Applies to the next search.");
                    break;
                case "volume":
                    if (!float.TryParse(rest, out float vol)) {
                        ctx.Fail($"Volume is {Volume.Value * 100f:0}. Use /movieblock volume 0-100.");
                    }
                    Volume.Value = Mathf.Clamp01(vol / 100f);
                    ctx.Reply($"Volume {Volume.Value * 100f:0}.");
                    break;
                case "size":
                    if (!float.TryParse(rest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float size)) {
                        ctx.Fail($"Size is {Size.Value:0.##} m wide. Use /movieblock size <metres>.");
                    }
                    Size.Value = Mathf.Clamp(size, 0.5f, 10f);
                    ctx.Reply($"Screen width {Size.Value:0.##} m.");
                    break;
                case "distance":
                    if (!float.TryParse(rest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dist)) {
                        ctx.Fail($"Distance is {Distance.Value:0.##} m. Use /movieblock distance <metres>.");
                    }
                    Distance.Value = Mathf.Clamp(dist, 0.5f, 20f);
                    MovieScreen.BringHere();
                    ctx.Reply($"Distance {Distance.Value:0.##} m.");
                    break;
                case "r34key":
                    SetR34Key(ctx, rest);
                    break;
                case "e621key":
                    var parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) {
                        E621User.Value = "";
                        E621ApiKey.Value = "";
                        ctx.Reply("e621 login cleared.");
                        break;
                    }
                    if (parts.Length != 2) {
                        ctx.Fail("Usage: /movieblock e621key <username> <api key>   (no arguments clears it)");
                    }
                    E621User.Value = parts[0];
                    E621ApiKey.Value = parts[1];
                    ctx.Reply($"e621 login saved for {parts[0]}.");
                    break;
                case "cache":
                    if (rest == "clear") {
                        ctx.Reply($"Deleted {VideoCache.Clear()} cached file(s).");
                    } else {
                        ctx.Reply($"Video cache: {VideoCache.SizeBytes() / 1048576.0:0} MB of {MaxCacheMB.Value} MB in {VideoCache.Folder}. " +
                                  "/movieblock cache clear to empty it.");
                    }
                    break;
                case "status":
                    ctx.Reply(MovieScreen.Describe());
                    break;
                default:
                    ctx.Reply("<b>/movieblock</b> e621 | r34   open the screen in front of you (or switch site)");
                    ctx.Reply($"  {FocusKey.Value} (or /movieblock focus)   use the screen / back to the game");
                    ctx.Reply("  here | close | follow [on|off] | search <tags> | volume 0-100 | size <m> | distance <m>");
                    ctx.Reply("  quality 480p|720p|original | r34key <api key text> | e621key <user> <key> | cache [clear]");
                    if (!videoModuleOk) {
                        ctx.Reply("<color=#ff8a80>" + VideoModuleHelp + "</color>");
                    }
                    break;
            }
        }

        private static void Open(CommandContext ctx, Source source) {
            if (!videoModuleOk) {
                ctx.Fail(VideoModuleHelp);
            }
            if (!MovieScreen.Open(source, focus: true)) {
                ctx.Fail("Load into a map first.");
            }
            ctx.Reply($"Movie block: {source.Title}. {FocusKey.Value} switches between the screen and the game. Only you can see it.");
        }

        private static void SetR34Key(CommandContext ctx, string text) {
            if (text.Length == 0) {
                ctx.Fail("Paste the credentials from rule34.xxx > My Account > Options > API Access Credentials:\n" +
                         "/movieblock r34key &api_key=...&user_id=...     (or: /movieblock r34key <user id> <api key>)");
            }
            string key = Regex.Match(text, @"api_key=([A-Za-z0-9]+)").Groups[1].Value;
            string user = Regex.Match(text, @"user_id=(\d+)").Groups[1].Value;
            if (key.Length == 0 || user.Length == 0) {
                var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) {
                    bool firstIsId = parts[0].All(char.IsDigit);
                    user = firstIsId ? parts[0] : parts[1];
                    key = firstIsId ? parts[1] : parts[0];
                }
            }
            if (key.Length == 0 || user.Length == 0 || !user.All(char.IsDigit)) {
                ctx.Fail("Couldn't find api_key and user_id in that. Paste the whole &api_key=...&user_id=... text.");
            }
            R34ApiKey.Value = key;
            R34UserId.Value = user;
            ctx.Reply($"rule34 API key saved (user {user}).");
            if (MovieScreen.Exists) {
                MovieScreen.Open(new Rule34Source(), focus: false);
                MovieScreen.SearchFor(R34DefaultTags.Value);
            }
        }

        private static IEnumerable<string> Autocomplete(int index, string text) {
            if (index == 1) {
                return Subcommands.Where(s => s.StartsWith(text, StringComparison.OrdinalIgnoreCase));
            }
            return Enumerable.Empty<string>();
        }

        // ------------------------------------------------------------------ panel page

        private static StatusCard status;

        private static void BuildPage(PageBuilder page) {
            status = page.Card(130f);
            page.Header("Open", "ico_camera");
            page.Buttons(
                new ButtonSpec("e621", "ico_magnifying_looking_glass", () => OpenFromPanel(new E621Source())),
                new ButtonSpec("rule34", "ico_magnifying_looking_glass", () => OpenFromPanel(new Rule34Source())));
            page.Buttons(
                new ButtonSpec("Bring here", "ico_pointing_at", () => MovieScreen.BringHere()),
                new ButtonSpec("Close", "ico_trashcan", MovieScreen.Close, ButtonStyle.Cancel));

            page.Header("Screen", "ico_gear");
            page.Slider("Width", Size, 0.5f, 6f, 0.1f, v => $"{v:0.0} m");
            page.Slider("Distance", Distance, 0.5f, 8f, 0.25f, v => $"{v:0.##} m");
            page.Toggle("Turn towards me", FaceCamera);
            page.Toggle("Follow me", Follow);

            page.Header("Playback", "ico_speaker");
            page.Slider("Volume", Volume, 0f, 1f, 0.05f, v => $"{v * 100f:0}");
            page.Toggle("3D sound from the screen", SpatialAudio);
            page.Toggle("Loop", Loop);
            page.Toggle("Play next when a video ends (loop off)", AutoNext);
            page.Note($"Commands: /movieblock e621 | r34 | here | close | quality 480p|720p|original | r34key ...\n" +
                      "rule34.xxx needs a free API key: My Account > Options > API Access Credentials, then /movieblock r34key <paste>.");
        }

        private static void OpenFromPanel(Source source) {
            if (!videoModuleOk) {
                CheatsProcessor.AppendText("<color=#ff8a80>" + VideoModuleHelp + "</color>\n");
                return;
            }
            ModPanel.Close();
            MovieScreen.Open(source, focus: true);
        }

        private static void RefreshPage() {
            status.Title.text = MovieScreen.Exists
                ? $"{Theme.Cyan}●</color> Screen open{(MovieScreen.IsFocused ? " (in use)" : "")}"
                : "No screen";
            status.Body.text = !videoModuleOk
                ? $"{Theme.Red}{VideoModuleHelp}</color>"
                : $"{MovieScreen.Describe()}\nFocus key: {FocusKey.Value}.  Cache: {VideoCache.SizeBytes() / 1048576.0:0} MB.";
        }
    }
}
