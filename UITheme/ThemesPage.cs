using System;
using System.Diagnostics;
using KoboldKare.ModKit;
using TMPro;
using UnityEngine;
using static KoboldKare.ModKit.UiFactory;

namespace UITheme {
    /// <summary>The "Themes" tab in the ModKit panel: a browser of installed themes with colour swatches.</summary>
    internal static class ThemesPage {
        private static StatusCard status;

        private static readonly string[] SwatchRoles = {
            Roles.Surface, Roles.Window, Roles.TitleBar, Roles.Button, Roles.Control, Roles.Accent, Roles.Text, Roles.Icon,
        };

        public static void Build(PageBuilder page) {
            status = page.Card(140f);
            page.Button("Import theme...", Theme.FirstAvailable("ico_box_open", "ico_open", "ico_bag"), ThemeImporter.ImportFromDialog);
            page.Buttons(
                new ButtonSpec("Reload themes", Theme.FirstAvailable("ico_refresh", "ico_save"), Reload),
                new ButtonSpec("Open themes folder", Theme.FirstAvailable("ico_open", "ico_bag"), OpenFolder, ButtonStyle.Subtle));
            page.Header("Installed themes", Theme.FirstAvailable("ico_star", "ico_gear"));

            foreach (var theme in ThemeLibrary.Themes) {
                AddThemeRow(page, theme);
            }
            page.Note($"Themes are folders with a theme.json in BepInEx/themes (or a plugin's themes folder). " +
                      $"You can also pick one in Options > Graphics > {ThemeSetting.Label}, or with /theme <name>.");
        }

        public static void Refresh() {
            var t = ThemeLibrary.Current;
            status.Title.text = $"{Theme.Cyan}●</color> {t.Name}";
            status.Body.text = (string.IsNullOrEmpty(t.Author) ? "" : $"by {t.Author}" + (string.IsNullOrEmpty(t.Version) ? "" : $"  ·  v{t.Version}") + "\n") +
                               (ThemeLibrary.CurrentFont != null ? $"Font: {ThemeLibrary.CurrentFont.name.Replace(" (UITheme)", "")}" : "Font: game default") +
                               (string.IsNullOrEmpty(ThemeImporter.LastResult) ? "" :
                                   "\n" + (ThemeImporter.LastResult.StartsWith("Import failed") ? Theme.Red : Theme.Cyan) +
                                   ThemeImporter.LastResult + "</color>");
        }

        private static void AddThemeRow(PageBuilder page, ThemeDefinition theme) {
            const float height = 118f;
            float y = page.Cursor;
            float x = PageBuilder.Pad;
            float w = page.Width;
            bool current = theme == ThemeLibrary.Current;

            var row = NewRect("Theme " + theme.Id, page.Content);
            Place(row, x, y, w, height);
            AddImage(row, Theme.PanelSprite, current ? new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.25f) : Theme.Card);

            var name = AddText(row, "Name", 16f, 8f, w - 190f, 36f, theme.Name, 28f, Theme.Text, TextAlignmentOptions.MidlineLeft);
            name.enableWordWrapping = false;
            name.enableAutoSizing = true;
            name.fontSizeMin = 16f;
            name.fontSizeMax = 28f;
            string sub = string.IsNullOrEmpty(theme.Author) ? theme.Description : $"by {theme.Author}" +
                         (string.IsNullOrEmpty(theme.Description) ? "" : "  ·  " + theme.Description);
            var subText = AddText(row, "Info", 16f, 44f, w - 190f, 28f, sub, 18f, Theme.TextDim, TextAlignmentOptions.TopLeft);
            subText.enableWordWrapping = false;
            subText.overflowMode = TextOverflowModes.Ellipsis;

            // Swatches of the theme's main roles (its role sprite tinted by the role colour, if it has one).
            ThemeLibrary.EnsureSprites(theme);
            for (int i = 0; i < SwatchRoles.Length; i++) {
                var sw = NewRect("Swatch", row);
                Place(sw, 16f + i * 34f, 78f, 28f, 28f);
                var swatchColor = theme.Resolve(SwatchRoles[i]);
                swatchColor.a = Mathf.Max(swatchColor.a, 0.85f);
                if (theme.RoleSprites.TryGetValue(SwatchRoles[i], out var spec) && spec.Loaded != null) {
                    AddImage(sw, spec.Loaded, swatchColor, sliced: false);
                } else {
                    AddImage(sw, Theme.PanelSprite, swatchColor);
                }
            }

            var apply = MakeButton(row, "Apply", w - 166f, (height - 56f) / 2f, 150f, 56f, Theme.PanelSprite,
                current ? Theme.Card : Theme.Okay, current ? "ico_checkmark" : Theme.FirstAvailable("ico_pointing_at", "ico_star"),
                current ? "In use" : "Apply", 24f, () => ThemeLibrary.Select(theme));
            apply.Interactable = !current;

            page.Space(height + 10f);
        }

        private static void Reload() {
            ThemeLibrary.Reload();
            ThemeLibrary.Reapply();
        }

        internal static void OpenFolder() {
            try {
                Process.Start("explorer.exe", "\"" + ThemeLibrary.UserThemesFolder + "\"");
            } catch (Exception e) {
                UIThemePlugin.Log.LogWarning("Couldn't open the themes folder: " + e.Message);
            }
        }
    }
}
