using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine.Networking;

namespace MovieBlock {
    /// <summary>A download the caller can cancel and watch.</summary>
    internal class Transfer {
        public bool Cancelled;
        public long Received;
        public long Total = -1;
        internal UnityWebRequest Request;

        public void Cancel() {
            if (Cancelled) {
                return;
            }
            Cancelled = true;
            try {
                Request?.Abort();
            } catch {
                // Already finished or disposed.
            }
        }

        public float Progress => Total > 0 ? Math.Min(1f, Received / (float)Total) : -1f;
    }

    /// <summary>
    /// HTTP through Unity's native UnityWebRequest (it does TLS in the engine; Mono's own TLS stack is broken in this
    /// game). Requests start and are polled on the main thread in <see cref="Pump"/>; parsing and file writes run on
    /// thread-pool threads, and every callback comes back on the main thread.
    /// </summary>
    internal static class Net {
        // e621 asks API clients for a descriptive User-Agent naming the project.
        public const string UserAgent = "KoboldKare-MovieBlock/1.0 (github.com/Scott-s-Modding-Cult/koboldkare-modloader)";

        private class Active {
            public UnityWebRequest Request;
            public Transfer Transfer;
            public Action<byte[]> Done;
            public Action<string> Fail;
            public long MaxBytes;
        }

        private static readonly List<Active> Requests = new List<Active>();
        private static readonly Queue<Action> MainThread = new Queue<Action>();

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// GET a small resource. <paramref name="parse"/> runs on a worker thread (no Unity calls in it);
        /// <paramref name="done"/> or <paramref name="fail"/> runs on the main thread.
        /// </summary>
        public static void Get<T>(string url, Func<byte[], T> parse, Action<T> done, Action<string> fail,
                                  IDictionary<string, string> headers = null, int maxBytes = 8 * 1024 * 1024) {
            Start(url, headers, 20, null, maxBytes, bytes => ThreadPool.QueueUserWorkItem(_ => {
                try {
                    T result = parse(bytes);
                    Post(() => done(result));
                } catch (Exception e) {
                    string message = e.GetBaseException().Message;
                    Post(() => fail?.Invoke(message));
                }
            }), fail);
        }

        /// <summary>Downloads to <paramref name="path"/> (via a .part file). Callbacks run on the main thread.</summary>
        public static Transfer Download(string url, string path, long expectedSize, Action done, Action<string> fail) {
            var transfer = new Transfer { Total = expectedSize > 0 ? expectedSize : -1 };
            Start(url, null, 0, transfer, long.MaxValue, bytes => {
                if (transfer.Cancelled) {
                    return;
                }
                ThreadPool.QueueUserWorkItem(_ => {
                    string part = path + ".part";
                    try {
                        File.WriteAllBytes(part, bytes);
                        if (File.Exists(path)) {
                            File.Delete(path);
                        }
                        File.Move(part, path);
                        Post(() => {
                            if (!transfer.Cancelled) {
                                done();
                            }
                        });
                    } catch (Exception e) {
                        TryDelete(part);
                        string message = e.GetBaseException().Message;
                        Post(() => fail?.Invoke(message));
                    }
                });
            }, error => {
                if (!transfer.Cancelled) {
                    fail?.Invoke(error);
                }
            });
            return transfer;
        }

        public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

        /// <summary>Main thread, every frame: finish requests and run queued callbacks.</summary>
        public static void Pump() {
            for (int i = Requests.Count - 1; i >= 0; i--) {
                var active = Requests[i];
                var request = active.Request;
                if (active.Transfer != null) {
                    if (active.Transfer.Cancelled) {
                        Requests.RemoveAt(i);
                        request.Dispose();
                        continue;
                    }
                    active.Transfer.Received = (long)request.downloadedBytes;
                }
                if (!request.isDone) {
                    if ((long)request.downloadedBytes > active.MaxBytes) {
                        request.Abort();
                    }
                    continue;
                }
                Requests.RemoveAt(i);
                try {
                    Finish(active);
                } catch (Exception e) {
                    MovieBlockPlugin.Log.LogError("Request handling failed: " + e);
                } finally {
                    request.Dispose();
                }
            }

            for (int i = 0; i < 64; i++) {
                Action next;
                lock (MainThread) {
                    if (MainThread.Count == 0) {
                        return;
                    }
                    next = MainThread.Dequeue();
                }
                try {
                    next();
                } catch (Exception e) {
                    MovieBlockPlugin.Log.LogError("Callback failed: " + e);
                }
            }
        }

        // ------------------------------------------------------------------ internals

        private static void Start(string url, IDictionary<string, string> headers, int timeoutSeconds, Transfer transfer, long maxBytes,
                                  Action<byte[]> done, Action<string> fail) {
            UnityWebRequest request;
            try {
                // Last line of defence: only ever talk to the two sites, over https.
                if (!Source.IsTrusted(url, "e621.net") && !Source.IsTrusted(url, "rule34.xxx")) {
                    fail?.Invoke("blocked a non-https or off-site URL");
                    return;
                }
                request = UnityWebRequest.Get(url);
                request.SetRequestHeader("User-Agent", UserAgent);
                if (headers != null) {
                    foreach (var pair in headers) {
                        request.SetRequestHeader(pair.Key, pair.Value);
                    }
                }
                if (timeoutSeconds > 0) {
                    request.timeout = timeoutSeconds;
                }
                request.redirectLimit = 8;
                request.SendWebRequest();
            } catch (Exception e) {
                fail?.Invoke(e.GetBaseException().Message);
                return;
            }
            if (transfer != null) {
                transfer.Request = request;
            }
            Requests.Add(new Active { Request = request, Transfer = transfer, Done = done, Fail = fail, MaxBytes = maxBytes });
        }

        private static void Finish(Active active) {
            var request = active.Request;
            if (active.Transfer != null && active.Transfer.Cancelled) {
                return;
            }
            if (request.result != UnityWebRequest.Result.Success) {
                string body = "";
                try {
                    body = (request.downloadHandler?.text ?? "").Trim();
                } catch {
                    // No body.
                }
                if (body.Length > 160) {
                    body = body.Substring(0, 160) + "...";
                }
                string message = request.responseCode > 0
                    ? $"HTTP {request.responseCode}{(body.Length > 0 && !body.StartsWith("<") ? ": " + body : "")}"
                    : request.error ?? "request failed";
                if ((long)request.downloadedBytes > active.MaxBytes) {
                    message = "response too large";
                }
                active.Fail?.Invoke(message);
                return;
            }
            byte[] bytes = ReadBytes(request.downloadHandler);
            if (bytes == null) {
                active.Fail?.Invoke("couldn't read the downloaded data");
                return;
            }
            active.Done(bytes);
        }

        private static void Post(Action action) {
            lock (MainThread) {
                MainThread.Enqueue(action);
            }
        }

        private static void TryDelete(string path) {
            try {
                if (File.Exists(path)) {
                    File.Delete(path);
                }
            } catch {
                // Locked or gone; the cache trim gets it later.
            }
        }

        // The game's stripped module dropped DownloadHandler.data. The internal native call behind it survived:
        //   static byte* DownloadHandler.InternalGetByteArray(DownloadHandler dh, out int length)
        private unsafe delegate byte* ByteArrayFn(DownloadHandler handler, out int length);

        private static ByteArrayFn byteArray;
        private static MethodInfo byteArrayMethod;
        private static bool byteArrayLookedUp;

        private static unsafe byte[] ReadBytes(DownloadHandler handler) {
            if (handler == null) {
                return null;
            }
            if (!byteArrayLookedUp) {
                byteArrayLookedUp = true;
                byteArrayMethod = typeof(DownloadHandler).GetMethod("InternalGetByteArray", BindingFlags.NonPublic | BindingFlags.Static);
                if (byteArrayMethod != null) {
                    try {
                        byteArray = (ByteArrayFn)Delegate.CreateDelegate(typeof(ByteArrayFn), byteArrayMethod);
                    } catch (Exception e) {
                        MovieBlockPlugin.Log.LogWarning("Falling back to reflection for download data: " + e.Message);
                    }
                } else {
                    MovieBlockPlugin.Log.LogError("DownloadHandler.InternalGetByteArray not found; downloads can't be read.");
                }
            }
            int length;
            byte* data;
            if (byteArray != null) {
                data = byteArray(handler, out length);
            } else if (byteArrayMethod != null) {
                var args = new object[] { handler, 0 };
                object result = byteArrayMethod.Invoke(null, args);
                length = (int)args[1];
                data = (byte*)Pointer.Unbox(result);
            } else {
                return null;
            }
            var bytes = new byte[Math.Max(0, length)];
            if (length > 0 && data != null) {
                Marshal.Copy((IntPtr)data, bytes, 0, length);
            }
            return bytes;
        }
    }
}
