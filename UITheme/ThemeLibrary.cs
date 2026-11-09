using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using KoboldKare.ModKit;
using TMPro;
using UnityEngine;

namespace UITheme {
    /// <summary>Finds theme folders, loads their fonts/sprites, and switches the active theme.</summary>
    internal static class ThemeLibrary {
        /// <summary>User themes: BepInEx/themes/&lt;Theme&gt;/theme.json</summary>
        public static string UserThemesFolder => Path.Combine(Paths.BepInExRootPath, "themes");

        private static readonly List<ThemeDefinition> themes = new List<ThemeDefinition>();
        private static readonly Dictionary<string, TMP_FontAsset> FontCache = new Dictionary<string, TMP_FontAsset>();

        public static IReadOnlyList<ThemeDefinition> Themes => themes;
        public static ThemeDefinition Current { get; private set; } = ThemeDefinition.CreateDefault();
        public static TMP_FontAsset CurrentFont { get; private set; }
        public static TMP_FontAsset CurrentHeadingFont { get; private set; }
        public static int CurrentIndex => Math.Max(0, themes.IndexOf(Current));

        public static event Action ThemesReloaded;

        /// <summary>Scan BepInEx/themes and BepInEx/plugins/*/themes for theme folders.</summary>
        public static void Reload() {
            string currentId = Current?.Id ?? "";
            themes.Clear();
            themes.Add(ThemeDefinition.CreateDefault());

            EnsureUserFolder();
            var roots = new List<string> { UserThemesFolder };
            if (Directory.Exists(Paths.PluginPath)) {
                roots.AddRange(Directory.GetDirectories(Paths.PluginPath, "themes", SearchOption.AllDirectories));
            }
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase)) {
                if (!Directory.Exists(root)) {
                    continue;
                }
                foreach (var folder in Directory.GetDirectories(root)) {
                    if (folder.EndsWith(".importing", StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(Path.Combine(folder, "theme.json"))) {
                        continue;
                    }
                    try {
                        var theme = ThemeDefinition.Load(folder);
                        if (themes.Any(t => string.Equals(t.Id, theme.Id, StringComparison.OrdinalIgnoreCase))) {
                            UIThemePlugin.Log.LogWarning($"Theme '{theme.Id}' found twice; using the first ({folder} skipped).");
                            continue;
                        }
                        foreach (var w in theme.Warnings) {
                            UIThemePlugin.Log.LogWarning($"Theme '{theme.Id}': {w}");
                        }
                        themes.Add(theme);
                    } catch (Exception e) {
                        UIThemePlugin.Log.LogError($"Couldn't load theme in {folder}: {e.Message}");
                    }
                }
            }
            themes.Sort((a, b) => a.IsDefault ? -1 : b.IsDefault ? 1 : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            UIThemePlugin.Log.LogInfo($"Themes: {string.Join(", ", themes.Select(t => t.Name).ToArray())}");

            Current = themes.FirstOrDefault(t => string.Equals(t.Id, currentId, StringComparison.OrdinalIgnoreCase)) ?? themes[0];
            ThemesReloaded?.Invoke();
        }

        public static ThemeDefinition Find(string idOrName) =>
            themes.FirstOrDefault(t => string.Equals(t.Id, idOrName, StringComparison.OrdinalIgnoreCase)) ??
            themes.FirstOrDefault(t => string.Equals(t.Name, idOrName, StringComparison.OrdinalIgnoreCase));

        public static void SelectIndex(int index) {
            if (index >= 0 && index < themes.Count) {
                Select(themes[index]);
            }
        }

        /// <summary>Make <paramref name="theme"/> active: restyle the game UI, ModKit UI, save the choice.</summary>
        public static void Select(ThemeDefinition theme) {
            Current = theme ?? themes.FirstOrDefault() ?? ThemeDefinition.CreateDefault();
            UIThemePlugin.SelectedTheme.Value = Current.Id;

            LoadAssets(Current);
            Restyler.Apply(Current);
            PushToModKit(Current);
            ThemeSetting.Sync();
            UIThemePlugin.Log.LogInfo($"Theme: {Current.Name}");
        }

        /// <summary>Re-apply the current theme (e.g. after editing its files and reloading).</summary>
        public static void Reapply() => Select(Current);

        // ------------------------------------------------------------------ assets

        private static void LoadAssets(ThemeDefinition theme) {
            CurrentFont = theme.IsDefault ? null : GetFont(theme.Fonts, theme);
            CurrentHeadingFont = theme.IsDefault ? null : GetFont(theme.HeadingFonts, theme) ?? CurrentFont;
            EnsureSprites(theme);
        }

        /// <summary>Load a theme's images (also used by the browser to draw swatches).</summary>
        public static void EnsureSprites(ThemeDefinition theme) {
            foreach (var spec in theme.Sprites.Values.Concat(theme.RoleSprites.Values)) {
                if (spec.Loaded == null) {
                    spec.Loaded = LoadSprite(theme, spec);
                }
            }
        }

        /// <summary>
        /// First usable font from the list, as a TMP font with the game's font as glyph fallback. Entries are
        /// either font files in the theme folder ("fonts/VT323-Regular.ttf") or installed Windows font names
        /// ("Courier New"). Fonts are loaded from their files: TMP can't build font assets from Unity's
        /// OS-font handles (CreateDynamicFontFromOSFont), which is why installed fonts used to be ignored.
        /// </summary>
        private static TMP_FontAsset GetFont(List<string> names, ThemeDefinition theme) {
            if (names == null || names.Count == 0) {
                return null;
            }
            foreach (var name in names) {
                string path = ResolveFontFile(name, theme.Folder);
                if (path == null) {
                    continue;
                }
                if (FontCache.TryGetValue(path, out var cached) && cached != null) {
                    return cached;
                }
                try {
                    var font = new Font(path);
                    var asset = TMP_FontAsset.CreateFontAsset(font);
                    if (asset == null) {
                        UIThemePlugin.Log.LogWarning($"Font '{name}' ({path}) couldn't be turned into a TMP font; trying the next one.");
                        continue;
                    }
                    asset.name = Path.GetFileNameWithoutExtension(name) + " (UITheme)";
                    Theme.Load();
                    if (Theme.GameFont != null) {
                        // Characters the theme font lacks (CJK, symbols) still render with the game font.
                        if (asset.fallbackFontAssetTable == null) {
                            asset.fallbackFontAssetTable = new List<TMP_FontAsset>();
                        }
                        asset.fallbackFontAssetTable.Add(Theme.GameFont);
                    }
                    FontCache[path] = asset;
                    UIThemePlugin.Log.LogInfo($"Font '{name}' loaded from {path}");
                    return asset;
                } catch (Exception e) {
                    UIThemePlugin.Log.LogWarning($"Font '{name}' ({path}) couldn't be loaded: {e.Message}");
                }
            }
            UIThemePlugin.Log.LogInfo($"None of these fonts are available, keeping the game font: {string.Join(", ", names.ToArray())}");
            return null;
        }

        private static readonly string[] FontExtensions = { ".ttf", ".otf", ".ttc" };

        /// <summary>A font file bundled with the theme, or the file of an installed Windows font.</summary>
        private static string ResolveFontFile(string name, string themeFolder) {
            if (string.IsNullOrWhiteSpace(name)) {
                return null;
            }
            if (FontExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) {
                if (themeFolder == null || name.Contains("..")) {
                    return null;
                }
                string bundled = Path.Combine(themeFolder, name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(bundled)) {
                    return bundled;
                }
                UIThemePlugin.Log.LogWarning($"Font file not found in theme: {name}");
                return null;
            }
            if (installedFontFiles == null) {
                installedFontFiles = ReadInstalledFonts();
                UIThemePlugin.Log.LogInfo($"Found {installedFontFiles.Count} installed font names in the Windows font registry.");
            }
            return installedFontFiles.TryGetValue(name.Trim(), out var file) && File.Exists(file) ? file : null;
        }

        private static Dictionary<string, string> installedFontFiles;

        /// <summary>Installed font name -> file, from the Windows font registry (machine-wide and per-user installs).</summary>
        private static Dictionary<string, string> ReadInstalledFonts() {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string windowsFonts = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Fonts");
            const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser }) {
                try {
                    using (var fonts = hive.OpenSubKey(key)) {
                        if (fonts == null) {
                            continue;
                        }
                        foreach (var valueName in fonts.GetValueNames()) {
                            if (!(fonts.GetValue(valueName) is string file) || string.IsNullOrEmpty(file)) {
                                continue;
                            }
                            string full = Path.IsPathRooted(file) ? file : Path.Combine(windowsFonts, file);
                            // "Cambria & Cambria Math (TrueType)" -> "Cambria", "Cambria Math"
                            string names = valueName;
                            int paren = names.LastIndexOf(" (", StringComparison.Ordinal);
                            if (paren > 0) {
                                names = names.Substring(0, paren);
                            }
                            foreach (var n in names.Split(new[] { " & " }, StringSplitOptions.RemoveEmptyEntries)) {
                                string clean = n.Trim();
                                if (!map.ContainsKey(clean)) {
                                    map[clean] = full;
                                }
                                // Also let "Courier New Regular" find "Courier New".
                                if (clean.EndsWith(" Regular", StringComparison.OrdinalIgnoreCase)) {
                                    string family = clean.Substring(0, clean.Length - " Regular".Length);
                                    if (!map.ContainsKey(family)) {
                                        map[family] = full;
                                    }
                                }
                            }
                        }
                    }
                } catch (Exception e) {
                    UIThemePlugin.Log.LogWarning("Couldn't read installed fonts from the registry: " + e.Message);
                }
            }
            return map;
        }

        private static Sprite LoadSprite(ThemeDefinition theme, SpriteSpec spec) {
            var path = Path.Combine(theme.Folder, spec.File);
            if (!File.Exists(path)) {
                UIThemePlugin.Log.LogWarning($"Theme '{theme.Id}': sprite file not found: {spec.File}");
                return null;
            }
            try {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(File.ReadAllBytes(path))) {
                    UIThemePlugin.Log.LogWarning($"Theme '{theme.Id}': {spec.File} isn't a readable PNG/JPG.");
                    return null;
                }
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = spec.Pixelated ? FilterMode.Point : FilterMode.Bilinear;
                texture.name = theme.Id + "/" + spec.File;
                var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f),
                    spec.PixelsPerUnit, 0, SpriteMeshType.FullRect, spec.Border);
                sprite.name = texture.name;
                return sprite;
            } catch (Exception e) {
                UIThemePlugin.Log.LogWarning($"Theme '{theme.Id}': couldn't load {spec.File}: {e.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------ ModKit panel

        private static void PushToModKit(ThemeDefinition theme) {
            if (theme.IsDefault) {
                Theme.ResetPalette();
                return;
            }
            var p = Theme.Default;
            void Set(string role, Action<Color> assign) {
                if (theme.TryGetExplicit(role, out var c)) {
                    assign(c);
                }
            }
            Set(Roles.Window, c => p.Body = c);
            Set(Roles.Panel, c => p.Card = new Color(c.r, c.g, c.b, c.a * 0.56f));
            Set(Roles.TitleBar, c => p.TitleBar = c);
            Set(Roles.TitleText, c => p.TitleText = c);
            Set(Roles.Button, c => p.Button = c);
            Set(Roles.Text, c => p.Text = c);
            Set(Roles.Hover, c => p.Highlight = c);
            Set(Roles.Pressed, c => p.Pressed = c);
            Set(Roles.ButtonOk, c => p.Okay = c);
            Set(Roles.ButtonCancel, c => p.Cancel = c);
            Set(Roles.Danger, c => p.Danger = c);
            Set(Roles.Accent, c => {
                p.Accent = c;
                p.ToggleOn = new Color(c.r, c.g, c.b, 0.45f);
            });
            Set(Roles.Icon, c => p.Icon = c);

            var sprites = theme.Sprites.Where(s => s.Value.Loaded != null).ToDictionary(s => s.Key, s => s.Value.Loaded);
            var roleSprites = theme.RoleSprites.Where(s => s.Value.Loaded != null).ToDictionary(
                s => s.Key, s => new RoleSprite { Sprite = s.Value.Loaded, Type = s.Value.ImageType });
            Theme.ApplyPalette(p, CurrentFont, CurrentHeadingFont, sprites, roleSprites);
        }

        private static void EnsureUserFolder() {
            try {
                Directory.CreateDirectory(UserThemesFolder);
                var readme = Path.Combine(UserThemesFolder, "README.md");
                if (!File.Exists(readme)) {
                    File.WriteAllText(readme, ThemeDocs.Readme);
                }
            } catch (Exception e) {
                UIThemePlugin.Log.LogWarning("Couldn't create the themes folder: " + e.Message);
            }
        }
    }
}
