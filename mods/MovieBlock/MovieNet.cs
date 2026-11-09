using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ExitGames.Client.Photon;
using KoboldKare.ModKit;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace MovieBlock {
    /// <summary>What a shared screen looks like right now. Sent by the owner, applied by viewers.</summary>
    internal class ScreenState {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Size;           // metres wide
        public bool HasVideo;
        public string Site = "";     // "e621" / "r34"
        public long PostId;
        public string Url = "";
        public string[] Tags = new string[0];
        public bool Playing;
        public bool Loop;
        public double Time;          // seconds into the video when Stamp was taken
        public int Stamp;            // PhotonNetwork.ServerTimestamp (ms, shared by the room)
    }

    /// <summary>
    /// Multiplayer for Movie Block, synced through the game's Photon room server.
    /// <list type="bullet">
    /// <item>Changes (new video, play/pause/seek, loop, size, a moved screen) go out as reliable Photon events, and the
    /// latest one per owner is kept in the room's event cache on the server, so players who join later get every
    /// shared screen straight away. Photon removes an owner's cached state when they leave.</item>
    /// <item>A small heartbeat every few seconds keeps everyone's time in step; it isn't cached.</item>
    /// </list>
    /// Every incoming event is untrusted: the sender is set by the Photon server (it can't be faked), events are
    /// size-capped, rate-limited per sender and type-checked, URLs must pass the same https e621/rule34 check as
    /// everything else, and nothing in an event is ever run.
    /// </summary>
    internal static class MovieNet {
        // Photon event codes in use: the game 65, 67, 72, 203; Voice Chat 177.
        public const byte EventCode = 178;
        private const byte Magic = 0x4D;
        private const byte Version = 1;
        private const byte TypeState = 1, TypeClosed = 2;
        private const int MaxMessageBytes = 16 * 1024;
        private const int MaxTags = 150, MaxTagLength = 100, MaxUrlLength = 400;
        private const float Heartbeat = 4f;
        private const float RemoteTimeout = 15f;
        private const float MinSendInterval = 0.25f;

        private static LoadBalancingClient hookedClient;

        // Viewer side: the newest state per owner (kept even while hidden, or before the map has loaded).
        private class Latest {
            public string Owner;
            public ScreenState State;
            public float Heard;
        }

        private static readonly Dictionary<int, Latest> LatestStates = new Dictionary<int, Latest>();
        private static readonly Dictionary<int, int> Budget = new Dictionary<int, int>();
        private static float nextBudgetRefill;
        private static readonly HashSet<int> Hinted = new HashSet<int>();

        // Owner side.
        private static ScreenState lastSent;
        private static float lastSentAt, nextHeartbeat;
        private static bool sharing, cacheStale;

        public static bool Available => InRoom;
        private static bool InRoom => PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

        // ================================================================== per frame

        public static void Tick() {
            Hook();
            if (!InRoom) {
                if (MovieScreen.Remotes.Count > 0) {
                    MovieScreen.RemoveAllRemotes();
                }
                LatestStates.Clear();
                Hinted.Clear();
                sharing = false;
                lastSent = null;
                return;
            }
            try {
                ApplyLatest();
                if (ModKit.InLevel) {
                    SendOwnState();
                }
            } catch (Exception e) {
                MovieBlockPlugin.Log.LogError("Movie Block networking failed: " + e);
            }
        }

        /// <summary>Listen to Photon events (again, if the game made a new networking client).</summary>
        private static void Hook() {
            var client = PhotonNetwork.NetworkingClient;
            if (client == null || client == hookedClient) {
                return;
            }
            if (hookedClient != null) {
                hookedClient.EventReceived -= OnEvent;
            }
            client.EventReceived += OnEvent;
            hookedClient = client;
        }

        // ================================================================== receiving

        private static void OnEvent(EventData e) {
            if (e.Code != EventCode) {
                return;
            }
            try {
                int actor = e.Sender; // filled in by the Photon server
                var room = PhotonNetwork.CurrentRoom;
                var player = room?.GetPlayer(actor);
                if (player == null || player.IsLocal || !(e.CustomData is byte[] data) || data.Length < 3 || data.Length > MaxMessageBytes ||
                    !Spend(actor)) {
                    return;
                }
                Handle(actor, player, data);
            } catch (Exception ex) {
                MovieBlockPlugin.Log.LogWarning("Dropped a bad Movie Block event: " + ex.Message);
            }
        }

        private static bool Spend(int actor) {
            if (Time.unscaledTime >= nextBudgetRefill) {
                nextBudgetRefill = Time.unscaledTime + 2f;
                Budget.Clear(); // at most 30 events per sender every 2 s (normal is well under 1 per second)
            }
            Budget.TryGetValue(actor, out int used);
            if (used >= 30) {
                return false;
            }
            Budget[actor] = used + 1;
            return true;
        }

        private static void Handle(int actor, Player player, byte[] data) {
            using (var reader = new BinaryReader(new MemoryStream(data, false), Encoding.UTF8)) {
                if (reader.ReadByte() != Magic || reader.ReadByte() != Version) {
                    return;
                }
                byte type = reader.ReadByte();
                if (type == TypeClosed) {
                    LatestStates.Remove(actor);
                    MovieScreen.RemoveRemote(actor);
                    return;
                }
                if (type != TypeState) {
                    return;
                }
                var state = ReadState(reader);
                if (state == null) {
                    return;
                }
                LatestStates[actor] = new Latest { Owner = Clean(player.NickName), State = state, Heard = Time.unscaledTime };
                if (ModKit.InLevel && MovieBlockPlugin.ShowOthers.Value) {
                    Show(actor, LatestStates[actor]);
                }
            }
        }

        /// <summary>
        /// Every frame: show shared screens we have state for (after a map load, or once "show others" is turned on),
        /// and drop ones whose owner left, went quiet or stopped sharing.
        /// </summary>
        private static void ApplyLatest() {
            var room = PhotonNetwork.CurrentRoom;
            var gone = new List<int>();
            foreach (var pair in LatestStates) {
                if (room?.GetPlayer(pair.Key) == null || Time.unscaledTime - pair.Value.Heard > RemoteTimeout) {
                    gone.Add(pair.Key);
                }
            }
            foreach (int actor in gone) {
                LatestStates.Remove(actor);
                MovieScreen.RemoveRemote(actor);
            }
            foreach (var actor in new List<int>(MovieScreen.Remotes.Keys)) {
                if (!LatestStates.ContainsKey(actor) || !MovieBlockPlugin.ShowOthers.Value || !ModKit.InLevel) {
                    MovieScreen.RemoveRemote(actor);
                }
            }
            if (!ModKit.InLevel) {
                return;
            }
            foreach (var pair in LatestStates) {
                if (!MovieBlockPlugin.ShowOthers.Value) {
                    if (Hinted.Add(pair.Key)) {
                        CheatsProcessor.AppendText($"<#7dfff0>Movie Block:</color> {pair.Value.Owner} is sharing a screen. " +
                                                   "Type /movieblock others on to see other players' screens.\n");
                    }
                } else if (!MovieScreen.Remotes.TryGetValue(pair.Key, out var screen) || screen == null) {
                    Show(pair.Key, pair.Value);
                }
            }
        }

        private static void Show(int actor, Latest latest) {
            var state = latest.State;
            // Ignore a pose far from the owner's kobold (keeps screens where they belong).
            if (PhotonNetwork.CurrentRoom?.GetPlayer(actor)?.TagObject is Kobold kobold && kobold != null &&
                (state.Position - kobold.transform.position).sqrMagnitude > 40f * 40f) {
                state.Position = kobold.transform.position + Vector3.up * 1.5f;
            }
            MovieScreen.ApplyRemote(actor, latest.Owner, state);
        }

        private static ScreenState ReadState(BinaryReader reader) {
            var s = new ScreenState {
                Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                Size = reader.ReadSingle(),
            };
            byte flags = reader.ReadByte();
            s.HasVideo = (flags & 1) != 0;
            s.Playing = (flags & 2) != 0;
            s.Loop = (flags & 4) != 0;
            s.Site = ReadString(reader, 8);
            s.PostId = reader.ReadInt64();
            s.Url = ReadString(reader, MaxUrlLength);
            int tagCount = reader.ReadUInt16();
            if (tagCount > MaxTags) {
                return null;
            }
            s.Tags = new string[tagCount];
            for (int i = 0; i < tagCount; i++) {
                s.Tags[i] = ReadString(reader, MaxTagLength).ToLowerInvariant();
            }
            s.Time = reader.ReadDouble();
            s.Stamp = reader.ReadInt32();

            // Type and range checks: anything odd and the whole event is dropped.
            if (!Finite(s.Position) || !Finite(s.Rotation.x) || !Finite(s.Rotation.y) || !Finite(s.Rotation.z) || !Finite(s.Rotation.w) ||
                !Finite(s.Size) || double.IsNaN(s.Time) || double.IsInfinity(s.Time) || s.Time < 0 || s.Time > 24 * 3600) {
                return null;
            }
            float norm = Mathf.Sqrt(s.Rotation.x * s.Rotation.x + s.Rotation.y * s.Rotation.y + s.Rotation.z * s.Rotation.z + s.Rotation.w * s.Rotation.w);
            if (norm < 0.5f || norm > 1.5f) {
                return null;
            }
            s.Rotation = new Quaternion(s.Rotation.x / norm, s.Rotation.y / norm, s.Rotation.z / norm, s.Rotation.w / norm);
            s.Size = Mathf.Clamp(s.Size, 0.5f, 15f);
            if (s.HasVideo) {
                string domain = s.Site == "e621" ? "e621.net" : s.Site == "r34" ? "rule34.xxx" : null;
                string ext = Path.GetExtension(s.Url.Split('?')[0]).ToLowerInvariant();
                if (domain == null || s.PostId <= 0 || !Source.IsTrusted(s.Url, domain) || (ext != ".mp4" && ext != ".webm")) {
                    return null;
                }
            }
            return s;
        }

        private static string ReadString(BinaryReader reader, int maxChars) {
            int bytes = reader.ReadUInt16();
            if (bytes > maxChars * 4) {
                throw new System.IO.InvalidDataException("string too long");
            }
            string text = Encoding.UTF8.GetString(reader.ReadBytes(bytes));
            if (text.Length > maxChars) {
                throw new System.IO.InvalidDataException("string too long");
            }
            return text;
        }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z) && Mathf.Abs(v.x) < 1e5f && Mathf.Abs(v.y) < 1e5f && Mathf.Abs(v.z) < 1e5f;

        /// <summary>Player names go into chat and onto screens: strip anything that could act as a rich-text tag.</summary>
        private static string Clean(string name) {
            name = (name ?? "").Replace("<", "").Replace(">", "");
            return name.Length > 32 ? name.Substring(0, 32) : name;
        }

        // ================================================================== sending (owner)

        private static void SendOwnState() {
            var screen = MovieScreen.Instance;
            bool share = MovieBlockPlugin.Share.Value && screen != null;
            if (!share) {
                if (sharing) {
                    SendClosed();
                }
                return;
            }
            var state = screen.CaptureState();
            state.Stamp = PhotonNetwork.ServerTimestamp;
            float now = Time.unscaledTime;
            if (now - lastSentAt < MinSendInterval) {
                return;
            }
            bool content = !sharing || ContentChanged(state, lastSent);
            bool moved = lastSent != null && Moved(state, lastSent);
            bool beat = now >= nextHeartbeat;
            if (!content && !moved && !beat) {
                return;
            }
            if (moved) {
                cacheStale = true;
            }
            if (content || (beat && cacheStale)) {
                // The server keeps this one for players who join later. Moves alone only refresh it on a heartbeat.
                SendCached(state);
            } else {
                Send(state, EventCaching.DoNotCache);
            }
            sharing = true;
            lastSent = state;
            lastSentAt = now;
            if (beat || content) {
                nextHeartbeat = now + Heartbeat;
            }
        }

        /// <summary>Video, play/pause, seek, loop or size changed: viewers (and the cache) need it now.</summary>
        private static bool ContentChanged(ScreenState now, ScreenState before) {
            if (before == null || now.HasVideo != before.HasVideo || now.PostId != before.PostId || now.Url != before.Url ||
                now.Playing != before.Playing || now.Loop != before.Loop || Mathf.Abs(now.Size - before.Size) > 0.01f) {
                return true;
            }
            if (now.HasVideo) {
                // A seek: the time isn't where the last event predicted.
                double predicted = before.Time + (before.Playing ? unchecked(now.Stamp - before.Stamp) / 1000.0 : 0);
                if (Math.Abs(now.Time - predicted) > 1.0 && !now.Loop) {
                    return true;
                }
            }
            return false;
        }

        private static bool Moved(ScreenState now, ScreenState before) =>
            (now.Position - before.Position).sqrMagnitude > 0.05f * 0.05f || Quaternion.Angle(now.Rotation, before.Rotation) > 2f;

        private static void SendCached(ScreenState state) {
            // Replace our cached state: remove the old one (by event code and sender), then add the new one.
            PhotonNetwork.RaiseEvent(EventCode, null, new RaiseEventOptions { CachingOption = EventCaching.RemoveFromRoomCache },
                SendOptions.SendReliable);
            Send(state, EventCaching.AddToRoomCache);
            cacheStale = false;
        }

        private static void Send(ScreenState state, EventCaching caching) {
            byte[] data = Write(state);
            if (data.Length > MaxMessageBytes) {
                return;
            }
            PhotonNetwork.RaiseEvent(EventCode, data, new RaiseEventOptions { Receivers = ReceiverGroup.Others, CachingOption = caching },
                SendOptions.SendReliable);
        }

        private static byte[] Write(ScreenState s) {
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory, Encoding.UTF8)) {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(TypeState);
                writer.Write(s.Position.x);
                writer.Write(s.Position.y);
                writer.Write(s.Position.z);
                writer.Write(s.Rotation.x);
                writer.Write(s.Rotation.y);
                writer.Write(s.Rotation.z);
                writer.Write(s.Rotation.w);
                writer.Write(s.Size);
                writer.Write((byte)((s.HasVideo ? 1 : 0) | (s.Playing ? 2 : 0) | (s.Loop ? 4 : 0)));
                WriteString(writer, s.Site, 8);
                writer.Write(s.PostId);
                WriteString(writer, s.Url, MaxUrlLength);
                int tags = Math.Min(MaxTags, s.Tags.Length);
                writer.Write((ushort)tags);
                for (int i = 0; i < tags; i++) {
                    WriteString(writer, s.Tags[i], MaxTagLength);
                }
                writer.Write(s.Time);
                writer.Write(s.Stamp);
                writer.Flush();
                return memory.ToArray();
            }
        }

        private static void WriteString(BinaryWriter writer, string text, int maxChars) {
            text = text ?? "";
            if (text.Length > maxChars) {
                text = text.Substring(0, maxChars);
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        /// <summary>Tell viewers our screen is gone (closed, sharing off) and clear it from the server's cache.</summary>
        public static void SendClosed() {
            if (sharing && InRoom) {
                try {
                    PhotonNetwork.RaiseEvent(EventCode, null, new RaiseEventOptions { CachingOption = EventCaching.RemoveFromRoomCache },
                        SendOptions.SendReliable);
                    PhotonNetwork.RaiseEvent(EventCode, new[] { Magic, Version, TypeClosed },
                        new RaiseEventOptions { Receivers = ReceiverGroup.Others, CachingOption = EventCaching.DoNotCache }, SendOptions.SendReliable);
                } catch {
                    // Leaving anyway.
                }
            }
            sharing = false;
            lastSent = null;
        }
    }
}
