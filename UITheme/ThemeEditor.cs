using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KoboldKare.ModKit;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static KoboldKare.ModKit.UiFactory;

namespace UITheme {
    /// <summary>
    /// In-game theme editor: a window docked on the right where every colour role gets a colour (hue,
    /// saturation, brightness, opacity, or a hex code). The game UI behind it recolours live. Themes made
    /// here are colours only, so they keep the game's own frame shapes, and are saved as
    /// BepInEx/themes/&lt;Name&gt;/theme.json with "custom": true.
    ///
    /// The window has fixed colours of its own (Restyler skips it), so it stays readable whatever you pick.
    /// It works from the Options menu (main menu or pause) and from the F8 panel in a level.
    /// </summary>
    internal class ThemeEditor : MonoBehaviour {
        internal const string RootName = "UITheme_Editor";

        private static ThemeEditor instance;
        public static bool IsOpen => instance != null;

        // ------------------------------------------------------------------ roles shown in the editor

        private class RoleInfo {
            public string Role, Label, Hint;
            public RoleInfo(string role, string label, string hint) { Role = role; Label = label; Hint = hint; }
        }

        private static readonly (string Group, RoleInfo[] Roles)[] Groups = {
            ("Backgrounds", new[] {
                new RoleInfo(Roles.Surface, "Menu background", "The main slate behind menus like Options."),
                new RoleInfo(Roles.Window, "Popup windows", "Bodies of popups and dialogs."),
                new RoleInfo(Roles.Panel, "Panels and lists", "Rounded panels, lists and scroll views."),
                new RoleInfo(Roles.PanelLight, "Light panels", "Faint panels such as stat blocks."),
                new RoleInfo(Roles.TitleBar, "Title bars", "Headers and top bars of windows."),
                new RoleInfo(Roles.Overlay, "Screen dimmer", "Darkens the screen behind popups."),
            }),
            ("Buttons", new[] {
                new RoleInfo(Roles.Button, "Buttons", "Main menu buttons."),
                new RoleInfo(Roles.ButtonOk, "Confirm buttons", "Okay / confirm style buttons."),
                new RoleInfo(Roles.ButtonAlt, "Secondary buttons", "Less important buttons."),
                new RoleInfo(Roles.ButtonCancel, "Cancel buttons", "Cancel and destructive buttons."),
                new RoleInfo(Roles.Hover, "Hover tint", "Tint while the mouse is over a button."),
                new RoleInfo(Roles.Pressed, "Pressed tint", "Tint while a button is held down."),
                new RoleInfo(Roles.Selected, "Controller selection", "Tint of the item a controller has selected."),
            }),
            ("Controls", new[] {
                new RoleInfo(Roles.Control, "Dropdowns and inputs", "Dropdowns, text boxes, toggles, slider tracks."),
                new RoleInfo(Roles.Accent, "Accent", "Slider handles and fills, highlighted boxes."),
                new RoleInfo(Roles.Scrollbar, "Scrollbar", "The draggable part of scrollbars."),
                new RoleInfo(Roles.ScrollbarTrack, "Scrollbar track", "The groove scrollbars run in."),
            }),
            ("Text and icons", new[] {
                new RoleInfo(Roles.Text, "Text", "Normal text on menus."),
                new RoleInfo(Roles.TitleText, "Title text", "Text on title bars."),
                new RoleInfo(Roles.ButtonText, "Button text", "Text on buttons."),
                new RoleInfo(Roles.FieldText, "Text in inputs", "Text inside dropdowns and text boxes."),
                new RoleInfo(Roles.HighlightText, "Highlighted values", "Yellow amounts and values."),
                new RoleInfo(Roles.Danger, "Warnings", "Error and warning text."),
                new RoleInfo(Roles.Icon, "Icons", "Tint of the game's icons on menus."),
            }),
        };

        // ------------------------------------------------------------------ fixed editor colours

        private static readonly Color BodyColor = new Color(0.09f, 0.10f, 0.14f, 0.97f);
        private static readonly Color BarColor = new Color(0.18f, 0.25f, 0.38f, 1f);
        private static readonly Color RowColor = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color RowSelected = new Color(0.35f, 0.62f, 1f, 0.35f);
        private static readonly Color TextColor = new Color(0.94f, 0.94f, 1f, 1f);
        private static readonly Color DimColor = new Color(0.70f, 0.72f, 0.82f, 1f);
        private static readonly Color InputColor = new Color(1f, 1f, 1f, 0.92f);
        private static readonly Color InputText = new Color(0.12f, 0.12f, 0.16f, 1f);
        private static readonly Color SaveColor = new Color(0.22f, 0.55f, 0.34f, 1f);
        private static readonly Color CancelColor = new Color(0.32f, 0.33f, 0.40f, 1f);
        private static readonly Color DeleteColor = new Color(0.62f, 0.22f, 0.22f, 1f);

        private const float WinW = 700f, WinH = 1000f;

        // ------------------------------------------------------------------ state

        private ThemeDefinition before;     // the theme in use when the editor opened (restored on Cancel)
        private ThemeDefinition editing;    // the custom theme being changed, or null for a new one
        private readonly Dictionary<string, Color> colors = new Dictionary<string, Color>();
        private string themeName;
        private string selected;
        private float hue, sat, val, alpha;
        private bool previewDirty;
        private float nextPreview;
        private bool tookControls;
        private bool confirmDelete;

        private readonly Dictionary<string, (Image Row, Image Swatch, TextMeshProUGUI Hex)> rows =
            new Dictionary<string, (Image, Image, TextMeshProUGUI)>();
        private TextMeshProUGUI roleTitle, roleHint, status;
        private Image bigSwatch;
        private TMP_InputField hexInput;
        private Slider hueSlider, satSlider, valSlider, alphaSlider;
        private TextMeshProUGUI hueValue, satValue, valValue, alphaValue;
        private Texture2D satTex, valTex, alphaTex;
        private PanelButton deleteButton;

        // ------------------------------------------------------------------ opening

        /// <summary>Start a new theme from the colours of the theme in use.</summary>
        public static void Create() => Open(null);

        /// <summary>Change a theme made with the editor.</summary>
        public static void Edit(ThemeDefinition theme) {
            if (!ThemeLibrary.IsEditable(theme)) {
                UIThemePlugin.Log.LogWarning($"'{theme?.Name}' wasn't made with the theme editor, so it can't be edited in game.");
                return;
            }
            Open(theme);
        }

        private static void Open(ThemeDefinition theme) {
            if (instance != null) {
                Destroy(instance.gameObject);
            }
            if (ModPanel.IsOpen) {
                ModPanel.Close();
            }
            Theme.Load();
            var go = new GameObject(RootName);
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ThemeEditor>();
            instance.Begin(theme);
        }

        private void Begin(ThemeDefinition theme) {
            before = ThemeLibrary.Current;
            editing = theme;
            var source = theme ?? before;
            foreach (var role in Roles.All) {
                colors[role] = source.Resolve(role);
            }
            themeName = theme != null ? theme.Name : UniqueName("My Theme");

            // In a level (from the F8 panel) the editor takes the cursor; in a menu the menu already has it.
            if (ModKit.InLevel && MainMenu.GetCurrentMode() == MainMenu.MainMenuMode.None && !Pauser.GetPaused()) {
                GameManager.SetControlsActive(false);
                OrbitCamera.SetTracking(false);
                tookControls = true;
            }
            Build();
            Select(Roles.Surface);
            previewDirty = true;
        }

        private static string UniqueName(string baseName) {
            string name = baseName;
            for (int i = 2; ThemeLibrary.Find(name) != null || Directory.Exists(Path.Combine(ThemeLibrary.UserThemesFolder, FolderName(name))); i++) {
                name = baseName + " " + i;
            }
            return name;
        }

        private static string FolderName(string name) {
            var chars = (name ?? "").Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray();
            string folder = new string(chars).Trim().TrimEnd('.');
            return folder.Length > 0 ? folder : "Custom Theme";
        }

        // ------------------------------------------------------------------ building the window

        private void Build() {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 600; // above the game's menus and the ModKit panel
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            gameObject.AddComponent<GraphicRaycaster>();

            // Catches clicks so the menu behind can be seen (it shows the preview) but not used.
            var blocker = NewRect("Blocker", transform);
            Stretch(blocker, 0f, 0f, 0f, 0f);
            AddImage(blocker, null, new Color(0f, 0f, 0f, 0.001f), sliced: false);

            var win = NewRect("Window", transform);
            win.anchorMin = win.anchorMax = new Vector2(1f, 0.5f);
            win.pivot = new Vector2(1f, 0.5f);
            win.sizeDelta = new Vector2(WinW, WinH);
            win.anchoredPosition = new Vector2(-28f, 0f);
            AddImage(win, Theme.PanelSprite, BodyColor);

            var bar = NewRect("TitleBar", win);
            Place(bar, 0f, 0f, WinW, 70f);
            AddImage(bar, Theme.PanelSprite, BarColor);
            AddIcon(bar, Theme.FirstAvailable("ico_star", "ico_gear"), 16f, 12f, 46f).color = TextColor;
            Label(bar, editing != null ? "Edit theme" : "Create theme", 76f, 0f, 480f, 70f, 34f, TextColor);
            var close = MakeButton(bar, "Close", WinW - 62f, 10f, 50f, 50f, Theme.PanelSprite, new Color(1f, 1f, 1f, 0f),
                Theme.FirstAvailable("ico_x-out", "ico_stop"), null, 0f, Cancel);
            close.Icon.color = TextColor;

            Label(win, "Name", 24f, 84f, 110f, 52f, 24f, TextColor);
            var nameInput = MakeInput(win, 140f, 84f, WinW - 164f, 52f, themeName, 24f, 40);
            nameInput.onValueChanged.AddListener(v => themeName = v);

            BuildRoleList(win, 16f, 150f, WinW - 32f, 466f);
            BuildPicker(win, 630f);

            Button(win, "Save", Theme.FirstAvailable("ico_save", "ico_checkmark"), 24f, 920f, 206f, SaveColor, Save);
            Button(win, "Cancel", Theme.FirstAvailable("ico_x-out", "ico_stop"), 246f, 920f, 206f, CancelColor, Cancel);
            if (editing != null) {
                deleteButton = Button(win, "Delete", Theme.FirstAvailable("ico_trashcan", "ico_x-out"), 468f, 920f, 208f, DeleteColor, Delete);
            }
            status = Label(win, "", 24f, 982f, WinW - 48f, 0f, 18f, DimColor);
        }

        private void BuildRoleList(RectTransform parent, float x, float y, float w, float h) {
            var scroll = NewRect("Roles", parent);
            Place(scroll, x, y, w, h);
            AddImage(scroll, Theme.PanelSprite, new Color(0f, 0f, 0f, 0.25f));
            var viewport = NewRect("Viewport", scroll);
            Stretch(viewport, 6f, 6f, 6f, 6f);
            AddImage(viewport, null, new Color(0f, 0f, 0f, 0.001f), sliced: false);
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = NewRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            var sr = scroll.gameObject.AddComponent<ScrollRect>();
            sr.viewport = viewport;
            sr.content = content;
            sr.horizontal = false;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 40f;

            float rowW = w - 12f;
            float cy = 4f;
            foreach (var group in Groups) {
                Label(content, group.Group.ToUpperInvariant(), 10f, cy, rowW - 20f, 34f, 18f, DimColor);
                cy += 36f;
                foreach (var info in group.Roles) {
                    string role = info.Role;
                    var row = MakeButton(content, info.Label, 0f, cy, rowW, 48f, Theme.PanelSprite, RowColor, null, null, 0f, () => Select(role));
                    var back = NewRect("SwatchBack", row.Background.rectTransform);
                    Place(back, 10f, 7f, 34f, 34f);
                    CheckerBehind(back);
                    var sw = NewRect("Swatch", row.Background.rectTransform);
                    Place(sw, 10f, 7f, 34f, 34f);
                    var swatch = AddImage(sw, null, colors[role], sliced: false);
                    swatch.raycastTarget = false;
                    Label(row.Background.rectTransform, info.Label, 58f, 0f, rowW - 260f, 48f, 22f, TextColor);
                    var hex = Label(row.Background.rectTransform, "", rowW - 196f, 0f, 184f, 48f, 19f, DimColor, TextAlignmentOptions.MidlineRight);
                    rows[role] = (row.Background, swatch, hex);
                    cy += 52f;
                }
                cy += 6f;
            }
            content.sizeDelta = new Vector2(0f, cy + 4f);
        }

        private void BuildPicker(RectTransform win, float y) {
            roleTitle = Label(win, "", 24f, y, WinW - 48f, 36f, 28f, TextColor);
            roleHint = Label(win, "", 24f, y + 36f, WinW - 48f, 28f, 19f, DimColor);

            var back = NewRect("BigSwatchBack", win);
            Place(back, 24f, y + 74f, 128f, 128f);
            CheckerBehind(back);
            var big = NewRect("BigSwatch", win);
            Place(big, 24f, y + 74f, 128f, 128f);
            bigSwatch = AddImage(big, null, Color.white, sliced: false);

            hexInput = MakeInput(win, 24f, y + 212f, 128f, 44f, "", 18f, 9);
            hexInput.onEndEdit.AddListener(OnHexEdited);

            var hueTex = new Texture2D(256, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int i = 0; i < 256; i++) {
                hueTex.SetPixel(i, 0, Color.HSVToRGB(i / 255f, 1f, 1f));
            }
            hueTex.Apply();
            satTex = NewTrackTexture();
            valTex = NewTrackTexture();
            alphaTex = NewTrackTexture();

            float sx = 172f;
            hueSlider = MakeColorSlider(win, "Hue", sx, y + 74f, hueTex, out hueValue, v => { hue = v; FromSliders(); });
            satSlider = MakeColorSlider(win, "Saturation", sx, y + 128f, satTex, out satValue, v => { sat = v; FromSliders(); });
            valSlider = MakeColorSlider(win, "Brightness", sx, y + 182f, valTex, out valValue, v => { val = v; FromSliders(); });
            alphaSlider = MakeColorSlider(win, "Opacity", sx, y + 236f, alphaTex, out alphaValue, v => { alpha = v; FromSliders(); });
        }

        private static Texture2D NewTrackTexture() =>
            new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };

        private static Sprite SpriteOf(Texture2D tex) =>
            Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);

        private Slider MakeColorSlider(RectTransform parent, string label, float x, float y, Texture2D track,
                                       out TextMeshProUGUI valueText, Action<float> changed) {
            Label(parent, label, x, y, 150f, 44f, 21f, TextColor);
            valueText = Label(parent, "", WinW - 24f - 70f, y, 70f, 44f, 20f, DimColor, TextAlignmentOptions.MidlineRight);

            var root = NewRect(label + " Slider", parent);
            Place(root, x + 150f, y + 8f, WinW - 24f - 80f - (x + 150f), 28f);
            var checker = NewRect("Checker", root);
            Stretch(checker, 0f, 4f, 0f, 4f);
            CheckerBehind(checker);
            var background = NewRect("Track", root);
            Stretch(background, 0f, 4f, 0f, 4f);
            AddImage(background, SpriteOf(track), Color.white, sliced: false);
            var handleArea = NewRect("Handle Slide Area", root);
            Stretch(handleArea, 10f, 0f, 10f, 0f);
            var handle = NewRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(20f, 0f);
            var handleImage = AddImage(handle, Theme.KnobSprite ?? Theme.PanelSprite, Color.white, sliced: false);

            var slider = root.gameObject.AddComponent<Slider>();
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            slider.colors = GameColors();
            slider.onValueChanged.AddListener(v => changed(v));
            return slider;
        }

        /// <summary>Half white, half black behind a swatch, so see-through colours show as see-through.</summary>
        private static void CheckerBehind(RectTransform rect) {
            var left = NewRect("Light", rect);
            left.anchorMin = Vector2.zero;
            left.anchorMax = new Vector2(0.5f, 1f);
            left.offsetMin = left.offsetMax = Vector2.zero;
            AddImage(left, null, new Color(0.92f, 0.92f, 0.92f, 1f), sliced: false).raycastTarget = false;
            var right = NewRect("Dark", rect);
            right.anchorMin = new Vector2(0.5f, 0f);
            right.anchorMax = Vector2.one;
            right.offsetMin = right.offsetMax = Vector2.zero;
            AddImage(right, null, new Color(0.08f, 0.08f, 0.08f, 1f), sliced: false).raycastTarget = false;
        }

        private static TextMeshProUGUI Label(RectTransform parent, string text, float x, float y, float w, float h, float size,
                                             Color color, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft) {
            var t = AddText(parent, "Text", x, y, w, h, text, size, color, align);
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        private static PanelButton Button(RectTransform parent, string label, string icon, float x, float y, float w, Color color, Action onClick) {
            var b = MakeButton(parent, label, x, y, w, 60f, Theme.PanelSprite, color, icon, label, 26f, () => onClick());
            b.Label.color = TextColor;
            b.Icon.color = TextColor;
            return b;
        }

        private static TMP_InputField MakeInput(RectTransform parent, float x, float y, float w, float h, string text, float size, int limit) {
            var root = NewRect("Input", parent);
            Place(root, x, y, w, h);
            root.gameObject.SetActive(false); // wire everything up before TMP_InputField's OnEnable runs
            var bg = AddImage(root, Theme.PanelSprite, InputColor);
            var area = NewRect("Text Area", root);
            Stretch(area, 12f, 4f, 12f, 4f);
            area.gameObject.AddComponent<RectMask2D>();
            var label = AddText(area, "Text", 0f, 0f, 10f, 10f, "", size, InputText, TextAlignmentOptions.MidlineLeft);
            Stretch(label.rectTransform, 0f, 0f, 0f, 0f);
            label.enableWordWrapping = false;
            var input = root.gameObject.AddComponent<TMP_InputField>();
            input.textViewport = area;
            input.textComponent = label;
            input.targetGraphic = bg;
            input.fontAsset = label.font;
            input.pointSize = size;
            input.characterLimit = limit;
            input.caretColor = InputText;
            input.customCaretColor = true;
            input.selectionColor = new Color(0.35f, 0.62f, 1f, 0.45f);
            input.navigation = new Navigation { mode = Navigation.Mode.None };
            root.gameObject.SetActive(true);
            input.text = text;
            return input;
        }

        // ------------------------------------------------------------------ editing

        private void Select(string role) {
            selected = role;
            var info = Groups.SelectMany(g => g.Roles).First(r => r.Role == role);
            roleTitle.text = info.Label;
            roleHint.text = info.Hint;
            var c = colors[role];
            Color.RGBToHSV(c, out hue, out sat, out val);
            alpha = c.a;
            foreach (var pair in rows) {
                pair.Value.Row.color = pair.Key == role ? RowSelected : RowColor;
            }
            ShowSelected(updateSliders: true, updateHex: true);
        }

        private void FromSliders() {
            var c = Color.HSVToRGB(hue, sat, val);
            c.a = alpha;
            colors[selected] = c;
            ShowSelected(updateSliders: false, updateHex: true);
        }

        private void OnHexEdited(string text) {
            if (!ThemeDefinition.TryParseColor(text, out var c)) {
                hexInput.SetTextWithoutNotify(ThemeDefinition.ToHex(colors[selected]));
                return;
            }
            colors[selected] = c;
            Color.RGBToHSV(c, out hue, out sat, out val);
            alpha = c.a;
            ShowSelected(updateSliders: true, updateHex: true);
        }

        private void ShowSelected(bool updateSliders, bool updateHex) {
            var c = colors[selected];
            bigSwatch.color = c;
            var row = rows[selected];
            row.Swatch.color = c;
            row.Hex.text = ThemeDefinition.ToHex(c);
            if (updateHex) {
                hexInput.SetTextWithoutNotify(ThemeDefinition.ToHex(c));
            }
            if (updateSliders) {
                hueSlider.SetValueWithoutNotify(hue);
                satSlider.SetValueWithoutNotify(sat);
                valSlider.SetValueWithoutNotify(val);
                alphaSlider.SetValueWithoutNotify(alpha);
            }
            hueValue.text = Mathf.RoundToInt(hue * 360f) + "°";
            satValue.text = Mathf.RoundToInt(sat * 100f) + "%";
            valValue.text = Mathf.RoundToInt(val * 100f) + "%";
            alphaValue.text = Mathf.RoundToInt(alpha * 100f) + "%";
            for (int i = 0; i < 64; i++) {
                float t = i / 63f;
                satTex.SetPixel(i, 0, Color.HSVToRGB(hue, t, Mathf.Max(val, 0.15f)));
                valTex.SetPixel(i, 0, Color.HSVToRGB(hue, sat, t));
                var a = Color.HSVToRGB(hue, sat, val);
                a.a = t;
                alphaTex.SetPixel(i, 0, a);
            }
            satTex.Apply();
            valTex.Apply();
            alphaTex.Apply();
            foreach (var pair in rows) {
                pair.Value.Swatch.color = colors[pair.Key];
                pair.Value.Hex.text = ThemeDefinition.ToHex(colors[pair.Key]);
            }
            previewDirty = true;
            confirmDelete = false;
            if (deleteButton != null) {
                deleteButton.SetLabel("Delete");
            }
        }

        private ThemeDefinition PreviewTheme() {
            var theme = new ThemeDefinition { Id = "__editor", Name = themeName, Custom = true };
            foreach (var pair in colors) {
                theme.Colors[pair.Key] = pair.Value;
            }
            return theme;
        }

        private void Update() {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            // Restyling scans the whole UI, so keep it to a few times a second while a slider is dragged.
            if (previewDirty && Time.unscaledTime >= nextPreview) {
                previewDirty = false;
                nextPreview = Time.unscaledTime + 0.15f;
                try {
                    ThemeLibrary.Preview(PreviewTheme());
                } catch (Exception e) {
                    UIThemePlugin.Log.LogWarning("Theme preview failed: " + e.Message);
                }
            }
        }

        // ------------------------------------------------------------------ save / cancel / delete

        private void Save() {
            string name = string.IsNullOrWhiteSpace(themeName) ? "My Theme" : themeName.Trim();
            string folder;
            if (editing != null) {
                folder = editing.Folder; // keep the folder (the theme's id), so a rename never loses the selection
            } else {
                string baseFolder = FolderName(name);
                folder = Path.Combine(ThemeLibrary.UserThemesFolder, baseFolder);
                for (int i = 2; Directory.Exists(folder); i++) {
                    folder = Path.Combine(ThemeLibrary.UserThemesFolder, baseFolder + " " + i);
                }
            }
            try {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "theme.json"), ThemeDefinition.ToCustomJson(name, colors));
            } catch (Exception e) {
                status.text = "Couldn't save: " + e.Message;
                UIThemePlugin.Log.LogError("Couldn't save the theme: " + e);
                return;
            }
            UIThemePlugin.Log.LogInfo($"Saved theme '{name}' to {folder}");
            ThemeLibrary.Reload();
            var saved = ThemeLibrary.Find(Path.GetFileName(folder));
            Finish();
            ThemeLibrary.Select(saved ?? ThemeLibrary.Themes[0]);
        }

        private void Cancel() {
            Finish();
            ThemeLibrary.Select(ThemeLibrary.Find(before?.Id ?? "") ?? ThemeLibrary.Themes[0]);
        }

        private void Delete() {
            if (editing == null) {
                return;
            }
            if (!confirmDelete) {
                confirmDelete = true;
                deleteButton.SetLabel("Really delete?");
                status.text = "Click again to delete this theme for good.";
                return;
            }
            try {
                Directory.Delete(editing.Folder, true);
            } catch (Exception e) {
                status.text = "Couldn't delete: " + e.Message;
                return;
            }
            UIThemePlugin.Log.LogInfo($"Deleted theme '{editing.Name}' ({editing.Folder})");
            string beforeId = before?.Id ?? "";
            ThemeLibrary.Reload();
            Finish();
            ThemeLibrary.Select(ThemeLibrary.Find(beforeId) ?? ThemeLibrary.Themes[0]);
        }

        private void Finish() {
            if (tookControls && ModKit.InLevel && MainMenu.GetCurrentMode() == MainMenu.MainMenuMode.None && !Pauser.GetPaused()) {
                GameManager.SetControlsActive(true);
                OrbitCamera.SetTracking(true);
                Cursor.lockState = CursorLockMode.Locked;
            }
            tookControls = false;
            Destroy(gameObject);
        }

        private void OnDestroy() {
            if (instance == this) {
                instance = null;
            }
            if (tookControls) {
                try {
                    GameManager.SetControlsActive(true);
                } catch {
                    // GameManager may be gone during shutdown.
                }
            }
            foreach (var tex in new[] { satTex, valTex, alphaTex }) {
                if (tex != null) {
                    Destroy(tex);
                }
            }
        }
    }
}
