using System;
using Steamworks;
using UnityEngine;

namespace VoiceChat {
    /// <summary>
    /// Records the microphone through Steam (the device picked in Steam > Settings > Voice). Steam
    /// compresses the audio and only produces data while you're actually speaking. Packets are batched
    /// for ~100 ms before sending to keep the Photon message rate low (about 10 per second per speaker).
    /// </summary>
    internal static class VoiceCapture {
        private const float FlushInterval = 0.1f;

        private static readonly byte[] Chunk = new byte[8 * 1024];
        private static readonly byte[] Pending = new byte[32 * 1024];
        private static int pendingLength;
        private static float flushAt;
        private static bool recording;

        /// <summary>Time.unscaledTime of the last audio Steam gave us.</summary>
        public static float LastVoiceTime { get; private set; } = -10f;

        public static bool Recording => recording;
        public static bool Speaking => Time.unscaledTime - LastVoiceTime < 0.3f;

        public static bool SteamReady {
            get {
                try {
                    return SteamManager.Initialized;
                } catch {
                    return false;
                }
            }
        }

        /// <param name="wantRecord">Whether the mic should be live this frame.</param>
        /// <param name="loopback">Also play our own voice back locally (testing).</param>
        public static void Update(bool wantRecord, bool loopback) {
            if (!SteamReady) {
                recording = false;
                pendingLength = 0;
                return;
            }

            if (wantRecord && !recording) {
                SteamUser.StartVoiceRecording();
                recording = true;
            } else if (!wantRecord && recording) {
                // Steam keeps handing out the last few hundred ms after this; Drain picks them up.
                SteamUser.StopVoiceRecording();
                recording = false;
            }

            Drain();

            if (pendingLength > 0 && (Time.unscaledTime >= flushAt || pendingLength > Pending.Length / 2)) {
                Flush(loopback);
            }
        }

        public static void Stop() {
            if (recording && SteamReady) {
                SteamUser.StopVoiceRecording();
            }
            recording = false;
            pendingLength = 0;
        }

        private static void Drain() {
            for (int i = 0; i < 4; i++) {
                if (SteamUser.GetAvailableVoice(out uint available) != EVoiceResult.k_EVoiceResultOK || available == 0) {
                    return;
                }
                var result = SteamUser.GetVoice(true, Chunk, (uint)Chunk.Length, out uint written);
                if (result != EVoiceResult.k_EVoiceResultOK || written == 0) {
                    return;
                }
                if (pendingLength + 2 + written > Pending.Length) {
                    // Shouldn't happen at 10 flushes a second; drop the backlog rather than grow forever.
                    pendingLength = 0;
                }
                if (pendingLength == 0) {
                    flushAt = Time.unscaledTime + FlushInterval;
                }
                // Each Steam voice packet ends in its own checksum and must be decoded on its own, so a
                // batch is a list of [2-byte little-endian length][packet] frames, not one glued blob.
                Pending[pendingLength] = (byte)(written & 0xFF);
                Pending[pendingLength + 1] = (byte)(written >> 8);
                Buffer.BlockCopy(Chunk, 0, Pending, pendingLength + 2, (int)written);
                pendingLength += 2 + (int)written;
                LastVoiceTime = Time.unscaledTime;
            }
        }

        private static void Flush(bool loopback) {
            try {
                VoiceNet.Send(Pending, pendingLength);
            } catch (Exception e) {
                VoiceChatPlugin.LogOnce("send", "Failed to send voice: " + e);
            }
            if (loopback) {
                VoicePlayback.ReceiveLoopback(Pending, pendingLength);
            }
            pendingLength = 0;
        }
    }
}
