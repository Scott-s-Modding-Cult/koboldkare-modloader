// Updates the Mod Manager itself from the repo's GitHub releases.
// The newest release that has KoboldKare-ModManager.zip wins; its exe replaces this one if its version is higher.
// So bump <Version> in ModManager.csproj for every release that changes the Mod Manager.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Web.Script.Serialization;

namespace KoboldKareModManager
{
    static class Updater
    {
        public const string Repo = "Scott-s-Modding-Cult/koboldkare-modloader";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        const string AssetName = "KoboldKare-ModManager.zip";
        const string ExeName = "KoboldKare Mod Manager.exe";

        public static Version Current => Assembly.GetExecutingAssembly().GetName().Version;

        public static string Show(Version version) => version.Revision > 0 ? version.ToString() : version.ToString(3);

        public class Update
        {
            public Version Version;
            public string Title, Page, NewExe;   // NewExe: the downloaded exe, in the temp folder.
        }

        // Null when this is already the newest. Throws when GitHub can't be reached.
        public static Update Find(Version current)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var web = new WebClient())
            {
                // GitHub's API refuses requests without a User-Agent.
                web.Headers[HttpRequestHeader.UserAgent] = "KoboldKare-ModManager";
                web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                // Not /releases/latest: that skips pre-releases, and this repo publishes those.
                string json = web.DownloadString("https://api.github.com/repos/" + Repo + "/releases?per_page=30");
                var releases = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<Dictionary<string, object>>>(json);

                foreach (var release in releases.Where(r => !Is(r, "draft")).OrderByDescending(r => Text(r, "published_at")))
                {
                    var asset = Assets(release).FirstOrDefault(a => Text(a, "name").Equals(AssetName, StringComparison.OrdinalIgnoreCase));
                    if (asset == null) { continue; }

                    // The zip is tiny, so read the version from the exe inside it rather than trusting tag names.
                    string temp = Path.Combine(Path.GetTempPath(), "KoboldKareModManager-update-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    string zip = Path.Combine(temp, AssetName);
                    web.Headers[HttpRequestHeader.Accept] = "application/octet-stream";
                    web.DownloadFile(Text(asset, "browser_download_url"), zip);
                    string exe = Path.Combine(temp, ExeName);
                    using (ZipArchive archive = ZipFile.OpenRead(zip))
                    {
                        ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase));
                        if (entry == null) { Discard(temp); return null; }
                        entry.ExtractToFile(exe, true);
                    }
                    Version version = AssemblyName.GetAssemblyName(exe).Version;
                    if (version <= current) { Discard(temp); return null; }
                    return new Update { Version = version, Title = Text(release, "name"), Page = Text(release, "html_url"), NewExe = exe };
                }
                return null;
            }
        }

        // Swaps the new exe in. Windows lets a running exe be renamed, just not overwritten, so this one becomes .old.
        public static void Apply(Update update, string exe)
        {
            string old = exe + ".old";
            if (File.Exists(old)) { File.Delete(old); }
            File.Move(exe, old);
            try
            {
                File.Copy(update.NewExe, exe);
            }
            catch (Exception)
            {
                File.Move(old, exe);
                throw;
            }
            Discard(update);
        }

        public static void Discard(Update update)
        {
            if (update?.NewExe != null) { Discard(Path.GetDirectoryName(update.NewExe)); }
        }

        static void Discard(string temp)
        {
            try { if (Directory.Exists(temp)) { Directory.Delete(temp, true); } }
            catch (Exception) { }
        }

        // The .old copy left by the last update. It may still be closing, so a failure just waits for next time.
        public static void CleanUp(string exe)
        {
            try { if (File.Exists(exe + ".old")) { File.Delete(exe + ".old"); } }
            catch (Exception) { }
        }

        static string Text(Dictionary<string, object> json, string key) =>
            json.TryGetValue(key, out object value) && value != null ? value.ToString() : "";

        static bool Is(Dictionary<string, object> json, string key) =>
            json.TryGetValue(key, out object value) && value is bool b && b;

        static IEnumerable<Dictionary<string, object>> Assets(Dictionary<string, object> release) =>
            release.TryGetValue("assets", out object assets) && assets is IEnumerable list
                ? list.OfType<Dictionary<string, object>>()
                : Enumerable.Empty<Dictionary<string, object>>();
    }
}
