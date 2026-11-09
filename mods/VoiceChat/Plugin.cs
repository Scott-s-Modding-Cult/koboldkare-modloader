using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KoboldKare.ModKit;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace VoiceChat {
    public enum VoiceMode {
        PushToTalk,
        Toggle,
        OpenMic,
    }

    /// <summary>
    /// Proximity voice chat. The mic is recorded and compressed by Steam (<see cref="VoiceCapture"/>),
    /// sent over the game's Photon room (<see cref="VoiceNet"/>) and played from each player's kobold
    /// (<see cref="VoicePlayback"/>). Everyone who wants to talk or listen needs the mod.
    /// </summary>
    [BepInPlugin(Guid, "Voice Chat", "1.0.0")]
    [BepInDependency(ModKit.Guid)]
    public class VoiceChatPlugin : BaseUnityPlugin {
        public const string Guid = "koboldkare.voicechat";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<VoiceMode> Mode;
        internal static ConfigEntry<string> TalkKey;
        internal static ConfigEntry<float> Volume;
        internal static ConfigEntry<bool> Proximity;
        internal static ConfigEntry<float> HearingDistance;
        internal static ConfigEntry<string> MutedPlayers;
        internal static ConfigEntry<bool> MuffleBehindWalls;
        internal static ConfigEntry<float> MuffleStrength;
        internal static ConfigEntry<bool> ShowHud;
        internal static ConfigEntry<bool> HearMyself;

        private static readonly HashSet<string> Logged = new HashSet<string>();
        private static HashSet<string> mutedSet;
        private static string mutedRaw;
        private static string parsedKeyName;
        private static Key? parsedKey;
        private static bool toggledOn;
        private static bool transmitting;

        private void Awake() {
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Turn voice chat on or off (both talking and listening).");
            Mode = Config.Bind("General", "Mode", VoiceMode.PushToTalk, "PushToTalk: hold the key. Toggle: press to start/stop. OpenMic: always on (Steam only sends while you speak).");
            TalkKey = Config.Bind("General", "TalkKey", "V", "Push-to-talk / toggle key. Any UnityEngine.InputSystem.Key name (V, B, CapsLock, LeftAlt, ...).");
            Volume = Config.Bind("Playback", "Volume", 1f, "Volume of other players' voices (0 to 2).");
            Proximity = Config.Bind("Playback", "Proximity", true, "Voices come from each kobold and fade with distance. Off: everyone hears everyone at full volume.");
            HearingDistance = Config.Bind("Playback", "HearingDistance", 30f, "Distance in metres at which a voice fades to silence (proximity only).");
            MuffleBehindWalls = Config.Bind("Playback", "MuffleBehindWalls", true, "Voices behind walls sound muffled and quieter (proximity only).");
            MuffleStrength = Config.Bind("Playback", "MuffleStrength", 1f, "How strongly walls muffle voices (0 to 1).");
            MutedPlayers = Config.Bind("Playback", "MutedPlayers", "", "Comma-separated player names you don't want to hear.");
            ShowHud = Config.Bind("Interface", "ShowHud", true, "Show who's talking in the bottom-left corner.");
            HearMyself = Config.Bind("Testing", "HearMyself", false, "Play your own voice back to you, to check your mic. Turns itself off when the game starts.");
            HearMyself.Value = false;

            SelfCheck.Run(typeof(VoiceChatPlugin).Assembly, Logger);

            ModCommands.Register("/voice",
                "[on|off|mode ptt|toggle|open|key <key>|test|block [off]|mute <name>|unmute <name>|list]  Voice chat.",
                RunCommand, Autocomplete);
            ModPanel.AddPage("Voice Chat", "ico_speaker", BuildPage, RefreshPage);
            ModKit.Tick += Tick;
            ModKit.MapLoaded += Occlusion.Reset;
            Logger.LogInfo("Voice Chat ready.");
        }

        internal static void LogOnce(string key, string message) {
            if (Logged.Add(key)) {
                Log.LogWarning(message + " (further messages like this are suppressed)");
            }
        }

        // ------------------------------------------------------------------ per frame

        private static void Tick() {
            VoiceNet.Update();

            transmitting = WantTransmit();
            bool someoneListening = VoiceNet.ListenerCount() > 0;
            bool selfTest = HearMyself.Value || TestBlock.Exists;
            VoiceCapture.Update(transmitting && (someoneListening || selfTest), selfTest);

            VoicePlayback.Update();
            TestBlock.Update(VoicePlayback.TakeBlockPeak());
            Hud.Update();
        }

        private static bool WantTransmit() {
            if (!Enabled.Value || !ModKit.InLevel) {
                toggledOn = false;
                return false;
            }
            var keyboard = Keyboard.current;
            var key = GetKey();
            bool typing = IsTyping();
            var control = keyboard != null && key != null ? keyboard[key.Value] : null;
            switch (Mode.Value) {
                case VoiceMode.OpenMic:
                    return true;
                case VoiceMode.Toggle:
                    if (!typing && control != null && control.wasPressedThisFrame) {
                        toggledOn = !toggledOn;
                    }
                    return toggledOn;
                default:
                    return !typing && control != null && control.isPressed;
            }
        }

        /// <summary>Don't treat the talk key as pressed while typing in chat.</summary>
        private static bool IsTyping() {
            var system = EventSystem.current;
            var selected = system != null ? system.currentSelectedGameObject : null;
            if (selected == null) {
                return false;
            }
            var tmp = selected.GetComponent<TMP_InputField>();
            if (tmp != null) {
                return tmp.isFocused;
            }
            var legacy = selected.GetComponent<InputField>();
            return legacy != null && legacy.isFocused;
        }

        private static Key? GetKey() {
            string name = TalkKey.Value;
            if (name != parsedKeyName) {
                parsedKeyName = name;
                parsedKey = Enum.TryParse((name ?? "").Trim(), true, out Key k) && k != Key.None ? k : (Key?)null;
                if (parsedKey == null) {
                    Log.LogWarning($"Talk key '{name}' isn't a valid key name.");
                }
            }
            return parsedKey;
        }

        // ------------------------------------------------------------------ muting

        internal static bool IsMuted(string nickName) {
            if (MutedPlayers.Value != mutedRaw) {
                mutedRaw = MutedPlayers.Value;
                mutedSet = new HashSet<string>(
                    (mutedRaw ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
            }
            return nickName != null && mutedSet.Contains(nickName.Trim());
        }

        private static void SetMuted(string nickName, bool muted) {
            IsMuted(nickName); // refresh the set
            var set = new HashSet<string>(mutedSet, StringComparer.OrdinalIgnoreCase);
            if (muted) {
                set.Add(nickName.Trim());
            } else {
                set.Remove(nickName.Trim());
            }
            MutedPlayers.Value = string.Join(",", set);
        }

        private static Player FindPlayer(string text) {
            if (string.IsNullOrWhiteSpace(text) || !PhotonNetwork.InRoom) {
                return null;
            }
            text = text.Trim();
            var others = PhotonNetwork.PlayerListOthers;
            if (int.TryParse(text, out int actor)) {
                var byNumber = others.FirstOrDefault(p => p.ActorNumber == actor);
                if (byNumber != null) {
                    return byNumber;
                }
            }
            return others.FirstOrDefault(p => string.Equals(p.NickName, text, StringComparison.OrdinalIgnoreCase))
                   ?? others.FirstOrDefault(p => (p.NickName ?? "").StartsWith(text, StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------ /voice

        private static void RunCommand(CommandContext ctx) {
            string rest = ctx.Args.Length > 2 ? string.Join(" ", ctx.Args.Skip(2)) : null;
            switch ((ctx.Arg(1) ?? "").ToLowerInvariant()) {
                case "":
                    ctx.Reply(StatusText(true));
                    break;
                case "on":
                    Enabled.Value = true;
                    ctx.Reply("Voice chat on.");
                    break;
                case "off":
                    Enabled.Value = false;
                    ctx.Reply("Voice chat off.");
                    break;
                case "mode":
                    switch ((ctx.Arg(2) ?? "").ToLowerInvariant()) {
                        case "ptt":
                        case "push":
                        case "pushtotalk":
                            Mode.Value = VoiceMode.PushToTalk;
                            break;
                        case "toggle":
                            Mode.Value = VoiceMode.Toggle;
                            break;
                        case "open":
                        case "openmic":
                            Mode.Value = VoiceMode.OpenMic;
                            break;
                        default:
                            ctx.Fail("Usage: /voice mode ptt|toggle|open");
                            break;
                    }
                    ModPanel.SyncFromConfig();
                    ctx.Reply("Mode: " + ModeLabel());
                    break;
                case "key":
                    if (Enum.TryParse(ctx.Arg(2) ?? "", true, out Key key) && key != Key.None) {
                        TalkKey.Value = key.ToString();
                        ctx.Reply("Talk key: " + TalkKey.Value);
                    } else {
                        ctx.Fail("Usage: /voice key <key>, e.g. /voice key B");
                    }
                    break;
                case "test":
                    HearMyself.Value = !HearMyself.Value;
                    ModPanel.SyncFromConfig();
                    ctx.Reply(HearMyself.Value
                        ? $"Mic test on: you'll hear yourself while you talk ({ModeLabel()}). /voice test again to stop."
                        : "Mic test off.");
                    break;
                case "mute":
                case "unmute": {
                    bool mute = ctx.Arg(1).Equals("mute", StringComparison.OrdinalIgnoreCase);
                    var player = FindPlayer(rest);
                    string name = player?.NickName ?? rest;
                    if (string.IsNullOrWhiteSpace(name)) {
                        ctx.Fail($"Usage: /voice {ctx.Arg(1).ToLowerInvariant()} <name>");
                    }
                    SetMuted(name, mute);
                    ctx.Reply((mute ? "Muted " : "Unmuted ") + name + ".");
                    break;
                }
                case "block":
                    if (string.Equals(ctx.Arg(2), "off", StringComparison.OrdinalIgnoreCase)) {
                        TestBlock.Remove();
                        ctx.Reply("Test block removed.");
                    } else if (TestBlock.Place()) {
                        ctx.Reply($"Test block placed in front of you: it plays your mic back in 3D ({ModeLabel()}). " +
                                  "Walk around it to hear the direction and falloff. /voice block off to remove it.");
                    } else {
                        ctx.Fail("Load a map first.");
                    }
                    break;
                case "list":
                    ctx.Reply(PlayerList(true));
                    break;
                default:
                    ctx.Fail("Usage: /voice [on|off|mode ptt|toggle|open|key <key>|test|block [off]|mute <name>|unmute <name>|list]");
                    break;
            }
        }

        private static IEnumerable<string> Autocomplete(int index, string text) {
            text = text ?? "";
            if (index == 1) {
                return new[] { "on", "off", "mode", "key", "test", "block", "mute", "unmute", "list" }
                    .Where(o => o.StartsWith(text, StringComparison.OrdinalIgnoreCase));
            }
            if (index == 2 && PhotonNetwork.InRoom) {
                return PhotonNetwork.PlayerListOthers.Select(p => p.NickName)
                    .Concat(new[] { "ptt", "toggle", "open" })
                    .Where(n => !string.IsNullOrEmpty(n) && n.StartsWith(text, StringComparison.OrdinalIgnoreCase));
            }
            if (index == 2) {
                return new[] { "ptt", "toggle", "open" }.Where(o => o.StartsWith(text, StringComparison.OrdinalIgnoreCase));
            }
            return Enumerable.Empty<string>();
        }

        // ------------------------------------------------------------------ text shared by command, panel and HUD

        private static string ModeLabel() {
            switch (Mode.Value) {
                case VoiceMode.Toggle:
                    return $"toggle with {TalkKey.Value}";
                case VoiceMode.OpenMic:
                    return "open mic";
                default:
                    return $"hold {TalkKey.Value} to talk";
            }
        }

        private static string StatusText(bool forChat) {
            var sb = new StringBuilder();
            if (!VoiceCapture.SteamReady) {
                sb.Append("Steam isn't running (or the game wasn't started through Steam), so voice chat can't work.");
                return sb.ToString();
            }
            if (!Enabled.Value) {
                return "Voice chat is off. /voice on to turn it on.";
            }
            sb.Append("Voice chat on, ").Append(ModeLabel()).Append('.');
            if (!VoiceNet.InRoom) {
                sb.Append(forChat ? " Single player: nobody can hear you (try /voice test)." : "\nSingle player: nobody to talk to.");
            } else {
                int listeners = VoiceNet.ListenerCount();
                int others = PhotonNetwork.PlayerListOthers.Length;
                sb.Append($"\n{listeners} of {others} other player{(others == 1 ? "" : "s")} have voice chat.");
            }
            if (HearMyself.Value) {
                sb.Append("\nMic test on: you hear yourself.");
            }
            if (TestBlock.Exists) {
                sb.Append("\nTest block placed: your mic plays from it in 3D")
                  .Append(VoicePlayback.BlockMuffled ? " (muffled by walls)." : ".");
            }
            return sb.ToString();
        }

        private static string PlayerList(bool forChat) {
            if (!VoiceNet.InRoom) {
                return "Not in a multiplayer room.";
            }
            var sb = new StringBuilder();
            foreach (var p in PhotonNetwork.PlayerListOthers) {
                var speaker = VoicePlayback.All.FirstOrDefault(s => s.Actor == p.ActorNumber);
                string state = !VoiceNet.HasMod(p) ? "no mod"
                    : IsMuted(p.NickName) ? "muted"
                    : speaker != null && speaker.Talking ? $"{Theme.Cyan}talking</color>" + (speaker.Muffled ? " (behind a wall)" : "")
                    : "has voice";
                sb.Append($"{p.ActorNumber} {p.NickName}: {state}\n");
            }
            if (sb.Length == 0) {
                return "Nobody else is here.";
            }
            return sb.ToString().TrimEnd();
        }

        // ------------------------------------------------------------------ F8 panel

        private static StatusCard card;

        private static void BuildPage(PageBuilder page) {
            card = page.Card(220f);

            page.Header("Talking", "ico_speaker");
            page.Buttons(
                new ButtonSpec("Push to talk", "ico_speaker", () => SetMode(page, VoiceMode.PushToTalk)),
                new ButtonSpec("Toggle", "ico_refresh", () => SetMode(page, VoiceMode.Toggle)),
                new ButtonSpec("Open mic", "ico_lightning", () => SetMode(page, VoiceMode.OpenMic)));
            page.Toggle("Hear myself (mic test)", HearMyself);
            page.Buttons(
                new ButtonSpec("Place test block", "ico_speaker", () => TestBlock.Place()),
                new ButtonSpec("Remove block", "ico_trashcan", TestBlock.Remove, ButtonStyle.Cancel));
            page.Note("The test block plays your mic back from where it stands (in 3D), so you can walk around it and hear the falloff. " +
                      "Change the key with /voice key <key> (or TalkKey in BepInEx/config/koboldkare.voicechat.cfg). " +
                      "Steam records the mic: pick the device and check its level in Steam > Settings > Voice.");

            page.Header("Listening", "ico_gear");
            page.Toggle("Voice chat on", Enabled);
            page.Slider("Volume", Volume, 0f, 2f, 0.05f, v => $"{v * 100:0}%");
            page.Toggle("Proximity (voices come from kobolds)", Proximity);
            page.Slider("Hearing distance", HearingDistance, 5f, 100f, 1f, v => $"{v:0} m");
            page.Toggle("Muffle voices behind walls", MuffleBehindWalls);
            page.Slider("Wall muffling", MuffleStrength, 0f, 1f, 0.05f, v => $"{v * 100:0}%");
            page.Toggle("Show who's talking on screen", ShowHud);
            page.Note("Mute someone with /voice mute <name>; /voice list shows who has the mod.");
        }

        private static void SetMode(PageBuilder page, VoiceMode mode) {
            Mode.Value = mode;
            toggledOn = false;
            page.SyncFromConfig();
        }

        private static void RefreshPage() {
            if (card == null) {
                return;
            }
            string dot = !VoiceCapture.SteamReady || !Enabled.Value ? Theme.Red + "●</color>"
                : transmitting ? Theme.Cyan + "●</color>"
                : "○";
            card.Title.text = $"{dot} Voice chat";
            string body = StatusText(false);
            if (VoiceNet.InRoom) {
                body += "\n\n" + PlayerList(false);
            }
            card.Body.text = body;
        }

        // ------------------------------------------------------------------ HUD

        private static class Hud {
            private static GameObject root;
            private static TextMeshProUGUI text;
            private static string last;

            public static void Update() {
                if (!ShowHud.Value || !ModKit.InLevel) {
                    if (root != null) {
                        root.SetActive(false);
                    }
                    return;
                }
                if (root == null) {
                    Create();
                }
                if (!root.activeSelf) {
                    root.SetActive(true);
                }

                var sb = new StringBuilder();
                if (transmitting && (VoiceCapture.Recording || VoiceCapture.Speaking)) {
                    sb.Append(Theme.Cyan).Append("● You</color>");
                    if (!HearMyself.Value && !TestBlock.Exists && VoiceNet.ListenerCount() == 0) {
                        sb.Append(" <alpha=#99>(nobody with voice chat to hear you)");
                    }
                    sb.Append('\n');
                }
                if (PhotonNetwork.InRoom) {
                    foreach (var speaker in VoicePlayback.All) {
                        if (!speaker.IsPlayer || !speaker.Talking) {
                            continue;
                        }
                        var player = PhotonNetwork.CurrentRoom.GetPlayer(speaker.Actor);
                        if (player == null) {
                            continue;
                        }
                        sb.Append(IsMuted(player.NickName) ? "<alpha=#77>● " + player.NickName + " (muted)<alpha=#FF>" : "● " + player.NickName).Append('\n');
                    }
                }
                string now = sb.ToString();
                if (now != last) {
                    last = now;
                    text.text = now;
                }
            }

            private static void Create() {
                root = new GameObject("VoiceChat_HUD");
                UnityEngine.Object.DontDestroyOnLoad(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 400;
                var scaler = root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 1f;

                var rect = UiFactory.NewRect("Text", root.transform);
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(0f, 0f);
                rect.pivot = new Vector2(0f, 0f);
                rect.anchoredPosition = new Vector2(32f, 220f);
                rect.sizeDelta = new Vector2(700f, 300f);
                text = rect.gameObject.AddComponent<TextMeshProUGUI>();
                if (Theme.Font != null) {
                    text.font = Theme.Font;
                }
                text.fontSize = 28f;
                text.color = Color.white;
                text.alignment = TextAlignmentOptions.BottomLeft;
                text.richText = true;
                text.enableWordWrapping = false;
                text.raycastTarget = false;
                text.outlineWidth = 0.2f;
                text.outlineColor = new Color32(0, 0, 0, 200);
                last = null;
            }
        }
    }
}
