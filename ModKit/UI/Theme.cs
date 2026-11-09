using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace KoboldKare.ModKit {
    /// <summary>
    /// Colours for ModKit-built UI. Defaults are KoboldKare's own look (from its Button, ConfirmPopup,
    /// SliderPrefab and MultiSelectToggle prefabs). A theme mod (UITheme) can replace them at runtime
    /// with <see cref="ApplyPalette"/>; the mod panel rebuilds itself on <see cref="Changed"/>.
    /// </summary>
    public class ThemePalette {
        public Color Body = new Color(0f, 0f, 0f, 0.8f);                       // ConfirmPopup Container
        public Color Card = new Color(0f, 0f, 0f, 0.45f);
        public Color TitleBar = new Color(0.2175151f, 0.56002f, 0.9811321f, 1f); // ConfirmPopup TopPanel
        public Color TitleText = Color.white;
        public Color Button = new Color(0.24826668f, 0.3755295f, 0.532f, 0.9372549f); // Button.prefab
        public Color Text = new Color(0.9301124f, 0.92156863f, 1f, 1f);
        public Color Highlight = new Color(0.49f, 1f, 0.9435571f, 1f);         // shared hover tint
        public Color Pressed = new Color(0.1090246f, 0.4716981f, 0.7129337f, 1f);
        public Color Okay = new Color(0.4666667f, 0.7063446f, 1f, 0.29803923f);
        public Color Cancel = new Color(1f, 0.49056026f, 0.4666667f, 0.29803923f);
        public Color Danger = new Color(1f, 0.49056026f, 0.4666667f, 1f);
        public Color ToggleOn = new Color(0.28875935f, 0.9299057f, 0.9716981f, 0.35f);
        public Color Accent = new Color(0.49f, 1f, 0.9435571f, 1f);           // value labels, lines, slider fill
        public Color Icon = Color.white;

        public ThemePalette Clone() => (ThemePalette)MemberwiseClone();
    }

    /// <summary>A theme sprite for a UI role (e.g. "titleBar"), and how to draw it.</summary>
    public class RoleSprite {
        public Sprite Sprite;
        public UnityEngine.UI.Image.Type Type = UnityEngine.UI.Image.Type.Sliced;
    }

    public static class Theme {
        private static ThemePalette palette = new ThemePalette();
        private static TMP_FontAsset fontOverride;
        private static TMP_FontAsset headingFontOverride;
        private static readonly Dictionary<string, Sprite> SpriteOverrides = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, RoleSprite> RoleSprites = new Dictionary<string, RoleSprite>();

        /// <summary>
        /// Theme sprite for a role ("window", "panel", "titleBar", "button", "buttonOk", "buttonCancel",
        /// "control"...), or null to use the default sprite.
        /// </summary>
        public static RoleSprite GetRoleSprite(string role) =>
            role != null && RoleSprites.TryGetValue(role, out var rs) && rs?.Sprite != null ? rs : null;

        /// <summary>Raised after a theme mod changes the palette, fonts or sprites.</summary>
        public static event Action Changed;

        /// <summary>A copy of the game's original look.</summary>
        public static ThemePalette Default => new ThemePalette();

        public static ThemePalette Palette => palette;

        public static Color Body => palette.Body;
        public static Color Card => palette.Card;
        public static Color TitleBar => palette.TitleBar;
        public static Color TitleText => palette.TitleText;
        public static Color Button => palette.Button;
        public static Color Text => palette.Text;
        public static Color TextDim => new Color(palette.Text.r, palette.Text.g, palette.Text.b, palette.Text.a * 0.65f);
        public static Color Highlight => palette.Highlight;
        public static Color Pressed => palette.Pressed;
        public static Color Okay => palette.Okay;
        public static Color Cancel => palette.Cancel;
        public static Color CancelSolid => palette.Danger;
        public static Color ToggleOn => palette.ToggleOn;
        public static Color Accent => palette.Accent;
        public static Color IconTint => palette.Icon;

        /// <summary>Rich-text colour tags following the theme, e.g. $"{Theme.Cyan}12</color>".</summary>
        public static string Cyan => "<color=#" + Hex(palette.Accent) + ">";
        public static string Red => "<color=#" + Hex(palette.Danger) + ">";

        /// <summary>RRGGBB hex for rich-text tags. (UnityEngine.ColorUtility is stripped in this game.)</summary>
        public static string Hex(Color c) {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
        }

        /// <summary>Replace ModKit's colours (and optionally fonts / sprites, by game sprite name).</summary>
        public static void ApplyPalette(ThemePalette newPalette, TMP_FontAsset font = null, TMP_FontAsset headingFont = null,
                                        IDictionary<string, Sprite> sprites = null, IDictionary<string, RoleSprite> roleSprites = null) {
            palette = newPalette?.Clone() ?? new ThemePalette();
            fontOverride = font;
            headingFontOverride = headingFont;
            SpriteOverrides.Clear();
            if (sprites != null) {
                foreach (var pair in sprites) {
                    SpriteOverrides[pair.Key] = pair.Value;
                }
            }
            RoleSprites.Clear();
            if (roleSprites != null) {
                foreach (var pair in roleSprites) {
                    RoleSprites[pair.Key] = pair.Value;
                }
            }
            loaded = false; // re-resolve sprites with overrides
            Load();
            try {
                Changed?.Invoke();
            } catch (Exception e) {
                ModKit.Log.LogError("Theme.Changed handler threw: " + e);
            }
        }

        /// <summary>Back to the game's original look.</summary>
        public static void ResetPalette() => ApplyPalette(null);

        public static TMP_FontAsset Font => fontOverride != null ? fontOverride : gameFont;
        public static TMP_FontAsset HeadingFont => headingFontOverride != null ? headingFontOverride : Font;
        /// <summary>The game's own UI font (NotoSans), regardless of theme.</summary>
        public static TMP_FontAsset GameFont => gameFont;
        /// <summary>Unity's built-in rounded "UISprite" (9-sliced), used for panels and simple buttons.</summary>
        public static Sprite PanelSprite { get; private set; }
        /// <summary>The game's button background ("Substance_graph_CustomBackground").</summary>
        public static Sprite ButtonSprite { get; private set; }
        /// <summary>Unity's built-in slider "Knob".</summary>
        public static Sprite KnobSprite { get; private set; }

        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();
        private static TMP_FontAsset gameFont;
        private static bool loaded;

        /// <summary>Indexes the game's loaded sprites and font. Called automatically by the panel.</summary>
        public static void Load(bool force = false) {
            if (loaded && gameFont != null && !force) {
                return;
            }
            loaded = true;
            Sprites.Clear();
            foreach (var obj in Resources.FindObjectsOfTypeAll(typeof(Sprite))) {
                if (obj is Sprite sprite && !Sprites.ContainsKey(sprite.name)) {
                    Sprites[sprite.name] = sprite;
                }
            }
            PanelSprite = Icon("UISprite");
            ButtonSprite = Icon("Substance_graph_CustomBackground") ?? PanelSprite;
            KnobSprite = Icon("Knob");

            if (gameFont == null) {
                TMP_FontAsset fallback = null;
                foreach (var obj in Resources.FindObjectsOfTypeAll(typeof(TMP_FontAsset))) {
                    if (!(obj is TMP_FontAsset font)) {
                        continue;
                    }
                    if (font.name == "NotoSans") {
                        gameFont = font;
                        break;
                    }
                    if (fallback == null && font.name.StartsWith("Noto")) {
                        fallback = font;
                    }
                }
                gameFont = gameFont ?? fallback;
            }
        }

        /// <summary>
        /// A game sprite by name (e.g. "ico_star"), honouring theme sprite overrides; null if it isn't in memory.
        /// </summary>
        public static Sprite Icon(string name) {
            if (name == null) {
                return null;
            }
            if (SpriteOverrides.TryGetValue(name, out var over) && over != null) {
                return over;
            }
            return Sprites.TryGetValue(name, out var s) ? s : null;
        }

        /// <summary>First of these sprite names that is loaded (not every game icon is in memory at all times).</summary>
        public static string FirstAvailable(params string[] names) {
            Load();
            foreach (var n in names) {
                if (Icon(n) != null) {
                    return n;
                }
            }
            return names.Length > 0 ? names[0] : null;
        }
    }
}
