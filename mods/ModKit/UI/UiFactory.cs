using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace KoboldKare.ModKit {
    /// <summary>
    /// Low-level uGUI helpers in the game's style. Layout is manual (top-left anchored, reference
    /// pixels at 1920x1080) rather than LayoutGroups, which keeps working even if parts of the game's
    /// UnityEngine.UI were stripped.
    /// </summary>
    public static class UiFactory {
        public static RectTransform NewRect(string name, Transform parent) {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Position from the parent's top-left corner.</summary>
        public static void Place(RectTransform rect, float x, float y, float w, float h) {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
        }

        /// <summary>Fill the parent, inset by the given margins.</summary>
        public static void Stretch(RectTransform rect, float left, float top, float right, float bottom) {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        public static Image AddImage(RectTransform rect, Sprite sprite, Color color, bool sliced = true) {
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.type = sliced && sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            return image;
        }

        /// <summary>
        /// Background for a UI role ("window", "panel", "titleBar", "button", "buttonOk", "buttonCancel",
        /// "control"...): the active theme's sprite for that role if it has one, else <paramref name="fallback"/>.
        /// </summary>
        public static Image AddRoleImage(RectTransform rect, string role, Sprite fallback, Color color) {
            var themed = Theme.GetRoleSprite(role);
            if (themed == null) {
                return AddImage(rect, fallback, color);
            }
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = themed.Sprite;
            image.type = themed.Type;
            image.color = color;
            return image;
        }

        public static Image AddIcon(RectTransform parent, string icon, float x, float y, float size) {
            var rect = NewRect("Icon", parent);
            Place(rect, x, y, size, size);
            var image = AddImage(rect, Theme.Icon(icon), Theme.IconTint, sliced: false);
            image.preserveAspect = true;
            image.raycastTarget = false;
            if (image.sprite == null) {
                image.color = new Color(1f, 1f, 1f, 0f);
            }
            return image;
        }

        public static TextMeshProUGUI AddText(RectTransform parent, string name, float x, float y, float w, float h,
                                              string text, float size, Color color, TextAlignmentOptions align) {
            var rect = NewRect(name, parent);
            Place(rect, x, y, w, h);
            var tmp = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (Theme.Font != null) {
                tmp.font = Theme.Font;
            }
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.richText = true;
            tmp.enableWordWrapping = true;
            tmp.raycastTarget = false;
            tmp.text = text;
            return tmp;
        }

        /// <summary>The ColorBlock every game button uses (cyan hover, deep-blue press).</summary>
        public static ColorBlock GameColors() {
            var colors = ColorBlock.defaultColorBlock;
            colors.normalColor = Color.white;
            colors.highlightedColor = Theme.Highlight;
            colors.pressedColor = Theme.Pressed;
            colors.selectedColor = Color.white; // don't leave a tint stuck on after a click
            colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.5f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.1f;
            return colors;
        }

        public static PanelButton MakeButton(RectTransform parent, string name, float x, float y, float w, float h,
                                             Sprite sprite, Color color, string icon, string label, float fontSize,
                                             UnityAction onClick, string role = null) {
            var rect = NewRect(name, parent);
            Place(rect, x, y, w, h);
            var image = role != null ? AddRoleImage(rect, role, sprite, color) : AddImage(rect, sprite, color);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.colors = GameColors();
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            if (onClick != null) {
                button.onClick.AddListener(onClick);
            }
            var parts = new PanelButton { Button = button, Background = image };
            float iconSize = Mathf.Min(40f, h - 16f);
            parts.Icon = AddIcon(rect, icon, 14f, (h - iconSize) / 2f, iconSize);
            if (label != null) {
                // If the icon isn't loaded, give its space to the label.
                float labelX = parts.Icon.sprite != null ? 14f + iconSize + 12f : 18f;
                parts.Label = AddText(rect, "Label", labelX, 0f, w - labelX - 14f, h, label, fontSize,
                    Theme.Text, TextAlignmentOptions.MidlineLeft);
                // One line only; shrink to fit rather than wrap.
                parts.Label.enableWordWrapping = false;
                parts.Label.enableAutoSizing = true;
                parts.Label.fontSizeMax = fontSize;
                parts.Label.fontSizeMin = 14f;
            }
            return parts;
        }
    }

    /// <summary>A button made by the panel/page builder.</summary>
    public class PanelButton {
        public Button Button;
        public Image Background;
        public Image Icon;
        public TextMeshProUGUI Label;

        public bool Interactable {
            get => Button.interactable;
            set => Button.interactable = value;
        }

        public void SetLabel(string text) {
            if (Label != null) {
                Label.text = text;
            }
        }

        /// <summary>Swap the icon (ignored if that sprite isn't loaded).</summary>
        public void SetIcon(string spriteName) {
            var sprite = Theme.Icon(spriteName);
            if (sprite != null && Icon != null) {
                Icon.sprite = sprite;
                Icon.color = Theme.IconTint;
            }
        }
    }
}
