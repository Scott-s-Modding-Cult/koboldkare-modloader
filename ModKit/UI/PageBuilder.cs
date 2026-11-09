using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static KoboldKare.ModKit.UiFactory;

namespace KoboldKare.ModKit {
    public enum ButtonStyle {
        /// <summary>The game's main button look (Button.prefab).</summary>
        Primary,
        /// <summary>Translucent blue, like ConfirmPopup's "Okay".</summary>
        Okay,
        /// <summary>Translucent red, like ConfirmPopup's "Cancel".</summary>
        Cancel,
        /// <summary>Dark card, for low-emphasis actions.</summary>
        Subtle,
    }

    /// <summary>Title + body text block for live status, made by <see cref="PageBuilder.Card"/>.</summary>
    public class StatusCard {
        public TextMeshProUGUI Title;
        public TextMeshProUGUI Body;
    }

    public struct ButtonSpec {
        public string Label;
        public string Icon;
        public Action OnClick;
        public ButtonStyle Style;

        public ButtonSpec(string label, string icon, Action onClick, ButtonStyle style = ButtonStyle.Okay) {
            Label = label;
            Icon = icon;
            OnClick = onClick;
            Style = style;
        }
    }

    /// <summary>
    /// Lays out one panel page top to bottom in KoboldKare's style. Sliders and toggles bind directly
    /// to BepInEx ConfigEntries: changes apply immediately and are saved by BepInEx.
    /// </summary>
    public class PageBuilder {
        public const float Pad = 24f;

        private readonly RectTransform content;
        private readonly List<Action> syncers = new List<Action>();
        private float y = 18f;

        internal PageBuilder(RectTransform content, float width) {
            this.content = content;
            Width = width - Pad * 2f;
        }

        /// <summary>Usable width inside the page padding.</summary>
        public float Width { get; }

        /// <summary>The page's root, for custom UI. Place things at <see cref="Cursor"/> and call <see cref="Space"/>.</summary>
        public RectTransform Content => content;

        /// <summary>Current vertical position (pixels from the top of the page).</summary>
        public float Cursor => y;

        public void Space(float pixels) => y += pixels;

        public TextMeshProUGUI Header(string text, string icon = null) {
            float textX = Pad;
            if (icon != null) {
                AddIcon(content, icon, Pad, y + 8f, 38f);
                textX += 50f;
            }
            var label = AddText(content, "Header", textX, y, Width - (textX - Pad), 54f, text, 32f, Theme.Text,
                TextAlignmentOptions.MidlineLeft);
            if (Theme.HeadingFont != null) {
                label.font = Theme.HeadingFont;
            }
            var line = NewRect("Line", content);
            Place(line, Pad, y + 54f, Width, 2f);
            AddImage(line, null, new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.45f), sliced: false);
            y += 68f;
            return label;
        }

        /// <summary>A dark card with a big title line and a multi-line body, for live status.</summary>
        public StatusCard Card(float height = 178f) {
            var card = NewRect("Card", content);
            Place(card, Pad, y, Width, height);
            AddRoleImage(card, "panel", Theme.PanelSprite, Theme.Card);
            var result = new StatusCard {
                Title = AddText(card, "Title", 18f, 10f, Width - 36f, 46f, "", 34f, Theme.Text, TextAlignmentOptions.MidlineLeft),
                Body = AddText(card, "Body", 18f, 58f, Width - 36f, height - 66f, "", 22f, Theme.Text, TextAlignmentOptions.TopLeft),
            };
            if (Theme.HeadingFont != null) {
                result.Title.font = Theme.HeadingFont;
            }
            y += height + 14f;
            return result;
        }

        public PanelButton Button(string label, string icon, Action onClick, ButtonStyle style = ButtonStyle.Primary, float height = 64f) {
            var button = MakeStyled(Pad, Width, height, label, icon, onClick, style, style == ButtonStyle.Primary ? 30f : 26f);
            y += height + 14f;
            return button;
        }

        /// <summary>Several buttons side by side on one row.</summary>
        public PanelButton[] Buttons(params ButtonSpec[] specs) {
            const float gap = 12f, height = 64f;
            float w = (Width - gap * (specs.Length - 1)) / Mathf.Max(1, specs.Length);
            var result = new PanelButton[specs.Length];
            for (int i = 0; i < specs.Length; i++) {
                var s = specs[i];
                result[i] = MakeStyled(Pad + i * (w + gap), w, height, s.Label, s.Icon, s.OnClick, s.Style, 26f);
            }
            y += height + 14f;
            return result;
        }

        public void Slider(string label, ConfigEntry<float> entry, float min, float max, float step,
                           Func<float, string> format = null, Action changed = null) {
            format = format ?? (v => v.ToString(step >= 1f ? "0" : "0.##"));
            MakeSlider(label, min, max, step, () => entry.Value, v => entry.Value = v, format, changed);
        }

        public void Slider(string label, ConfigEntry<int> entry, int min, int max,
                           Func<int, string> format = null, Action changed = null) {
            format = format ?? (v => v.ToString());
            MakeSlider(label, min, max, 1f, () => entry.Value, v => entry.Value = Mathf.RoundToInt(v),
                v => format(Mathf.RoundToInt(v)), changed);
        }

        public void Toggle(string label, ConfigEntry<bool> entry, Action changed = null) {
            PanelButton button = null;
            void Show() {
                bool on = entry.Value;
                button.SetIcon(on ? "ico_toggleon" : "ico_toggleoff");
                button.Background.color = on ? Theme.ToggleOn : Theme.Card;
            }
            button = MakeButton(content, "Toggle", Pad, y, Width, 56f, Theme.PanelSprite, Theme.Card, "ico_toggleoff", label, 24f, () => {
                entry.Value = !entry.Value;
                Show();
                changed?.Invoke();
            }, "panel");
            Show();
            syncers.Add(Show);
            y += 66f;
        }

        /// <summary>Small dim text; height grows to fit the wrapped text.</summary>
        public TextMeshProUGUI Note(string text) {
            var note = AddText(content, "Note", Pad, y - 4f, Width, 30f, text, 19f, Theme.TextDim, TextAlignmentOptions.TopLeft);
            float height = Mathf.Max(26f, note.GetPreferredValues(text, Width, 0f).y) + 4f;
            note.rectTransform.sizeDelta = new Vector2(Width, height);
            y += height + 12f;
            return note;
        }

        /// <summary>Re-read every bound config value into the controls (e.g. after resetting defaults).</summary>
        public void SyncFromConfig() {
            foreach (var sync in syncers) {
                sync();
            }
        }

        internal void Finish() {
            content.sizeDelta = new Vector2(0f, y + 12f);
        }

        // ------------------------------------------------------------------ internals

        private PanelButton MakeStyled(float x, float w, float h, string label, string icon, Action onClick, ButtonStyle style, float fontSize) {
            Sprite sprite;
            Color color;
            string role;
            switch (style) {
                case ButtonStyle.Primary:
                    sprite = Theme.ButtonSprite;
                    color = Theme.Button;
                    role = "button";
                    break;
                case ButtonStyle.Cancel:
                    sprite = Theme.PanelSprite;
                    color = Theme.Cancel;
                    role = "buttonCancel";
                    break;
                case ButtonStyle.Subtle:
                    sprite = Theme.PanelSprite;
                    color = Theme.Card;
                    role = "panel";
                    break;
                default:
                    sprite = Theme.PanelSprite;
                    color = Theme.Okay;
                    role = "buttonOk";
                    break;
            }
            return MakeButton(content, label ?? "Button", x, y, w, h, sprite, color, icon, label, fontSize,
                () => onClick?.Invoke(), role);
        }

        private void MakeSlider(string label, float min, float max, float step, Func<float> get, Action<float> set,
                                Func<float, string> format, Action changed) {
            AddText(content, "Label", Pad, y, Width * 0.72f, 32f, label, 24f, Theme.Text, TextAlignmentOptions.MidlineLeft);
            var valueText = AddText(content, "Value", Pad + Width * 0.6f, y, Width * 0.4f, 32f, format(get()), 24f,
                Theme.Accent, TextAlignmentOptions.MidlineRight);

            var root = NewRect("Slider", content);
            Place(root, Pad + 4f, y + 38f, Width - 8f, 26f);
            var background = NewRect("Background", root);
            Stretch(background, 0f, 8f, 0f, 8f);
            // A themed control sprite is drawn as-is; the plain sprite gets a faint text-coloured track.
            AddRoleImage(background, "control", Theme.PanelSprite, Theme.GetRoleSprite("control") != null
                ? Color.white
                : new Color(Theme.Text.r, Theme.Text.g, Theme.Text.b, 0.18f));
            var fillArea = NewRect("Fill Area", root);
            Stretch(fillArea, 0f, 8f, 0f, 8f);
            var fill = NewRect("Fill", fillArea);
            fill.sizeDelta = Vector2.zero;
            AddImage(fill, Theme.PanelSprite, new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.85f));
            var handleArea = NewRect("Handle Slide Area", root);
            Stretch(handleArea, 13f, 0f, 13f, 0f);
            var handle = NewRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(26f, 0f);
            var handleImage = AddImage(handle, Theme.KnobSprite ?? Theme.PanelSprite, Color.white, sliced: false);

            var slider = root.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = false;
            slider.colors = GameColors();
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            slider.SetValueWithoutNotify(get());
            slider.onValueChanged.AddListener(v => {
                float snapped = Mathf.Clamp(Mathf.Round(v / step) * step, min, max);
                if (Mathf.Abs(get() - snapped) > 0.0001f) {
                    set(snapped);
                }
                valueText.text = format(snapped);
                changed?.Invoke();
            });
            syncers.Add(() => {
                slider.SetValueWithoutNotify(get());
                valueText.text = format(get());
            });
            y += 78f;
        }
    }
}
