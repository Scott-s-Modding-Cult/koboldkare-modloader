using System;
using System.Collections.Generic;
using System.Diagnostics;
using Photon.Pun;
using Photon.Realtime;
using Steamworks;
using UnityEngine;

namespace VoiceChat {
    /// <summary>
    /// Plays other players' voices from their kobold's head.
    ///
    /// Each speaker is an AudioSource looping a constant "carrier" clip, with a filter
    /// (OnAudioFilterRead) that multiplies the carrier by the decoded voice. Unity then applies its
    /// normal 3D distance falloff and panning, and the filter runs at the audio device's buffer size,
    /// so latency stays low (a streamed PCMReaderCallback clip would add ~0.4 s).
    ///
    /// The game strips AudioSettings.outputSampleRate, so <see cref="RateProbe"/> measures the
    /// device's rate from the filter callbacks and the speakers resample 48 kHz voice to it.
    /// </summary>
    internal static class VoicePlayback {
        public const int DecodeRate = 48000;
        private const int LoopbackActor = -1;
        private const int BlockActor = -2;

        private static readonly Dictionary<int, Speaker> Speakers = new Dictionary<int, Speaker>();
        private static readonly List<int> Remove = new List<int>();
        private static byte[] compressed = new byte[8 * 1024];
        private static byte[] decoded = new byte[DecodeRate * 2]; // 1 s of 16-bit mono
        private static AudioClip carrier;
        private static GameObject root;

        /// <summary>The audio device's sample rate, measured by <see cref="RateProbe"/>.</summary>
        public static volatile int OutputRate = 48000;
        private static bool rateLogged;
        private static volatile string pendingLog; // written on the audio thread, logged on the main thread

        public static IEnumerable<Speaker> All => Speakers.Values;

        // ------------------------------------------------------------------ receiving (main thread)

        public static void Receive(int actor, byte[] data, int offset, int length) {
            if (!VoiceChatPlugin.Enabled.Value || !VoiceCapture.SteamReady) {
                return;
            }
            var player = PhotonNetwork.CurrentRoom?.GetPlayer(actor);
            if (player == null) {
                return;
            }
            var speaker = Get(actor);
            speaker.LastPacketTime = Time.unscaledTime;
            if (VoiceChatPlugin.IsMuted(player.NickName)) {
                return;
            }
            Decode(speaker, data, offset, length);
        }

        /// <summary>Our own voice: to the test block (3D) and/or straight back to us (mic test).</summary>
        public static void ReceiveLoopback(byte[] data, int length) {
            if (TestBlock.Exists) {
                var block = Get(BlockActor);
                block.LastPacketTime = Time.unscaledTime;
                Decode(block, data, 0, length);
            }
            if (VoiceChatPlugin.HearMyself.Value) {
                var speaker = Get(LoopbackActor);
                speaker.LastPacketTime = Time.unscaledTime;
                Decode(speaker, data, 0, length);
            }
        }

        /// <summary>Recent output peak of the test block's speaker (0 to ~1), for its pulse.</summary>
        public static float TakeBlockPeak() =>
            Speakers.TryGetValue(BlockActor, out var speaker) ? speaker.TakePeak() : 0f;

        /// <summary>True while the test block's voice is muffled by walls.</summary>
        public static bool BlockMuffled =>
            Speakers.TryGetValue(BlockActor, out var speaker) && speaker.Muffled;

        /// <summary>A batch is a list of [2-byte little-endian length][Steam voice packet] frames.</summary>
        private static void Decode(Speaker speaker, byte[] data, int offset, int length) {
            int pos = offset;
            int end = offset + length;
            while (pos + 2 <= end) {
                int size = data[pos] | (data[pos + 1] << 8);
                pos += 2;
                if (size <= 0 || pos + size > end) {
                    VoiceChatPlugin.LogOnce("frame", "Got a malformed voice batch; dropped it.");
                    return;
                }
                DecodePacket(speaker, data, pos, size);
                pos += size;
            }
        }

        private static void DecodePacket(Speaker speaker, byte[] data, int offset, int length) {
            if (compressed.Length < length) {
                compressed = new byte[length * 2];
            }
            Buffer.BlockCopy(data, offset, compressed, 0, length);

            for (int attempt = 0; attempt < 3; attempt++) {
                var result = SteamUser.DecompressVoice(compressed, (uint)length, decoded, (uint)decoded.Length, out uint written, DecodeRate);
                if (result == EVoiceResult.k_EVoiceResultBufferTooSmall && decoded.Length < 4 * 1024 * 1024) {
                    decoded = new byte[decoded.Length * 2];
                    continue;
                }
                if (result == EVoiceResult.k_EVoiceResultOK && written > 0) {
                    speaker.Push(decoded, (int)written);
                } else if (result != EVoiceResult.k_EVoiceResultOK) {
                    VoiceChatPlugin.LogOnce("decode:" + result, "Steam couldn't decode a voice packet: " + result);
                }
                return;
            }
        }

        private static Speaker Get(int actor) {
            if (!Speakers.TryGetValue(actor, out var speaker)) {
                var kind = actor == LoopbackActor ? SpeakerKind.Loopback
                    : actor == BlockActor ? SpeakerKind.TestBlock
                    : SpeakerKind.Player;
                speaker = new Speaker(actor, kind);
                Speakers[actor] = speaker;
            }
            return speaker;
        }

        // ------------------------------------------------------------------ per frame (main thread)

        public static void Update() {
            EnsureRoot();
            var message = pendingLog;
            if (message != null) {
                pendingLog = null;
                VoiceChatPlugin.Log.LogInfo(message);
            }

            var room = PhotonNetwork.CurrentRoom;
            bool enabled = VoiceChatPlugin.Enabled.Value;
            foreach (var pair in Speakers) {
                var speaker = pair.Value;
                Transform anchor = null;
                bool spatial = false;
                bool muted = false;
                switch (speaker.Kind) {
                    case SpeakerKind.Player: {
                        var player = room?.GetPlayer(pair.Key);
                        if (player == null) {
                            Remove.Add(pair.Key);
                            continue;
                        }
                        anchor = AnchorFor(player.TagObject as Kobold);
                        spatial = anchor != null && VoiceChatPlugin.Proximity.Value;
                        muted = VoiceChatPlugin.IsMuted(player.NickName);
                        break;
                    }
                    case SpeakerKind.TestBlock:
                        anchor = TestBlock.Transform;
                        if (anchor == null) {
                            Remove.Add(pair.Key);
                            continue;
                        }
                        spatial = true; // the block is there to test 3D falloff, whatever the setting
                        break;
                    default:
                        if (!VoiceChatPlugin.HearMyself.Value && !speaker.Talking) {
                            Remove.Add(pair.Key);
                            continue;
                        }
                        break;
                }

                // Idle speakers keep no AudioSource running.
                if (!enabled || Time.unscaledTime - speaker.LastPacketTime > 5f) {
                    speaker.DestroySource();
                    continue;
                }
                speaker.Sync(anchor, spatial, muted);
                speaker.UpdateOcclusion(spatial ? anchor : null);
            }
            foreach (int actor in Remove) {
                Speakers[actor].DestroySource();
                Speakers.Remove(actor);
            }
            Remove.Clear();
        }

        private static Transform AnchorFor(Kobold kobold) {
            if (kobold == null) {
                return null;
            }
            var head = kobold.GetAttachPointTransform(Equipment.AttachPoint.Head);
            if (head != null) {
                return head;
            }
            return kobold.hip != null ? kobold.hip : kobold.transform;
        }

        public static void Clear() {
            foreach (var speaker in Speakers.Values) {
                speaker.DestroySource();
            }
            Speakers.Clear();
        }

        internal static AudioClip Carrier {
            get {
                if (carrier == null) {
                    const int length = DecodeRate / 10;
                    carrier = AudioClip.Create("VoiceChat_Carrier", length, 1, DecodeRate, false);
                    var ones = new float[length];
                    for (int i = 0; i < length; i++) {
                        ones[i] = 1f;
                    }
                    carrier.SetData(ones, 0);
                    // The game unloads unused assets between maps; keep ours.
                    carrier.hideFlags = HideFlags.DontUnloadUnusedAsset;
                }
                return carrier;
            }
        }

        /// <summary>Holds the rate probe. The game destroys DontDestroyOnLoad objects on map loads, so recreate it.</summary>
        private static void EnsureRoot() {
            if (root != null) {
                return;
            }
            root = new GameObject("VoiceChat_Audio");
            UnityEngine.Object.DontDestroyOnLoad(root);
            var source = root.AddComponent<AudioSource>();
            source.clip = Carrier;
            source.loop = true;
            source.spatialBlend = 0f;
            source.priority = 0;
            source.volume = 1f; // the probe outputs silence; full volume keeps Unity from virtualising it
            root.AddComponent<RateProbe>();
            source.Play();
        }

        internal static void ReportMeasuredRate(double framesPerSecond) {
            int[] standard = { 11025, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000, 176400, 192000 };
            int best = 0;
            double bestError = double.MaxValue;
            foreach (int rate in standard) {
                double error = Math.Abs(framesPerSecond / rate - 1.0);
                if (error < bestError) {
                    bestError = error;
                    best = rate;
                }
            }
            if (bestError < 0.03 && (best != OutputRate || !rateLogged)) {
                OutputRate = best;
                rateLogged = true;
                pendingLog = $"Audio output rate: {best} Hz (measured {framesPerSecond:0}).";
            }
        }
    }

    internal enum SpeakerKind {
        Player,
        Loopback,  // mic test: our own voice, flat (2D)
        TestBlock, // our own voice from the test block, in 3D
    }

    /// <summary>One voice: a jitter buffer plus the AudioSource that plays it.</summary>
    internal sealed class Speaker {
        private const int Prebuffer = VoicePlayback.DecodeRate * 12 / 100;  // wait for 120 ms before starting
        private const int MaxBuffered = VoicePlayback.DecodeRate * 5 / 10;  // drop backlog beyond 500 ms

        public readonly int Actor;
        public readonly SpeakerKind Kind;
        public float LastPacketTime = -10f;
        public volatile float Gain = 1f;

        private readonly float[] ring = new float[VoicePlayback.DecodeRate * 2];
        private readonly object gate = new object();
        private int read, write, count;
        private double phase;
        private bool playing;
        private volatile float peak;

        // Wall occlusion: a low-pass cutoff and extra gain, eased on the main thread, read by the audio thread.
        private const float OpenCutoff = 20000f;
        private volatile float cutoff = OpenCutoff;
        private volatile float occlusionGain = 1f;
        private float targetCutoff = OpenCutoff, targetOcclusionGain = 1f;
        private float currentCutoff = OpenCutoff, currentOcclusionGain = 1f;
        private float nextOcclusionCheck;
        private float lowPass1, lowPass2; // audio thread only

        private GameObject go;
        private AudioSource source;
        private Transform anchor;
        private bool anchored;

        public Speaker(int actor, SpeakerKind kind) {
            Actor = actor;
            Kind = kind;
        }

        public bool IsPlayer => Kind == SpeakerKind.Player;
        public bool Talking => Time.unscaledTime - LastPacketTime < 0.35f;
        public bool Muffled => currentCutoff < 6000f;

        /// <summary>Main thread: re-measure walls between us and <paramref name="target"/> (null = not spatial).</summary>
        public void UpdateOcclusion(Transform target) {
            if (target == null || !VoiceChatPlugin.MuffleBehindWalls.Value) {
                targetCutoff = OpenCutoff;
                targetOcclusionGain = 1f;
            } else if (Time.unscaledTime >= nextOcclusionCheck) {
                nextOcclusionCheck = Time.unscaledTime + 0.1f;
                if (Occlusion.Measure(target.position, out float blocked, out int walls)) {
                    float amount = Math.Max(0f, Math.Min(1f, VoiceChatPlugin.MuffleStrength.Value)) * blocked;
                    float wallCutoff = walls >= 3 ? 400f : walls == 2 ? 650f : 1100f;
                    targetCutoff = (float)Math.Exp(Lerp(Math.Log(OpenCutoff), Math.Log(wallCutoff), amount));
                    targetOcclusionGain = Math.Max(0.15f, 1f - amount * (0.3f + 0.15f * Math.Max(0, walls - 1)));
                }
            }

            // Ease over ~0.15 s so walking past a doorway doesn't click.
            double k = 1.0 - Math.Exp(-Time.unscaledDeltaTime * 8.0);
            currentCutoff = (float)Math.Exp(Lerp(Math.Log(currentCutoff), Math.Log(targetCutoff), k));
            currentOcclusionGain += (float)((targetOcclusionGain - currentOcclusionGain) * k);
            cutoff = currentCutoff;
            occlusionGain = currentOcclusionGain;
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>Loudest sample played since the last call (main thread).</summary>
        public float TakePeak() {
            float p = peak;
            peak = 0f;
            return p;
        }

        public void Push(byte[] pcm16, int bytes) {
            lock (gate) {
                for (int i = 0; i + 1 < bytes; i += 2) {
                    short sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
                    if (count == ring.Length) {
                        read = (read + 1) % ring.Length;
                        count--;
                    }
                    ring[write] = sample / 32768f;
                    write = (write + 1) % ring.Length;
                    count++;
                }
                if (count > MaxBuffered) {
                    int drop = count - Prebuffer;
                    read = (read + drop) % ring.Length;
                    count -= drop;
                }
            }
        }

        /// <summary>Audio thread: multiply the carrier in <paramref name="data"/> by the voice.</summary>
        public void Fill(float[] data, int channels) {
            double step = (double)VoicePlayback.DecodeRate / VoicePlayback.OutputRate;
            float gain = Gain * occlusionGain;
            float fc = cutoff;
            bool filter = fc < 18000f;
            // Two one-pole low-passes in a row (12 dB/octave): voices behind walls lose their highs.
            float alpha = filter ? (float)(1.0 - Math.Exp(-2.0 * Math.PI * fc / VoicePlayback.OutputRate)) : 1f;
            float loudest = 0f;
            lock (gate) {
                for (int i = 0; i < data.Length; i += channels) {
                    float s = 0f;
                    if (!playing && count >= Prebuffer) {
                        playing = true;
                    }
                    if (playing) {
                        if (count < 2) {
                            playing = false; // ran dry: wait for the buffer to refill
                        } else {
                            float a = ring[read];
                            float b = ring[(read + 1) % ring.Length];
                            s = a + (b - a) * (float)phase;
                            phase += step;
                            while (phase >= 1.0 && count > 1) {
                                phase -= 1.0;
                                read = (read + 1) % ring.Length;
                                count--;
                            }
                        }
                    }
                    if (filter) {
                        lowPass1 += alpha * (s - lowPass1);
                        lowPass2 += alpha * (lowPass1 - lowPass2);
                        s = lowPass2;
                    } else {
                        lowPass1 = s;
                        lowPass2 = s;
                    }
                    s *= gain;
                    if (s > 1f) {
                        s = 1f;
                    } else if (s < -1f) {
                        s = -1f;
                    }
                    float abs = s < 0f ? -s : s;
                    if (abs > loudest) {
                        loudest = abs;
                    }
                    for (int c = 0; c < channels; c++) {
                        data[i + c] *= s;
                    }
                }
            }
            if (loudest > peak) {
                peak = loudest;
            }
        }

        /// <param name="target">Where the voice comes from, or null to play it flat.</param>
        /// <param name="spatial">Use 3D falloff and panning (needs a target).</param>
        public void Sync(Transform target, bool spatial, bool muted) {
            if (go == null) {
                Create();
            }

            bool hasTarget = target != null;
            if (hasTarget != anchored || target != anchor || (hasTarget && go.transform.parent != target)) {
                go.transform.SetParent(target, false);
                go.transform.localPosition = Vector3.zero;
                anchor = target;
                anchored = hasTarget;
            }

            float distance = Math.Max(2f, VoiceChatPlugin.HearingDistance.Value);
            source.spatialBlend = spatial && hasTarget ? 1f : 0f;
            source.maxDistance = distance;
            source.minDistance = Math.Min(2f, distance * 0.5f);
            Gain = muted ? 0f : Math.Max(0f, VoiceChatPlugin.Volume.Value);
            if (!source.isPlaying) {
                source.Play();
            }
        }

        private void Create() {
            go = new GameObject(Kind == SpeakerKind.Loopback ? "VoiceChat_Loopback"
                : Kind == SpeakerKind.TestBlock ? "VoiceChat_TestBlockSpeaker"
                : "VoiceChat_Speaker_" + Actor);
            source = go.AddComponent<AudioSource>();
            source.clip = VoicePlayback.Carrier;
            source.loop = true;
            source.playOnAwake = false;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.dopplerLevel = 0f;
            source.spread = 0f;
            source.priority = 0;
            source.volume = 1f;
            go.AddComponent<VoiceFilter>().Speaker = this;
            anchor = null;
            anchored = false;
            lock (gate) {
                // Start fresh: anything buffered while there was no source is stale.
                int keep = Math.Min(count, Prebuffer);
                read = (read + count - keep) % ring.Length;
                count = keep;
                playing = false;
                phase = 0;
            }
            source.Play();
        }

        public void DestroySource() {
            if (go != null) {
                UnityEngine.Object.Destroy(go);
            }
            go = null;
            source = null;
            anchor = null;
            anchored = false;
        }
    }

    internal class VoiceFilter : MonoBehaviour {
        public Speaker Speaker;

        private void OnAudioFilterRead(float[] data, int channels) {
            var speaker = Speaker;
            if (speaker == null) {
                Array.Clear(data, 0, data.Length);
                return;
            }
            speaker.Fill(data, channels);
        }
    }

    /// <summary>Silent filter that counts frames per second to find the device sample rate.</summary>
    internal class RateProbe : MonoBehaviour {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private double windowStart = -1;
        private long frames;

        private void OnAudioFilterRead(float[] data, int channels) {
            double now = Clock.Elapsed.TotalSeconds;
            if (windowStart < 0) {
                windowStart = now;
                frames = 0;
            } else {
                frames += data.Length / Math.Max(1, channels);
                if (now - windowStart >= 2.0) {
                    VoicePlayback.ReportMeasuredRate(frames / (now - windowStart));
                    windowStart = now;
                    frames = 0;
                }
            }
            Array.Clear(data, 0, data.Length);
        }
    }
}
