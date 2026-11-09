using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SimpleJSON;
using UnityEngine;

namespace UITheme {
    /// <summary>
    /// The colour roles a theme can set. Each maps to how KoboldKare's own UI uses colour, so one theme
    /// restyles every menu. Defaults are the game's original values.
    /// </summary>
    internal static class Roles {
        public const string Overlay = "overlay";             // full-screen dimmers behind popups
        public const string Window = "window";               // popup/window bodies (black, no sprite)
        public const string Panel = "panel";                 // translucent rounded panels, lists, scroll views
        public const string PanelLight = "panelLight";       // faint white panels (stat blocks)
        public const string Surface = "surface";             // main slate menu backgrounds
        public const string TitleBar = "titleBar";           // blue headers / top panels
        public const string Button = "button";               // main buttons
        public const string ButtonOk = "buttonOk";           // confirm-style buttons
        public const string ButtonAlt = "buttonAlt";         // secondary buttons
        public const string ButtonCancel = "buttonCancel";   // cancel / destructive buttons
        public const string Control = "control";             // dropdowns, inputs, toggles, slider tracks
        public const string Scrollbar = "scrollbar";
        public const string ScrollbarTrack = "scrollbarTrack";
        public const string Accent = "accent";               // slider handles/fills, highlighted boxes
        public const string Text = "text";                   // normal text on themed backgrounds
        public const string TitleText = "titleText";         // text on title bars
        public const string ButtonText = "buttonText";       // text on buttons
        public const string FieldText = "fieldText";         // text inside controls
        public const string HighlightText = "highlightText"; // yellow values/amounts
        public const string Icon = "icon";                   // game icons (tinted, never replaced)
        public const string Hover = "hover";                 // button hover tint
        public const string Pressed = "pressed";             // button pressed tint
        public const string Selected = "selected";           // controller-selected tint
        public const string Danger = "danger";               // error / warning text

        public static readonly string[] All = {
            Overlay, Window, Panel, PanelLight, Surface, TitleBar, Button, ButtonOk, ButtonAlt, ButtonCancel, Control,
            Scrollbar, ScrollbarTrack, Accent, Text, TitleText, ButtonText, FieldText, HighlightText, Icon, Hover, Pressed,
            Selected, Danger,
        };

        /// <summary>If a theme sets the right-hand role but not the left, the left follows it.</summary>
        public static readonly Dictionary<string, string> Fallback = new Dictionary<string, string> {
            { TitleText, Text }, { ButtonText, Text }, { FieldText, Text }, { HighlightText, Accent },
            { Selected, Hover }, { PanelLight, Panel }, { Window, Panel }, { Scrollbar, TitleBar },
            { ScrollbarTrack, Panel }, { ButtonAlt, ButtonOk }, { Icon, Text },
        };

        /// <summary>The game's own colours (what the default theme looks like).</summary>
        public static readonly Dictionary<string, Color> GameDefaults = new Dictionary<string, Color> {
            { Overlay, new Color(0f, 0f, 0f, 0.9f) },
            { Window, new Color(0f, 0f, 0f, 0.8f) },
            { Panel, new Color(0f, 0f, 0f, 0.8f) },
            { PanelLight, new Color(1f, 1f, 1f, 0.39f) },
            { Surface, Hex("#384451FC") },
            { TitleBar, Hex("#378FFA") },
            { Button, new Color(0.248f, 0.376f, 0.532f, 0.94f) },
            { ButtonOk, Hex("#77B4FF4D") },
            { ButtonAlt, Hex("#77B4FF8C") },
            { ButtonCancel, Hex("#FF7D774D") },
            { Control, Color.white },
            { Scrollbar, Hex("#378FFA") },
            { ScrollbarTrack, Hex("#778EFF1C") },
            { Accent, Hex("#A0E4FF") },
            { Text, Hex("#EDEBFF") },
            { TitleText, Color.white },
            { ButtonText, Hex("#EDEBFF") },
            { FieldText, Hex("#323232") },
            { HighlightText, Hex("#FFF296") },
            { Icon, Color.white },
            { Hover, new Color(0.49f, 1f, 0.9435571f, 1f) },
            { Pressed, new Color(0.1090246f, 0.4716981f, 0.7129337f, 1f) },
            { Selected, new Color(0.2559185f, 0.764151f, 0.7129337f, 1f) },
            { Danger, Hex("#FF7D77") },
        };

        public static Color Hex(string s) => ThemeDefinition.TryParseColor(s, out var c) ? c : Color.magenta;
    }

    internal class SpriteSpec {
        public string File;
        public Vector4 Border; // left, bottom, right, top (Unity order)
        public float PixelsPerUnit = 100f;
        public bool Pixelated;
        /// <summary>"sliced" (default when a border is set), "tiled" (repeat the middle/edges), or "simple" (stretch).</summary>
        public string Mode;
        public Sprite Loaded;

        public UnityEngine.UI.Image.Type ImageType {
            get {
                switch ((Mode ?? "").ToLowerInvariant()) {
                    case "tiled": return UnityEngine.UI.Image.Type.Tiled;
                    case "simple": return UnityEngine.UI.Image.Type.Simple;
                    case "sliced": return UnityEngine.UI.Image.Type.Sliced;
                    default: return Border != Vector4.zero ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
                }
            }
        }
    }

    /// <summary>A theme folder: theme.json plus optional images.</summary>
    internal class ThemeDefinition {
        public string Id;
        public string Name;
        public string Author = "";
        public string Version = "";
        public string Description = "";
        public string Folder;
        public bool IsDefault;
        public readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>();
        public readonly List<string> Fonts = new List<string>();
        public readonly List<string> HeadingFonts = new List<string>();
        public float HeadingMinSize = 34f;
        public readonly Dictionary<string, SpriteSpec> Sprites = new Dictionary<string, SpriteSpec>();
        /// <summary>Sprites per colour role (e.g. "titleBar"), applied to everything classified as that role.</summary>
        public readonly Dictionary<string, SpriteSpec> RoleSprites = new Dictionary<string, SpriteSpec>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Roles that may get a role sprite (backgrounds only; never icons or text).</summary>
        public static readonly HashSet<string> SpriteRoles = new HashSet<string> {
            Roles.Window, Roles.Panel, Roles.PanelLight, Roles.Surface, Roles.TitleBar, Roles.Button, Roles.ButtonOk,
            Roles.ButtonAlt, Roles.ButtonCancel, Roles.Control, Roles.Scrollbar, Roles.ScrollbarTrack, Roles.Accent, Roles.Overlay,
        };

        public static ThemeDefinition CreateDefault() => new ThemeDefinition {
            Id = "", Name = "KoboldKare (default)", Author = "Naelstrof", IsDefault = true,
            Description = "The game's original look.",
        };

        /// <summary>Theme colour for a role: explicit, else via fallback chain, else the game's own.</summary>
        public Color Resolve(string role) => TryGetExplicit(role, out var c) ? c : Roles.GameDefaults[role];

        /// <summary>True if the theme sets this role (directly or through its fallback chain).</summary>
        public bool TryGetExplicit(string role, out Color color) {
            for (int depth = 0; role != null && depth < 4; depth++) {
                if (Colors.TryGetValue(role, out color)) {
                    return true;
                }
                Roles.Fallback.TryGetValue(role, out role);
            }
            color = default;
            return false;
        }

        public static ThemeDefinition Load(string folder) =>
            Parse(File.ReadAllText(Path.Combine(folder, "theme.json")), Path.GetFileName(folder), folder);

        /// <summary>Parse theme.json text (also used to validate a theme before importing it).</summary>
        public static ThemeDefinition Parse(string json, string id, string folder) {
            var theme = new ThemeDefinition { Id = id, Folder = folder };
            theme.Name = theme.Id;
            var root = JSON.Parse(json);
            if (root == null) {
                throw new FormatException("theme.json isn't valid JSON");
            }
            theme.Name = Str(root["name"], theme.Id);
            theme.Author = Str(root["author"], "");
            theme.Version = Str(root["version"], "");
            theme.Description = Str(root["description"], "");

            var colors = root["colors"];
            if (colors != null) {
                foreach (var role in Roles.All) {
                    var value = colors[role];
                    if (value == null || string.IsNullOrEmpty(value.Value) && value.Count == 0) {
                        continue;
                    }
                    if (TryParseColor(value, out var c)) {
                        theme.Colors[role] = c;
                    } else {
                        theme.Warnings.Add($"colors.{role}: can't read '{value}'");
                    }
                }
            }

            var fonts = root["fonts"];
            if (fonts != null) {
                AddNames(fonts["body"], theme.Fonts);
                AddNames(fonts["headings"], theme.HeadingFonts);
                if (fonts["headingMinSize"] != null && !string.IsNullOrEmpty(fonts["headingMinSize"].Value)) {
                    theme.HeadingMinSize = fonts["headingMinSize"].AsFloat;
                }
            }

            ReadSprites(theme, root["sprites"], "sprites", theme.Sprites, null);
            ReadSprites(theme, root["roleSprites"], "roleSprites", theme.RoleSprites, SpriteRoles);
            return theme;
        }

        private static void ReadSprites(ThemeDefinition theme, JSONNode section, string label,
                                        Dictionary<string, SpriteSpec> into, HashSet<string> allowedKeys) {
            if (section == null) {
                return;
            }
            foreach (string key in KeysOf(section)) {
                if (allowedKeys != null && !allowedKeys.Contains(key)) {
                    theme.Warnings.Add($"{label}.{key}: not a background role (allowed: {string.Join(", ", allowedKeys)})");
                    continue;
                }
                var node = section[key];
                var spec = new SpriteSpec();
                if (node.IsString) {
                    spec.File = node.Value;
                } else {
                    spec.File = Str(node["file"], null);
                    var b = node["border"];
                    if (b != null && b.Count == 4) {
                        // JSON order is left, top, right, bottom (CSS-like); Unity wants left, bottom, right, top.
                        spec.Border = new Vector4(b[0].AsFloat, b[3].AsFloat, b[2].AsFloat, b[1].AsFloat);
                    }
                    if (node["pixelsPerUnit"] != null && !string.IsNullOrEmpty(node["pixelsPerUnit"].Value)) {
                        spec.PixelsPerUnit = node["pixelsPerUnit"].AsFloat;
                    }
                    spec.Pixelated = node["pixelated"] != null && node["pixelated"].AsBool;
                    spec.Mode = Str(node["mode"], null);
                }
                if (string.IsNullOrEmpty(spec.File)) {
                    theme.Warnings.Add($"{label}.{key}: no file");
                    continue;
                }
                into[key] = spec;
            }
        }

        private static IEnumerable<string> KeysOf(JSONNode node) {
            foreach (var pair in node) {
                yield return pair.Key;
            }
        }

        private static string Str(JSONNode node, string fallback) =>
            node == null || string.IsNullOrEmpty(node.Value) ? fallback : node.Value;

        private static void AddNames(JSONNode node, List<string> into) {
            if (node == null) {
                return;
            }
            if (node.IsArray) {
                for (int i = 0; i < node.Count; i++) {
                    if (!string.IsNullOrEmpty(node[i].Value)) {
                        into.Add(node[i].Value);
                    }
                }
            } else if (!string.IsNullOrEmpty(node.Value)) {
                into.Add(node.Value);
            }
        }

        public static bool TryParseColor(JSONNode node, out Color color) {
            if (node.IsArray && (node.Count == 3 || node.Count == 4)) {
                float r = node[0].AsFloat, g = node[1].AsFloat, b = node[2].AsFloat;
                float a = node.Count == 4 ? node[3].AsFloat : 1f;
                bool bytes = r > 1f || g > 1f || b > 1f;
                color = bytes ? new Color(r / 255f, g / 255f, b / 255f, a > 1f ? a / 255f : a) : new Color(r, g, b, a);
                return true;
            }
            return TryParseColor(node.Value, out color);
        }

        /// <summary>#RGB, #RRGGBB or #RRGGBBAA. (UnityEngine.ColorUtility is stripped in this game.)</summary>
        public static bool TryParseColor(string s, out Color color) {
            color = default;
            if (string.IsNullOrEmpty(s)) {
                return false;
            }
            s = s.Trim().TrimStart('#');
            if (s.Length == 3) {
                s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            }
            if (s.Length != 6 && s.Length != 8) {
                return false;
            }
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) {
                return false;
            }
            if (s.Length == 6) {
                v = (v << 8) | 0xFF;
            }
            color = new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
            return true;
        }
    }
}
