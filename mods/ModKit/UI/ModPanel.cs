using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using static KoboldKare.ModKit.UiFactory;

namespace KoboldKare.ModKit {
    /// <summary>
    /// The shared in-game mod panel (default hotkey F8). Each mod adds a tab:
    /// <code>
    /// ModPanel.AddPage("My Mod", "ico_star",
    ///     build: page => {
    ///         page.Header("Settings", "ico_gear");
    ///         page.Slider("Speed", speedConfig, 0f, 10f, 0.5f);
    ///         page.Toggle("Enabled", enabledConfig);
    ///     },
    ///     refresh: () => { /* update live text every ~0.4s while visible */ });
    /// </code>
    /// The panel docks to the right, frees the cursor and pauses player controls the same way the
    /// game's own menus do, and steps aside when another menu (pause, chat) opens.
    /// </summary>
    public static class ModPanel {
        private class Page {
            public string Title;
            public string Icon;
            public int Order;
            public Action<PageBuilder> Build;
            public Action Refresh;
            public PageBuilder Builder;
            public GameObject Root;
            public PanelButton Tab;
        }

        private static readonly List<Page> Pages = new List<Page>();
        private static PanelHost host;
        private static bool dirty;
        private static string parsedKeyName;
        private static Key? parsedKey;
        private static bool errorLogged;

        public static bool IsOpen => host != null && host.IsOpen;

        public static IReadOnlyList<string> PageTitles => Pages.Select(p => p.Title).ToList();

        static ModPanel() {
            // A theme change rebuilds the panel; if it's open, reopen it on the same tab.
            Theme.Changed += () => {
                dirty = true;
                if (IsOpen) {
                    string page = host.CurrentTitle;
                    EnsureHost();
                    host.Open(page);
                }
            };
        }

        /// <summary>
        /// Add a tab. <paramref name="build"/> runs when the panel is (re)built; <paramref name="refresh"/>
        /// runs about every 0.4s while that tab is visible. Lower <paramref name="order"/> = further left.
        /// </summary>
        public static void AddPage(string title, string icon, Action<PageBuilder> build, Action refresh = null, int order = 100) {
            if (string.IsNullOrEmpty(title)) {
                throw new ArgumentException("Page needs a title", nameof(title));
            }
            Pages.RemoveAll(p => p.Title == title);
            Pages.Add(new Page { Title = title, Icon = icon, Order = order, Build = build, Refresh = refresh });
            Pages.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Title, b.Title));
            dirty = true;
        }

        public static void Open(string pageTitle = null) {
            if (!CanOpen()) {
                return;
            }
            EnsureHost();
            host.Open(pageTitle);
        }

        public static void Close() => host?.Close(restoreControls: true);

        /// <summary>Re-read config values into every page's sliders/toggles (call after changing config in code).</summary>
        public static void SyncFromConfig() {
            foreach (var page in Pages) {
                page.Builder?.SyncFromConfig();
            }
        }

        // ------------------------------------------------------------------ driven by ModKitRunner

        internal static void Update() {
            var keyboard = Keyboard.current;
            var key = GetKey();
            if (keyboard == null || key == null || !keyboard[key.Value].wasPressedThisFrame) {
                return;
            }
            if (IsOpen) {
                Close();
            } else {
                Open();
            }
        }

        internal static void ReportError(Exception e) {
            if (!errorLogged) {
                errorLogged = true;
                ModKit.Log.LogError("Mod panel error (further errors suppressed): " + e);
            }
        }

        private static Key? GetKey() {
            string name = ModKitPlugin.PanelHotkeyName;
            if (name != parsedKeyName) {
                parsedKeyName = name;
                parsedKey = Enum.TryParse((name ?? "").Trim(), true, out Key k) && k != Key.None ? k : (Key?)null;
                if (parsedKey == null) {
                    ModKit.Log.LogWarning($"Panel hotkey '{name}' isn't a valid key name; the panel can't be opened.");
                }
            }
            return parsedKey;
        }

        private static bool CanOpen() =>
            // Same conditions the game uses before opening chat/equipment.
            ModKit.InLevel && MainMenu.GetCurrentMode() == MainMenu.MainMenuMode.None && !Pauser.GetPaused();

        private static void EnsureHost() {
            if (host != null && dirty) {
                UnityEngine.Object.Destroy(host.gameObject);
                host = null;
            }
            if (host == null) {
                Theme.Load();
                var go = new GameObject("ModKit_Panel");
                UnityEngine.Object.DontDestroyOnLoad(go);
                host = go.AddComponent<PanelHost>();
                host.Build(Pages);
                dirty = false;
            }
        }

        // ------------------------------------------------------------------ the panel object

        private class PanelHost : MonoBehaviour {
            private const float Width = 620f;
            private const float TitleH = 76f;
            private const float TabH = 60f;
            private const float FooterH = 50f;

            private List<Page> pages;
            private Page current;
            private TextMeshProUGUI titleText;
            private TextMeshProUGUI footerText;
            private bool open;
            private float nextRefresh;
            private bool refreshErrorLogged;

            public bool IsOpen => open;
            public string CurrentTitle => current?.Title;

            public void Build(List<Page> list) {
                pages = list;
                var canvas = gameObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 500;
                var scaler = gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
                gameObject.AddComponent<GraphicRaycaster>();

                var panel = NewRect("Panel", transform);
                panel.anchorMin = new Vector2(1f, 0f);
                panel.anchorMax = new Vector2(1f, 1f);
                panel.pivot = new Vector2(1f, 0.5f);
                panel.sizeDelta = new Vector2(Width, -80f);
                panel.anchoredPosition = new Vector2(-28f, 0f);
                AddRoleImage(panel, "window", Theme.PanelSprite, Theme.Body);

                // Title bar (ConfirmPopup "TopPanel").
                var title = NewRect("TitleBar", panel);
                title.anchorMin = new Vector2(0f, 1f);
                title.anchorMax = new Vector2(1f, 1f);
                title.pivot = new Vector2(0.5f, 1f);
                title.sizeDelta = new Vector2(0f, TitleH);
                title.anchoredPosition = Vector2.zero;
                AddRoleImage(title, "titleBar", Theme.PanelSprite, Theme.TitleBar);
                AddIcon(title, Theme.FirstAvailable("ico_gear", "ico_kobold_paperdoll"), 16f, 12f, 52f);
                titleText = AddText(title, "Title", 80f, 0f, 440f, TitleH, "Mods", 40f, Theme.TitleText, TextAlignmentOptions.MidlineLeft);
                if (Theme.HeadingFont != null) {
                    titleText.font = Theme.HeadingFont;
                }
                titleText.enableWordWrapping = false;
                titleText.enableAutoSizing = true;
                titleText.fontSizeMax = 40f;
                titleText.fontSizeMin = 20f;
                var close = MakeButton(title, "Close", Width - 66f, 12f, 52f, 52f, Theme.PanelSprite, new Color(1f, 1f, 1f, 0f),
                    // ico_close is a door in this game; ico_x-out is the X.
                    Theme.FirstAvailable("ico_x-out", "ico_stop"), null, 0f, () => Close(restoreControls: true));
                close.Icon.rectTransform.anchoredPosition = new Vector2(6f, -6f);

                // Tab strip: one tab per mod page.
                var tabs = NewRect("Tabs", panel);
                tabs.anchorMin = new Vector2(0f, 1f);
                tabs.anchorMax = new Vector2(1f, 1f);
                tabs.pivot = new Vector2(0.5f, 1f);
                tabs.sizeDelta = new Vector2(0f, TabH);
                tabs.anchoredPosition = new Vector2(0f, -TitleH);
                const float tabPad = 12f, tabGap = 8f;
                int count = Mathf.Max(1, pages.Count);
                float tabW = Mathf.Min(220f, (Width - tabPad * 2f - tabGap * (count - 1)) / count);
                for (int i = 0; i < pages.Count; i++) {
                    var page = pages[i];
                    page.Tab = MakeButton(tabs, "Tab", tabPad + i * (tabW + tabGap), 10f, tabW, TabH - 14f, Theme.PanelSprite, Theme.Card,
                        page.Icon, page.Title, 22f, () => Show(page), "panel");
                }

                // One scroll view per page; only the current one is active.
                var body = NewRect("Body", panel);
                Stretch(body, 0f, TitleH + TabH, 0f, FooterH);
                foreach (var page in pages) {
                    BuildPage(page, body);
                }
                if (pages.Count == 0) {
                    AddText(body, "Empty", PageBuilder.Pad, 20f, Width - PageBuilder.Pad * 2f, 80f,
                        "No mods have added a page yet.", 24f, Theme.TextDim, TextAlignmentOptions.TopLeft);
                }

                var footer = NewRect("Footer", panel);
                footer.anchorMin = new Vector2(0f, 0f);
                footer.anchorMax = new Vector2(1f, 0f);
                footer.pivot = new Vector2(0.5f, 0f);
                footer.sizeDelta = new Vector2(0f, FooterH);
                footer.anchoredPosition = Vector2.zero;
                footerText = AddText(footer, "Hint", PageBuilder.Pad, 0f, Width - PageBuilder.Pad * 2f, FooterH, "", 20f, Theme.TextDim,
                    TextAlignmentOptions.Midline);

                gameObject.SetActive(false);
            }

            private void BuildPage(Page page, RectTransform body) {
                var scroll = NewRect(page.Title, body);
                Stretch(scroll, 0f, 0f, 0f, 0f);
                var viewport = NewRect("Viewport", scroll);
                Stretch(viewport, 0f, 0f, 0f, 0f);
                AddImage(viewport, null, new Color(0f, 0f, 0f, 0.001f), sliced: false); // catches drag/scroll
                viewport.gameObject.AddComponent<RectMask2D>();
                var content = NewRect("Content", viewport);
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(1f, 1f);
                content.pivot = new Vector2(0.5f, 1f);
                content.anchoredPosition = Vector2.zero;
                var scrollRect = scroll.gameObject.AddComponent<ScrollRect>();
                scrollRect.viewport = viewport;
                scrollRect.content = content;
                scrollRect.horizontal = false;
                scrollRect.vertical = true;
                scrollRect.movementType = ScrollRect.MovementType.Clamped;
                scrollRect.scrollSensitivity = 40f;

                page.Builder = new PageBuilder(content, Width);
                try {
                    page.Build?.Invoke(page.Builder);
                } catch (Exception e) {
                    ModKit.Log.LogError($"Building panel page '{page.Title}' failed: {e}");
                    page.Builder.Note($"{Theme.Red}This page failed to build:</color> {e.GetType().Name}: {e.Message}");
                }
                page.Builder.Finish();
                page.Root = scroll.gameObject;
                page.Root.SetActive(false);
            }

            public void Open(string pageTitle) {
                gameObject.SetActive(true);
                open = true;
                GameManager.SetControlsActive(false);
                OrbitCamera.SetTracking(false);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                var target = pages.FirstOrDefault(p => p.Title == pageTitle) ?? current ?? pages.FirstOrDefault();
                if (target != null) {
                    Show(target);
                }
                foreach (var page in pages) {
                    page.Builder?.SyncFromConfig();
                }
                footerText.text = $"{ModKitPlugin.PanelHotkeyName} to close  ·  changes save automatically";
                nextRefresh = 0f;
            }

            public void Close(bool restoreControls) {
                open = false;
                gameObject.SetActive(false);
                GameManager.SetControlsActive(true);
                if (restoreControls) {
                    OrbitCamera.SetTracking(true);
                    Cursor.lockState = CursorLockMode.Locked;
                }
            }

            private void Show(Page page) {
                current = page;
                foreach (var p in pages) {
                    p.Root?.SetActive(p == page);
                    if (p.Tab != null) {
                        p.Tab.Background.color = p == page
                            ? new Color(Theme.TitleBar.r, Theme.TitleBar.g, Theme.TitleBar.b, 0.6f)
                            : Theme.Card;
                    }
                }
                titleText.text = page.Title;
                nextRefresh = 0f;
            }

            private void Update() {
                if (!open) {
                    return;
                }
                // Another menu (pause, chat...) took over: step aside and let it own the cursor.
                if (MainMenu.GetCurrentMode() != MainMenu.MainMenuMode.None || Pauser.GetPaused() || !ModKit.InLevel) {
                    Close(restoreControls: false);
                    return;
                }
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                if (current?.Refresh != null && Time.unscaledTime >= nextRefresh) {
                    nextRefresh = Time.unscaledTime + 0.4f;
                    try {
                        current.Refresh();
                    } catch (Exception e) {
                        if (!refreshErrorLogged) {
                            refreshErrorLogged = true;
                            ModKit.Log.LogError($"Refreshing panel page '{current.Title}' failed: {e}");
                        }
                    }
                }
            }

            private void OnDestroy() {
                if (open) {
                    // Destroyed while open (e.g. map unloaded): never leave the player frozen.
                    try {
                        GameManager.SetControlsActive(true);
                    } catch {
                        // GameManager may already be gone during shutdown.
                    }
                }
                if (host == this) {
                    host = null;
                }
            }
        }
    }
}
