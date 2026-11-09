using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Components;
using UnityEngine.UI;
using UnityScriptableSettings;

namespace UITheme {
    /// <summary>
    /// "UI Theme" dropdown inside the game's own Options menu (Graphics tab by default). It's a real
    /// UnityScriptableSettings setting, so the game draws it with its own dropdown widget. The choice is
    /// stored in UITheme's BepInEx config (by theme id), not in PlayerPrefs, so adding/removing theme
    /// folders never shifts your selection.
    /// </summary>
    public class ThemeSetting : SettingDropdown, ILabeledSetting {
        internal const string Label = "UI Theme";
        public string LabelText => "Theme";       // under the "UI Theme" section header
        internal static ThemeSetting Instance;
        private static bool syncing;

        internal void SetOptions(string[] names) {
            dropdownOptions = names;
        }

        public override void SetValue(int value) {
            base.SetValue(value);
            if (!syncing) {
                ThemeLibrary.SelectIndex(GetValue());
            }
        }

        public override void Load() {
            Sync();
        }

        public override void Save() {
            // Saved by UITheme's own config when the theme changes.
        }

        public override void ResetToDefault() {
            SetValue(0);
        }

        /// <summary>Update the dropdown's options and selection to match the theme library.</summary>
        internal static void Sync() {
            if (Instance == null) {
                return;
            }
            syncing = true;
            try {
                Instance.SetOptions(ThemeLibrary.Themes.Select(t => t.Name).ToArray());
                Instance.selectedValue = -1; // force the change event even if the index is the same
                Instance.SetValue(ThemeLibrary.CurrentIndex);
                Instance.NotifyChange();
            } finally {
                syncing = false;
            }
            EditorButtons.Refresh();
        }
    }

    /// <summary>
    /// "Custom theme" row under "Import theme": [+ Create theme] [Edit theme]. Built from a copy of the
    /// game's own dropdown row, so it lines up and is restyled like the rows above it; the dropdown is
    /// swapped for two buttons that use its look. Edit only works on themes made with the editor.
    /// </summary>
    internal static class EditorButtons {
        private static Button edit;

        internal static void Create(ScriptableSettingSpawner spawner) {
            if (spawner.dropdown == null) {
                return;
            }
            var row = UnityEngine.Object.Instantiate(spawner.dropdown, spawner.transform);
            row.name = "UIThemeEditorButtons";
            row.transform.localScale = Vector3.one;
            var dropdown = row.GetComponentInChildren<TMP_Dropdown>(true);
            if (dropdown == null || dropdown.gameObject == row) {
                UnityEngine.Object.Destroy(row);
                UIThemePlugin.Log.LogWarning("The Options row layout wasn't as expected, so the Create theme buttons are only in the F8 panel.");
                return;
            }
            foreach (var text in row.GetComponentsInChildren<TMP_Text>(true)) {
                if (text.name == "Label" && !text.transform.IsChildOf(dropdown.transform)) {
                    var localizer = text.GetComponent<LocalizeStringEvent>();
                    if (localizer != null) {
                        localizer.enabled = false;
                    }
                    text.text = "Custom theme";
                }
            }

            // Two buttons in exactly the dropdown's spot, drawn like it.
            var spot = (RectTransform)dropdown.transform;
            var holder = new GameObject("Buttons", typeof(RectTransform)).GetComponent<RectTransform>();
            holder.SetParent(spot.parent, false);
            holder.SetSiblingIndex(spot.GetSiblingIndex());
            holder.anchorMin = spot.anchorMin;
            holder.anchorMax = spot.anchorMax;
            holder.pivot = spot.pivot;
            holder.anchoredPosition = spot.anchoredPosition;
            holder.sizeDelta = spot.sizeDelta;
            var look = dropdown.targetGraphic as Image ?? dropdown.GetComponent<Image>();
            var caption = dropdown.captionText;
            MakeButton(holder, 0f, 0.5f, "+ Create theme", look, caption, dropdown.colors, ThemeEditor.Create);
            edit = MakeButton(holder, 0.5f, 1f, "Edit theme", look, caption, dropdown.colors, () => ThemeEditor.Edit(ThemeLibrary.Current));
            UnityEngine.Object.DestroyImmediate(dropdown.gameObject);

            spawner.titles.Add(row); // removed with the section titles when the spawner cleans up
            Refresh();
            Restyler.RequestScan();
        }

        internal static void Refresh() {
            if (edit != null) {
                edit.interactable = ThemeLibrary.IsEditable(ThemeLibrary.Current);
            }
        }

        private static Button MakeButton(RectTransform parent, float from, float to, string label, Image look, TMP_Text caption,
                                         ColorBlock colors, Action onClick) {
            var rect = new GameObject(label, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(from, 0f);
            rect.anchorMax = new Vector2(to, 1f);
            rect.offsetMin = new Vector2(from > 0f ? 6f : 0f, 0f);
            rect.offsetMax = new Vector2(to < 1f ? -6f : 0f, 0f);
            var image = rect.gameObject.AddComponent<Image>();
            if (look != null) {
                image.sprite = look.sprite;
                image.type = look.type;
                image.color = look.color;
            }
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.colors = colors;
            button.onClick.AddListener(() => onClick());

            var textRect = new GameObject("Text", typeof(RectTransform)).GetComponent<RectTransform>();
            textRect.SetParent(rect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10f, 0f);
            textRect.offsetMax = new Vector2(-10f, 0f);
            var text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            if (caption != null) {
                text.font = caption.font;
                text.fontSize = caption.fontSize;
                text.color = caption.color;
            }
            text.enableAutoSizing = true;
            text.fontSizeMax = caption != null ? caption.fontSize : 24f;
            text.fontSizeMin = 12f;
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            text.text = label;
            return button;
        }
    }

    /// <summary>Settings that bring their own label text instead of a localization-table entry.</summary>
    internal interface ILabeledSetting {
        string LabelText { get; }
    }

    /// <summary>
    /// "Import UI theme" row in Options: a dropdown of actions (the game has no plain button setting,
    /// and this way it uses the game's own widget and controller navigation). The first entry shows the
    /// last result; picking an action runs it and snaps back to that entry.
    /// </summary>
    public class ThemeImportSetting : SettingDropdown, ILabeledSetting {
        internal static ThemeImportSetting Instance;
        private const string Idle = "Choose an action...";
        private static readonly string[] Actions = { "Import from file...", "Open themes folder", "Reload themes" };
        private static bool resetting;

        public string LabelText => "Import theme";

        internal void Refresh() {
            string status = string.IsNullOrEmpty(ThemeImporter.LastResult) ? Idle : ThemeImporter.LastResult;
            dropdownOptions = new[] { status }.Concat(Actions).ToArray();
        }

        public override void SetValue(int value) {
            base.SetValue(value);
            if (resetting || value <= 0) {
                return;
            }
            // Run the action after the dropdown's own change handling has finished, then reset it.
            ThemeImporter.RunOnMainThread(() => {
                switch (value) {
                    case 1: ThemeImporter.ImportFromDialog(); break;
                    case 2: ThemesPage.OpenFolder(); break;
                    case 3:
                        ThemeLibrary.Reload();
                        ThemeLibrary.Reapply();
                        break;
                }
                ResetToIdle();
            });
        }

        internal static void ResetToIdle() {
            if (Instance == null) {
                return;
            }
            resetting = true;
            try {
                Instance.Refresh();
                Instance.selectedValue = -1;
                Instance.SetValue(0);
                Instance.NotifyChange(); // the game rebuilds the dropdown's options from GetDropdownOptions()
            } finally {
                resetting = false;
            }
        }

        public override void Load() => ResetToIdle();
        public override void Save() { }
        public override void ResetToDefault() => ResetToIdle();
    }

    internal static class OptionsIntegration {
        private static readonly FieldInfo ManagerInstance = AccessTools.Field(typeof(SettingsManager), "instance");
        private static readonly FieldInfo SpawnerDropdowns = AccessTools.Field(typeof(ScriptableSettingSpawner), "dropdowns");

        internal const string SectionTitle = "UI Theme";
        private static readonly FieldInfo ManagerSettings = AccessTools.Field(typeof(SettingsManager), "settings");

        public static void Install(Harmony harmony) {
            harmony.PatchAll(typeof(SettingsManagerAwakePatch));
            harmony.PatchAll(typeof(SettingsManagerStartPatch));
            harmony.PatchAll(typeof(CreateDropDownPatch));
            harmony.PatchAll(typeof(SpawnerRestylePatch));
            // If the settings manager already exists (plugin loaded late), register right away.
            if (ManagerInstance?.GetValue(null) is SettingsManager existing && existing != null) {
                Register();
            }
        }

        /// <summary>Add the UI Theme setting to the game's settings (once per SettingsManager).</summary>
        internal static void Register() {
            try {
                if (ThemeSetting.Instance != null && SettingsManager.GetSetting(ThemeSetting.Instance.name) == ThemeSetting.Instance) {
                    return;
                }
                var settings = SettingsManager.GetSettings();
                if (settings == null || settings.Count == 0) {
                    return;
                }
                string groupName = UIThemePlugin.OptionsGroup.Value;
                var host = settings.FirstOrDefault(s => s != null && s.group != null && s.group.name == groupName)
                           ?? settings.First(s => s != null && s.group != null);

                var setting = ScriptableObject.CreateInstance<ThemeSetting>();
                setting.name = "UIThemeSelection";
                setting.group = host.group;
                // Borrow an existing localized label so the game's localizer is happy; we overwrite its text below.
                setting.localizedName = new LocalizedString(host.localizedName.TableReference, host.localizedName.TableEntryReference);
                setting.SetOptions(ThemeLibrary.Themes.Select(t => t.Name).ToArray());
                ThemeSetting.Instance = setting;
                SettingsManager.AddSetting(setting);

                var import = ScriptableObject.CreateInstance<ThemeImportSetting>();
                import.name = "UIThemeImport";
                import.group = host.group;
                import.localizedName = new LocalizedString(host.localizedName.TableReference, host.localizedName.TableEntryReference);
                import.Refresh();
                ThemeImportSetting.Instance = import;
                SettingsManager.AddSetting(import);
                KeepSectionTogether();
                UIThemePlugin.Log.LogInfo($"Added a '{SectionTitle}' section to Options > {host.group.name}.");
            } catch (Exception e) {
                UIThemePlugin.Log.LogError("Couldn't add the UI Theme option to the Options menu: " + e);
            }
        }

        /// <summary>
        /// The game sorts settings by group with an unstable sort, which could scatter our rows among the
        /// Graphics settings. Re-order (stably) so ours sit together at the end of their group, in a fixed order.
        /// </summary>
        internal static void KeepSectionTogether() {
            try {
                var manager = ManagerInstance?.GetValue(null) as SettingsManager;
                if (manager == null || !(ManagerSettings?.GetValue(manager) is List<Setting> list)) {
                    return;
                }
                var ordered = list
                    .Select((s, index) => new { s, index })
                    .OrderBy(x => x.s != null && x.s.group != null ? x.s.group.name : "", StringComparer.InvariantCulture)
                    // Ours first in their group: the UI Theme section sits at the top of the tab.
                    .ThenBy(x => x.s is ThemeSetting ? 0 : x.s is ThemeImportSetting ? 1 : 2)
                    .ThenBy(x => x.index)
                    .Select(x => x.s)
                    .ToList();
                list.Clear();
                list.AddRange(ordered); // the game's read-only view wraps this same list
            } catch (Exception e) {
                UIThemePlugin.Log.LogWarning("Couldn't order the UI Theme options: " + e.Message);
            }
        }

        [HarmonyPatch(typeof(SettingsManager), "Start")]
        private static class SettingsManagerStartPatch {
            [HarmonyPostfix]
            private static void Postfix() => KeepSectionTogether(); // Start re-sorts the list
        }

        /// <summary>
        /// The "UI Theme" section header. Our rows come first in the group, so the spawner has normally
        /// just created the group's own header ("Graphics") right before our first row: relabel that one
        /// instead of stacking a second header above it.
        /// </summary>
        private static void AddSectionTitle(ScriptableSettingSpawner spawner, Setting option) {
            var last = spawner.titles.LastOrDefault();
            bool justCreated = last != null && last.transform.parent == spawner.transform &&
                               last.transform.GetSiblingIndex() == spawner.transform.childCount - 1;
            if (!justCreated) {
                spawner.CreateTitle(option.group.localizedName);
                last = spawner.titles.LastOrDefault();
            }
            if (last != null) {
                SetTitleText(last, SectionTitle);
            }
        }

        /// <summary>After our last row, start the group's regular section again (localized, e.g. "Graphics").</summary>
        private static void RestoreGroupTitle(ScriptableSettingSpawner spawner, Setting option) {
            spawner.CreateTitle(option.group.localizedName);
        }

        private static void SetTitleText(GameObject title, string label) {
            foreach (var text in title.GetComponentsInChildren<TMP_Text>(true)) {
                if (text.name != "Label") {
                    continue;
                }
                var localizer = text.GetComponent<LocalizeStringEvent>();
                if (localizer != null) {
                    localizer.enabled = false;
                }
                text.text = label;
            }
            // Some title prefabs put the LocalizeStringEvent on a parent; disable any that would rename it.
            foreach (var localizer in title.GetComponentsInChildren<LocalizeStringEvent>(true)) {
                localizer.enabled = false;
            }
        }

        [HarmonyPatch(typeof(SettingsManager), "Awake")]
        private static class SettingsManagerAwakePatch {
            [HarmonyPostfix]
            private static void Postfix(SettingsManager __instance) {
                if (ReferenceEquals(ManagerInstance?.GetValue(null), __instance)) {
                    Register();
                }
            }
        }

        /// <summary>Give our dropdown its real label (the game would localize it to the borrowed string).</summary>
        [HarmonyPatch(typeof(ScriptableSettingSpawner), nameof(ScriptableSettingSpawner.CreateDropDown))]
        private static class CreateDropDownPatch {
            /// <summary>Right before the first of our rows, add the section header.</summary>
            [HarmonyPrefix]
            private static void Prefix(ScriptableSettingSpawner __instance, SettingInt option) {
                if (option is ThemeSetting) {
                    try {
                        AddSectionTitle(__instance, option);
                    } catch (Exception e) {
                        UIThemePlugin.Log.LogWarning("Couldn't add the UI Theme section header: " + e.Message);
                    }
                }
            }

            [HarmonyPostfix]
            private static void Postfix(ScriptableSettingSpawner __instance, SettingInt option) {
                if (!(option is ILabeledSetting labeled)) {
                    return;
                }
                try {
                    if (!(SpawnerDropdowns?.GetValue(__instance) is Dictionary<Setting, TMP_Dropdown> map) ||
                        !map.TryGetValue(option, out var dropdown) || dropdown == null) {
                        return;
                    }
                    // Walk up to the row the spawner instantiated, then fix its "Label".
                    var row = dropdown.transform;
                    while (row.parent != null && row.parent != __instance.transform) {
                        row = row.parent;
                    }
                    foreach (var text in row.GetComponentsInChildren<TMP_Text>(true)) {
                        if (text.name != "Label") {
                            continue;
                        }
                        var localizer = text.GetComponent<LocalizeStringEvent>();
                        if (localizer != null) {
                            localizer.enabled = false;
                        }
                        text.text = labeled.LabelText;
                    }
                    // Our section is done: the rest of the tab gets its normal header back.
                    if (option is ThemeImportSetting) {
                        try {
                            EditorButtons.Create(__instance);
                        } catch (Exception e) {
                            UIThemePlugin.Log.LogWarning("Couldn't add the Create theme buttons: " + e.Message);
                        }
                        RestoreGroupTitle(__instance, option);
                    }
                } catch (Exception e) {
                    UIThemePlugin.Log.LogWarning("Couldn't label the UI Theme option: " + e.Message);
                }
            }
        }

        /// <summary>Options rows are created on demand; restyle them straight away instead of on the next scan.</summary>
        [HarmonyPatch(typeof(ScriptableSettingSpawner), nameof(ScriptableSettingSpawner.CreateTitle))]
        private static class SpawnerRestylePatch {
            [HarmonyPostfix]
            private static void Postfix() => Restyler.RequestScan();
        }
    }
}
