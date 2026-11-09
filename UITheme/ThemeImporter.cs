using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace UITheme {
    /// <summary>
    /// Installs themes into BepInEx/themes from a .zip, a theme.json, or a theme folder. Only theme
    /// files are copied (json/png/jpg/txt/md), paths can't escape the target folder, sizes are capped,
    /// theme.json is validated first, and an existing theme folder is never overwritten.
    /// </summary>
    internal static class ThemeImporter {
        private static readonly HashSet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".json", ".png", ".jpg", ".jpeg", ".txt", ".md", ".ttf", ".otf",
        };
        private const int MaxFiles = 300;
        private const long MaxFileBytes = 16L * 1024 * 1024;
        private const long MaxTotalBytes = 64L * 1024 * 1024;

        private static readonly Queue<Action> MainThread = new Queue<Action>();
        private static volatile bool dialogOpen;

        /// <summary>Text of the last import ("Imported: X" / "Import failed: ..."), shown in Options and the panel.</summary>
        public static string LastResult { get; private set; } = "";

        public static event Action ResultChanged;

        /// <summary>Run queued main-thread work (results from the file dialog thread).</summary>
        public static void Tick() {
            while (true) {
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
                    UIThemePlugin.Log.LogError("Theme import step failed: " + e);
                }
            }
        }

        public static void RunOnMainThread(Action action) {
            lock (MainThread) {
                MainThread.Enqueue(action);
            }
        }

        // ------------------------------------------------------------------ file dialog

        /// <summary>Open a Windows file picker (on its own thread, so the game keeps running) and import the choice.</summary>
        public static void ImportFromDialog() {
            if (dialogOpen) {
                return;
            }
            dialogOpen = true;
            IntPtr owner = IntPtr.Zero;
            try {
                owner = GetActiveWindow();
            } catch {
                // not on Windows / no window: dialog without owner
            }
            var thread = new Thread(() => {
                string path = null;
                try {
                    path = ShowOpenDialog(owner);
                } catch (Exception e) {
                    RunOnMainThread(() => SetResult("Import failed: couldn't open the file picker (" + e.Message + ")"));
                } finally {
                    dialogOpen = false;
                }
                if (!string.IsNullOrEmpty(path)) {
                    RunOnMainThread(() => Import(path));
                }
            }) { IsBackground = true, Name = "UITheme file picker" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        // ------------------------------------------------------------------ importing

        /// <summary>Import a theme from a .zip, a theme.json, or a folder containing theme.json.</summary>
        public static bool Import(string path) {
            try {
                path = path.Trim().Trim('"');
                string installedId;
                if (Directory.Exists(path)) {
                    installedId = ImportFolder(path);
                } else if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
                    installedId = ImportZip(path);
                } else if (File.Exists(path) && Path.GetFileName(path).Equals("theme.json", StringComparison.OrdinalIgnoreCase)) {
                    installedId = ImportFolder(Path.GetDirectoryName(path));
                } else {
                    throw new InvalidDataException("pick a theme .zip, a theme.json, or a theme folder");
                }

                ThemeLibrary.Reload();
                var theme = ThemeLibrary.Find(installedId);
                if (theme != null) {
                    ThemeLibrary.Select(theme);
                }
                SetResult($"Imported: {theme?.Name ?? installedId}");
                return true;
            } catch (Exception e) {
                UIThemePlugin.Log.LogWarning($"Theme import from '{path}' failed: {e}");
                SetResult("Import failed: " + e.Message);
                return false;
            }
        }

        private static string ImportZip(string zipPath) {
            using (var zip = new MiniZip(zipPath)) {
                var themeJsons = zip.Entries
                    .Where(e => !e.IsDirectory && Path.GetFileName(e.Name).Equals("theme.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Name.Count(c => c == '/'))
                    .ToList();
                if (themeJsons.Count == 0) {
                    throw new InvalidDataException("the zip has no theme.json");
                }
                if (themeJsons.Count > 1 && themeJsons[0].Name.Count(c => c == '/') == themeJsons[1].Name.Count(c => c == '/')) {
                    UIThemePlugin.Log.LogWarning($"{Path.GetFileName(zipPath)} has several themes; importing the first ({themeJsons[0].Name}).");
                }
                var main = themeJsons[0];
                string prefix = main.Name.Substring(0, main.Name.Length - "theme.json".Length); // "" or "Folder/"

                string json = Encoding.UTF8.GetString(zip.Read(main, MaxFileBytes));
                var probe = ThemeDefinition.Parse(json, "import", null); // throws if invalid
                string baseName = prefix.Length > 0 ? prefix.TrimEnd('/').Split('/').Last() : Path.GetFileNameWithoutExtension(zipPath);
                string target = UniqueTarget(SafeFolderName(baseName, probe.Name));

                string staging = target + ".importing";
                Directory.CreateDirectory(staging);
                try {
                    int files = 0;
                    long total = 0;
                    foreach (var entry in zip.Entries) {
                        if (entry.IsDirectory || !entry.Name.StartsWith(prefix, StringComparison.Ordinal)) {
                            continue;
                        }
                        string relative = entry.Name.Substring(prefix.Length);
                        if (!IsAllowed(relative)) {
                            continue;
                        }
                        if (++files > MaxFiles) {
                            throw new InvalidDataException($"too many files (max {MaxFiles})");
                        }
                        total += entry.UncompressedSize;
                        if (total > MaxTotalBytes) {
                            throw new InvalidDataException("theme is too large");
                        }
                        string dest = SafeCombine(staging, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.WriteAllBytes(dest, zip.Read(entry, MaxFileBytes));
                    }
                    Directory.Move(staging, target);
                } catch {
                    TryDelete(staging);
                    throw;
                }
                UIThemePlugin.Log.LogInfo($"Imported theme '{probe.Name}' from {Path.GetFileName(zipPath)} into {target}");
                return Path.GetFileName(target);
            }
        }

        private static string ImportFolder(string source) {
            source = Path.GetFullPath(source).TrimEnd('\\', '/');
            var themeJson = Path.Combine(source, "theme.json");
            if (!File.Exists(themeJson)) {
                throw new InvalidDataException("that folder has no theme.json");
            }
            var probe = ThemeDefinition.Parse(File.ReadAllText(themeJson), "import", source);
            var existing = ThemeLibrary.Themes.FirstOrDefault(t => t.Folder != null &&
                string.Equals(Path.GetFullPath(t.Folder).TrimEnd('\\', '/'), source, StringComparison.OrdinalIgnoreCase));
            if (existing != null) {
                return existing.Id; // already installed: just switch to it
            }

            string target = UniqueTarget(SafeFolderName(Path.GetFileName(source), probe.Name));
            string staging = target + ".importing";
            Directory.CreateDirectory(staging);
            try {
                int files = 0;
                long total = 0;
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) {
                    string relative = file.Substring(source.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (!IsAllowed(relative)) {
                        continue;
                    }
                    if (++files > MaxFiles) {
                        throw new InvalidDataException($"too many files (max {MaxFiles})");
                    }
                    var info = new FileInfo(file);
                    total += info.Length;
                    if (info.Length > MaxFileBytes || total > MaxTotalBytes) {
                        throw new InvalidDataException("theme is too large");
                    }
                    string dest = SafeCombine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest);
                }
                Directory.Move(staging, target);
            } catch {
                TryDelete(staging);
                throw;
            }
            UIThemePlugin.Log.LogInfo($"Imported theme '{probe.Name}' from {source} into {target}");
            return Path.GetFileName(target);
        }

        // ------------------------------------------------------------------ helpers

        private static bool IsAllowed(string relative) {
            if (string.IsNullOrEmpty(relative) || relative.Split('/').Length > 4) {
                return false;
            }
            string name = Path.GetFileName(relative);
            return !name.StartsWith(".") && AllowedExtensions.Contains(Path.GetExtension(name));
        }

        /// <summary>Combine and make sure the result stays inside <paramref name="root"/> (no zip-slip).</summary>
        private static string SafeCombine(string root, string relative) {
            if (relative.Contains("..") || Path.IsPathRooted(relative) || relative.Contains(":")) {
                throw new InvalidDataException($"unsafe path in theme: {relative}");
            }
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException($"unsafe path in theme: {relative}");
            }
            return full;
        }

        private static string SafeFolderName(string preferred, string themeName) {
            string Clean(string s) {
                var sb = new StringBuilder();
                foreach (char c in s ?? "") {
                    sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : c == ' ' ? '_' : '\0');
                }
                return sb.ToString().Replace("\0", "").Trim('_');
            }
            string name = Clean(preferred);
            if (name.Length == 0) {
                name = Clean(themeName);
            }
            return name.Length == 0 ? "ImportedTheme" : name.Length > 48 ? name.Substring(0, 48) : name;
        }

        /// <summary>A folder under BepInEx/themes that doesn't exist yet (never overwrites a theme).</summary>
        private static string UniqueTarget(string name) {
            Directory.CreateDirectory(ThemeLibrary.UserThemesFolder);
            string target = Path.Combine(ThemeLibrary.UserThemesFolder, name);
            for (int i = 2; Directory.Exists(target) || ThemeLibrary.Find(Path.GetFileName(target)) != null; i++) {
                target = Path.Combine(ThemeLibrary.UserThemesFolder, $"{name}_{i}");
            }
            return target;
        }

        private static void TryDelete(string folder) {
            try {
                if (Directory.Exists(folder)) {
                    Directory.Delete(folder, true);
                }
            } catch {
                // leftovers named *.importing are ignored by the theme scan (no theme.json at top level)
            }
        }

        private static void SetResult(string text) {
            LastResult = text;
            UIThemePlugin.Log.LogInfo(text);
            ResultChanged?.Invoke();
        }

        // ------------------------------------------------------------------ Win32 open-file dialog

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class OpenFileName {
            public int structSize = Marshal.SizeOf(typeof(OpenFileName));
            public IntPtr owner = IntPtr.Zero;
            public IntPtr instance = IntPtr.Zero;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public string file;
            public int maxFile;
            public string fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData = IntPtr.Zero;
            public IntPtr hook = IntPtr.Zero;
            public string templateName;
            public IntPtr reservedPtr = IntPtr.Zero;
            public int reservedInt;
            public int flagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        private static string ShowOpenDialog(IntPtr owner) {
            var ofn = new OpenFileName {
                owner = owner,
                filter = "UI theme (.zip or theme.json)\0*.zip;theme.json\0All files\0*.*\0\0",
                file = new string('\0', 2048),
                fileTitle = new string('\0', 512),
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
                title = "Import a KoboldKare UI theme",
                // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
                flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008,
            };
            ofn.maxFile = ofn.file.Length;
            ofn.maxFileTitle = ofn.fileTitle.Length;
            if (!GetOpenFileName(ofn)) {
                return null; // cancelled
            }
            int end = ofn.file.IndexOf('\0');
            return end >= 0 ? ofn.file.Substring(0, end) : ofn.file;
        }
    }
}
