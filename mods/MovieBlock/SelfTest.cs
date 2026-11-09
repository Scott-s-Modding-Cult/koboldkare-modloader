using System;
using System.Collections;
using System.IO;
using BepInEx;
using UnityEngine;

namespace MovieBlock {
    /// <summary>
    /// Developer check that needs no map or input: create BepInEx\config\koboldkare.movieblock.selftest and start the
    /// game. At the main menu it builds the screen in front of an offscreen camera, searches e621, plays the first
    /// video and writes snapshots plus a log to BepInEx\movieblock-selftest\. The trigger file is deleted first, so it
    /// runs once.
    /// </summary>
    internal class SelfTest : MonoBehaviour {
        private static string Trigger => Path.Combine(Paths.ConfigPath, "koboldkare.movieblock.selftest");
        private static string OutDir => Path.Combine(Paths.BepInExRootPath, "movieblock-selftest");

        public static void StartIfRequested() {
            if (!File.Exists(Trigger)) {
                return;
            }
            File.Delete(Trigger);
            var go = new GameObject("MovieBlock_SelfTest");
            DontDestroyOnLoad(go);
            go.AddComponent<SelfTest>();
        }

        private Camera cam;
        private RenderTexture target;
        private StreamWriter log;

        private void Log(string line) {
            MovieBlockPlugin.Log.LogInfo("[selftest] " + line);
            log?.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            log?.Flush();
        }

        private IEnumerator Start() {
            Directory.CreateDirectory(OutDir);
            log = new StreamWriter(Path.Combine(OutDir, "selftest.log"), false);
            yield return new WaitForSecondsRealtime(6f); // let the main menu settle

            var origin = new Vector3(0f, 5000f, 0f);
            target = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32);
            var camGo = new GameObject("MovieBlock_TestCamera");
            DontDestroyOnLoad(camGo);
            camGo.transform.position = origin;
            cam = camGo.AddComponent<Camera>();
            cam.targetTexture = target;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.25f, 0.3f, 0.35f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            cam.fieldOfView = 50f;

            MovieScreen screen = null;
            try {
                screen = MovieScreen.CreateForTest(new E621Source(), cam, origin + Vector3.forward * 2.9f);
                Log("screen built");
            } catch (Exception e) {
                Log("BUILD FAILED: " + e);
                yield break;
            }

            float until = Time.unscaledTime + 25f;
            while (screen.TestPostCount == 0 && Time.unscaledTime < until) {
                yield return null;
            }
            Log($"search: {screen.TestPostCount} posts; status: {screen.TestStatus}");
            yield return new WaitForSecondsRealtime(5f); // thumbnails
            yield return Capture("1-grid");

            bool acDone = false;
            screen.TestAutocomplete("kob", list => {
                Log("autocomplete 'kob': " + string.Join(", ", list.ConvertAll(t => $"{t.Name}({t.Count})").ToArray()));
                acDone = true;
            });
            until = Time.unscaledTime + 10f;
            while (!acDone && Time.unscaledTime < until) {
                yield return null;
            }
            screen.TestTypeAndSuggest("kobold drag");
            yield return new WaitForSecondsRealtime(3f);
            yield return Capture("2-autocomplete");

            if (screen.TestPostCount > 0) {
                screen.TestPlay(0);
                until = Time.unscaledTime + 90f;
                var player = screen.TestPlayer;
                while (Time.unscaledTime < until && !(player.isPlaying && player.time > 3.0)) {
                    yield return null;
                }
                Log($"video: prepared={player.isPrepared} playing={player.isPlaying} {player.width}x{player.height} " +
                    $"time={player.time:0.0}/{player.length:0.0} frame={player.frame} url={player.url} status: {screen.TestStatus}");
                yield return Capture("3-video");
                yield return new WaitForSecondsRealtime(2f);
                Log($"video later: time={player.time:0.0} frame={player.frame}");
            }

            screen.TestSetSource(new Rule34Source());
            yield return new WaitForSecondsRealtime(8f);
            Log("r34: " + screen.TestStatus + $" posts={screen.TestPostCount}");
            yield return Capture("4-r34");

            Destroy(screen.gameObject);
            MovieScreen.TestCamera = null;
            Destroy(camGo);
            target.Release();
            Log("done");
            log.Dispose();
            log = null;
            Destroy(gameObject);
        }

        private IEnumerator Capture(string name) {
            yield return new WaitForEndOfFrame();
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            var tex = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            File.WriteAllBytes(Path.Combine(OutDir, name + ".jpg"), tex.EncodeToJPG(85));
            Destroy(tex);
            Log("captured " + name);
        }
    }
}
