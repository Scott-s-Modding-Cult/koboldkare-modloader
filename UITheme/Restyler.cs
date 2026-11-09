using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UITheme {
    /// <summary>
    /// Restyles KoboldKare's UI at runtime. Every Image/text is snapshotted the first time it's seen
    /// (original colour, sprite, font) and classified into a role by that ORIGINAL look, so themes can
    /// be switched any number of times and the default theme restores everything exactly.
    ///
    /// Text and icons only change when they sit on a themed background ("text follows background"):
    /// HUD text over the 3D world keeps its colour, so light themes stay readable.
    /// </summary>
    internal static class Restyler {
        private class Snap {
            public Graphic Graphic;
            public Color Color;
            public Sprite Sprite;
            public Image.Type Type;
            public TMP_FontAsset Font;
            public bool Classified;
            public string Role;
        }

        private static readonly Dictionary<Graphic, Snap> Snaps = new Dictionary<Graphic, Snap>();
        private static readonly Dictionary<Selectable, ColorBlock> Blocks = new Dictionary<Selectable, ColorBlock>();
        private static ThemeDefinition active = ThemeDefinition.CreateDefault();
        private static float nextScan;
        private static bool scanRequested;

        // Backgrounds that make text/icons on them follow the theme.
        private static readonly HashSet<string> BackgroundRoles = new HashSet<string> {
            Roles.Overlay, Roles.Window, Roles.Panel, Roles.PanelLight, Roles.Surface, Roles.TitleBar, Roles.Button,
            Roles.ButtonOk, Roles.ButtonAlt, Roles.ButtonCancel, Roles.Control, Roles.Accent,
        };

        // Reference alpha of each role in the game's own UI: a theme colour's alpha is scaled by
        // (original alpha / reference) so e.g. a 39% and an 80% black panel keep their relative strength.
        private static readonly Dictionary<string, float> RefAlpha = new Dictionary<string, float> {
            { Roles.Overlay, 0.9f }, { Roles.Window, 0.8f }, { Roles.Panel, 0.8f }, { Roles.PanelLight, 0.39f },
            { Roles.Surface, 0.99f }, { Roles.Button, 0.94f }, { Roles.ButtonOk, 0.3f }, { Roles.ButtonAlt, 0.55f },
            { Roles.ButtonCancel, 0.3f }, { Roles.ScrollbarTrack, 0.11f },
        };

        /// <summary>Scan for new UI soon (next frame), e.g. after a menu opens.</summary>
        public static void RequestScan() => scanRequested = true;

        public static void Tick() {
            if (scanRequested || Time.unscaledTime >= nextScan) {
                scanRequested = false;
                nextScan = Time.unscaledTime + UIThemePlugin.ScanInterval.Value;
                Scan(applyAll: false);
            }
        }

        /// <summary>Switch to <paramref name="theme"/> and restyle everything already seen.</summary>
        public static void Apply(ThemeDefinition theme) {
            active = theme ?? ThemeDefinition.CreateDefault();
            Scan(applyAll: true);
        }

        // ------------------------------------------------------------------ scanning

        private static void Scan(bool applyAll) {
            Prune();
            var fresh = new List<Snap>();
            foreach (var obj in Resources.FindObjectsOfTypeAll(typeof(Graphic))) {
                var g = obj as Graphic;
                if (g == null || !g.gameObject.scene.isLoaded || IsExcluded(g.transform)) {
                    continue; // prefab assets (scene not loaded), ModKit's own panel (styled by ModKit itself)
                }
                if (!Snaps.ContainsKey(g)) {
                    fresh.Add(GetSnap(g));
                }
            }
            var targets = applyAll ? Snaps.Values.ToList() : fresh;
            if (targets.Count == 0) {
                return;
            }
            // Backgrounds first, then the text/icons that look them up.
            foreach (var s in targets.Where(s => !(s.Graphic is TMP_Text))) {
                ApplyGraphic(s);
            }
            foreach (var s in targets.Where(s => s.Graphic is TMP_Text)) {
                ApplyGraphic(s);
            }
            foreach (var obj in Resources.FindObjectsOfTypeAll(typeof(Selectable))) {
                var sel = obj as Selectable;
                if (sel == null || !sel.gameObject.scene.isLoaded || IsExcluded(sel.transform)) {
                    continue;
                }
                if (applyAll || !Blocks.ContainsKey(sel)) {
                    ApplySelectable(sel);
                }
            }
        }

        private static bool IsExcluded(Transform t) => t.root.name == "ModKit_Panel";

        private static void Prune() {
            foreach (var dead in Snaps.Keys.Where(k => k == null).ToList()) {
                Snaps.Remove(dead);
            }
            foreach (var dead in Blocks.Keys.Where(k => k == null).ToList()) {
                Blocks.Remove(dead);
            }
        }

        private static Snap GetSnap(Graphic g) {
            if (Snaps.TryGetValue(g, out var s)) {
                return s;
            }
            s = new Snap { Graphic = g, Color = g.color };
            if (g is Image img) {
                s.Sprite = img.sprite;
                s.Type = img.type;
            }
            if (g is TMP_Text t) {
                s.Font = t.font;
            }
            Snaps[g] = s;
            return s;
        }

        private static string RoleOf(Snap s) {
            if (!s.Classified) {
                s.Classified = true;
                s.Role = s.Graphic is TMP_Text ? ClassifyText(s) : ClassifyImage(s);
            }
            return s.Role;
        }

        // ------------------------------------------------------------------ applying

        private static void ApplyGraphic(Snap s) {
            var g = s.Graphic;
            if (g == null) {
                return;
            }
            // Always start from the original.
            g.color = s.Color;
            if (g is Image img) {
                img.sprite = s.Sprite;
                img.type = s.Type;
            }
            if (g is TMP_Text text && s.Font != null) {
                text.font = s.Font;
            }
            if (active.IsDefault) {
                return;
            }

            string role = RoleOf(s);
            if (g is Image image) {
                // Role sprites win (e.g. every title bar gets the cloud pattern), then swaps by original
                // sprite name (icons too, if a theme really wants to).
                SpriteSpec spec = null;
                if (role != null && active.RoleSprites.TryGetValue(role, out var roleSpec) && roleSpec.Loaded != null) {
                    spec = roleSpec;
                } else if (s.Sprite != null && active.Sprites.TryGetValue(s.Sprite.name, out var nameSpec) && nameSpec.Loaded != null) {
                    spec = nameSpec;
                }
                if (spec != null) {
                    image.sprite = spec.Loaded;
                    // Filled images are progress/energy bars: keep them Filled or they stop working.
                    if (s.Type != Image.Type.Filled) {
                        image.type = spec.ImageType;
                    }
                }
            }
            if (role == null) {
                return;
            }
            var color = active.Resolve(role);
            float reference = RefAlpha.TryGetValue(role, out var r) ? r : 1f;
            color.a = Mathf.Clamp01(color.a * s.Color.a / reference);
            g.color = color;

            if (g is TMP_Text tmp && role != Roles.Icon) {
                var font = tmp.fontSize >= active.HeadingMinSize || IsHeadingName(tmp.name)
                    ? ThemeLibrary.CurrentHeadingFont
                    : ThemeLibrary.CurrentFont;
                if (font != null) {
                    tmp.font = font;
                }
            }
        }

        private static void ApplySelectable(Selectable sel) {
            if (!Blocks.TryGetValue(sel, out var original)) {
                original = sel.colors;
                Blocks[sel] = original;
            }
            var block = original;
            if (!active.IsDefault && sel.targetGraphic != null && Snaps.TryGetValue(sel.targetGraphic, out var snap) && RoleOf(snap) != null) {
                block.highlightedColor = active.Resolve(Roles.Hover);
                block.pressedColor = active.Resolve(Roles.Pressed);
                block.selectedColor = active.Resolve(Roles.Selected);
            }
            sel.colors = block;
        }

        private static bool IsHeadingName(string name) =>
            name.IndexOf("Title", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Header", System.StringComparison.OrdinalIgnoreCase) >= 0;

        // ------------------------------------------------------------------ classification

        private static string ClassifyImage(Snap s) {
            var c = s.Color;
            if (c.a < 0.02f) {
                return null; // invisible raycast blockers
            }
            string sprite = s.Sprite != null ? s.Sprite.name : null;
            string name = s.Graphic.name;

            if (sprite != null && sprite.StartsWith("ico_")) {
                return BackgroundBehind(s.Graphic.transform) != null ? Roles.Icon : null;
            }
            if (Near(c, 0f, 0f, 0f)) {
                if (sprite == null) {
                    return name.IndexOf("Overlay", System.StringComparison.OrdinalIgnoreCase) >= 0 ? Roles.Overlay
                        : c.a >= 0.7f ? Roles.Window : Roles.Panel;
                }
                return Roles.Panel;
            }
            if (Near(c, 0x38, 0x44, 0x51) || Near(c, 0x87, 0x95, 0xE5)) {
                return Roles.Surface;
            }
            if (Near(c, 0x37, 0x8F, 0xFA)) {
                return name.IndexOf("Scrollbar", System.StringComparison.OrdinalIgnoreCase) >= 0 ? Roles.Scrollbar : Roles.TitleBar;
            }
            if (Near(c, 0.248f, 0.376f, 0.532f)) {
                return Roles.Button;
            }
            if (Near(c, 0x77, 0xB4, 0xFF)) {
                return c.a <= 0.4f ? Roles.ButtonOk : c.a <= 0.75f ? Roles.ButtonAlt : Roles.Accent;
            }
            if (Near(c, 0xFF, 0x7D, 0x77)) {
                return Roles.ButtonCancel;
            }
            if (Near(c, 0x77, 0x8E, 0xFF)) {
                return Roles.ScrollbarTrack;
            }
            if (Near(c, 0xA0, 0xE4, 0xFF) || Near(c, 0.289f, 0.93f, 0.972f)) {
                return Roles.Accent;
            }
            if (Near(c, 1f, 1f, 1f) || Near(c, 0xF5, 0xF5, 0xF5)) {
                if (sprite == "Checkmark" || sprite == "DropdownArrow" || sprite == "Arrow") {
                    return BackgroundBehind(s.Graphic.transform) != null ? Roles.Icon : null;
                }
                bool builtIn = sprite == null || sprite == "UISprite" || sprite == "Background" ||
                               sprite == "InputFieldBackground" || sprite == "UIMask" || sprite == "Knob";
                if (builtIn) {
                    return c.a >= 0.9f ? Roles.Control : c.a >= 0.2f ? Roles.PanelLight : null;
                }
            }
            return null; // artwork, portraits, item pictures: leave alone
        }

        private static string ClassifyText(Snap s) {
            string background = BackgroundBehind(s.Graphic.transform);
            if (background == null) {
                return null; // HUD / world text: keep readable against the 3D scene
            }
            var c = s.Color;
            Color.RGBToHSV(c, out _, out float sat, out float val);
            float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            if (c.r > 0.9f && c.g > 0.85f && c.b < 0.7f) {
                return Roles.HighlightText;
            }
            if (lum > 0.72f && sat < 0.3f) {
                switch (background) {
                    case Roles.TitleBar: return Roles.TitleText;
                    case Roles.Control: return Roles.FieldText;
                    case Roles.Button: case Roles.ButtonOk: case Roles.ButtonAlt: case Roles.ButtonCancel: return Roles.ButtonText;
                    default: return Roles.Text;
                }
            }
            if (val < 0.4f && sat < 0.25f) {
                return Roles.FieldText;
            }
            return null; // specially coloured text (pinks, blues...) keeps its colour
        }

        /// <summary>Role of the nearest themed background image among the ancestors (and their direct children).</summary>
        private static string BackgroundBehind(Transform t) {
            var current = t;
            for (int depth = 0; depth < 8 && current != null; depth++) {
                var image = current.GetComponent<Image>();
                if (image != null && image.transform != t) {
                    string role = RoleOf(GetSnap(image));
                    if (role != null && BackgroundRoles.Contains(role)) {
                        return role;
                    }
                }
                current = current.parent;
            }
            return null;
        }

        private static bool Near(Color c, int r, int g, int b) => Near(c, r / 255f, g / 255f, b / 255f);

        private static bool Near(Color c, float r, float g, float b) =>
            Mathf.Abs(c.r - r) < 0.04f && Mathf.Abs(c.g - g) < 0.04f && Mathf.Abs(c.b - b) < 0.04f;
    }
}
