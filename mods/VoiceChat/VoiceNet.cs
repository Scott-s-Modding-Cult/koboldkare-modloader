using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace VoiceChat {
    /// <summary>
    /// Moves compressed Steam voice packets between players with Photon events, on the room
    /// connection the game already has. The game's own events use codes 65, 67 and 72; players
    /// without this mod ignore the unknown code. Packets only go to players who advertise the mod
    /// through a player property, so un-modded players and the room's message budget aren't spent on them.
    /// </summary>
    internal static class VoiceNet {
        public const byte EventCode = 177;
        private const byte Magic = 0x4B; // 'K'
        // 2: a payload is a list of [ushort length][Steam voice packet] frames (1 glued packets together, which Steam rejects).
        private const byte ProtocolVersion = 2;
        public const string HasModProperty = "kk.voice";

        private static readonly SendOptions Unreliable = SendOptions.SendUnreliable;
        private static readonly List<int> Targets = new List<int>();
        private static LoadBalancingClient hookedClient;
        private static float nextAdvertise;

        /// <summary>True when we're in a networked room (not single player).</summary>
        public static bool InRoom => PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

        /// <summary>Called every frame: keeps the event hook and our "has the mod" property in place.</summary>
        public static void Update() {
            var client = PhotonNetwork.NetworkingClient;
            if (client != null && client != hookedClient) {
                if (hookedClient != null) {
                    hookedClient.EventReceived -= OnEvent;
                }
                client.EventReceived += OnEvent;
                hookedClient = client;
            }

            if (InRoom && Time.unscaledTime >= nextAdvertise) {
                nextAdvertise = Time.unscaledTime + 3f;
                var me = PhotonNetwork.LocalPlayer;
                if (me != null && !me.CustomProperties.ContainsKey(HasModProperty)) {
                    me.SetCustomProperties(new Hashtable { { HasModProperty, (byte)ProtocolVersion } });
                }
            }
        }

        public static bool HasMod(Player player) =>
            player != null && (player.IsLocal || player.CustomProperties.ContainsKey(HasModProperty));

        /// <summary>Number of other players in the room who can hear us.</summary>
        public static int ListenerCount() {
            if (!InRoom) {
                return 0;
            }
            int n = 0;
            foreach (var p in PhotonNetwork.PlayerListOthers) {
                if (HasMod(p)) {
                    n++;
                }
            }
            return n;
        }

        public static void Send(byte[] buffer, int length) {
            if (!InRoom || length <= 0) {
                return;
            }
            Targets.Clear();
            foreach (var p in PhotonNetwork.PlayerListOthers) {
                if (HasMod(p)) {
                    Targets.Add(p.ActorNumber);
                }
            }
            if (Targets.Count == 0) {
                return;
            }
            var payload = new byte[length + 2];
            payload[0] = Magic;
            payload[1] = ProtocolVersion;
            Buffer.BlockCopy(buffer, 0, payload, 2, length);
            PhotonNetwork.RaiseEvent(EventCode, payload, new RaiseEventOptions { TargetActors = Targets.ToArray() }, Unreliable);
        }

        private static void OnEvent(EventData e) {
            if (e.Code != EventCode) {
                return;
            }
            try {
                if (e.CustomData is byte[] data && data.Length > 2 && data[0] == Magic && data[1] == ProtocolVersion) {
                    VoicePlayback.Receive(e.Sender, data, 2, data.Length - 2);
                }
            } catch (Exception ex) {
                VoiceChatPlugin.LogOnce("receive", "Failed to play a voice packet: " + ex);
            }
        }
    }
}
