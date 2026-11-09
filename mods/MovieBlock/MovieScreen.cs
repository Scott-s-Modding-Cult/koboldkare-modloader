using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KoboldKare.ModKit;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Video;
using static KoboldKare.ModKit.UiFactory;
using Object = UnityEngine.Object;

namespace MovieBlock {
    /// <summary>
    /// The floating screen: a thin block with a world-space canvas on its front. It browses a <see cref="Source"/>
    /// by tag (thumbnail grid with tag autocomplete) and plays the chosen video with 3D sound. While "focused"
    /// (F7) the cursor is free and player controls are off, so the canvas can be clicked and typed into.
    /// Only the local player sees it; a map load removes it.
    /// </summary>
    internal partial class MovieScreen : MonoBehaviour {
        public static MovieScreen Instance { get; private set; }

        // Canvas units (1 unit = Size / W metres).
        private const float W = 1600f, H = 1000f, Pad = 14f, Gap = 8f, TopH = 72f, StatusH = 40f;
        private const float BodyY = Pad + TopH + StatusH + 6f;
        private const float ControlsH = 76f;
        private const float TagsW = 380f;
        private const int Columns = 5;
        private const int MaxThumbLoads = 4;
        private const int MaxSuggestions = 8;

        private static readonly Color ScreenBg = new Color(0.035f, 0.04f, 0.055f, 0.97f);
        private static readonly Color CellBg = new Color(0.11f, 0.12f, 0.15f, 1f);
        private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

        // ------------------------------------------------------------------ state

        private Source source;
        private int sort;
        private int page;
        private bool lastPage;
        private List<Post> posts = new List<Post>();
        private int current = -1;
        private int searchGen, playGen, acGen;

        private bool focused;
        private bool pendingFocus;
        private float pendingFocusAt, pendingFocusUntil;
        private bool muted;
        private int caretToEndFrames;
        private bool acPending;
        private float acDue;

        private Vector3 followTarget;
        private bool followMoving;

        // ------------------------------------------------------------------ UI

        private Canvas canvas;
        private RectTransform root;
        private TMP_InputField input;
        private PanelButton sourceButton, sortButton, optionsButton, prevPageButton, nextPageButton;
        private TextMeshProUGUI pageLabel, statusText, hintText, bannerText;
        private RectTransform banner;
        private RectTransform browseView, playerView;
        private RectTransform gridContent;
        private ScrollRect gridScroll;
        private TextMeshProUGUI browseMessage;
        private RectTransform suggestBox;
        private readonly List<PanelButton> suggestRows = new List<PanelButton>();
        private readonly List<string> suggestNames = new List<string>();

        private RectTransform videoArea;
        private RawImage videoImage;
        private TextMeshProUGUI overlayText;
        private TextMeshProUGUI infoText;
        private RectTransform tagContent;
        private ScrollRect tagScroll;
        private PanelButton playButton, loopButton, muteButton;
        private RectTransform seekFill, volumeFill;
        private TextMeshProUGUI timeText;

        // ------------------------------------------------------------------ video / downloads

        private VideoPlayer player;
        private AudioSource audioSource;
        private RenderTexture renderTexture;
        private Transfer download;
        private string playingPath;

        private class ThumbJob {
            public int Gen;
            public string Url;
            public RawImage Target;
            public float CellAspect;
        }

        private readonly Queue<ThumbJob> thumbQueue = new Queue<ThumbJob>();
        private int thumbLoads;
        private readonly List<Texture2D> thumbs = new List<Texture2D>();

        // ================================================================== static API

        public static bool Exists => Instance != null;

        // ------------------------------------------------------------------ self-test hooks (see SelfTest.cs)

        internal static Camera TestCamera;

        /// <summary>Builds a screen without a map or player, for the offscreen self-test.</summary>
        internal static MovieScreen CreateForTest(Source src, Camera camera, Vector3 position) {
            Theme.Load();
            TestCamera = camera;
            var go = new GameObject("MovieBlock_TestScreen");
            go.SetActive(false);
            var screen = go.AddComponent<MovieScreen>();
            screen.Build();
            Instance = screen;
            go.SetActive(true);
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation(position - camera.transform.position, Vector3.up);
            screen.SetSource(src);
            return screen;
        }

        internal int TestPostCount => posts.Count;
        internal VideoPlayer TestPlayer => player;
        internal string TestStatus => statusText.text + " | " + browseMessage.text + " | " + overlayText.text;
        internal void TestPlay(int index) => PlayIndex(index);
        internal void TestSetSource(Source src) => SetSource(src);
        internal void TestAutocomplete(string prefix, Action<List<TagSuggestion>> done) => source.Autocomplete(prefix, done);

        internal void TestTypeAndSuggest(string text) {
            focused = true; // lets ShowSuggestions display
            input.text = text;
            input.ActivateInputField();
        }
        public static bool IsFocused => Instance != null && Instance.focused;

        /// <summary>Creates the screen (or switches its site), puts it in front of the player and focuses it soon.</summary>
        public static bool Open(Source src, bool focus) {
            if (!ModKit.InLevel || ModKit.LocalKobold == null) {
                return false;
            }
            if (Instance == null) {
                Theme.Load();
                var go = new GameObject("MovieBlock_Screen");
                go.SetActive(false);
                var screen = go.AddComponent<MovieScreen>();
                screen.Build();
                Instance = screen;
                go.SetActive(true);
                screen.PlaceInFront();
                screen.SetSource(src);
            } else {
                Instance.PlaceInFront();
                if (Instance.source == null || Instance.source.Key != src.Key) {
                    Instance.SetSource(src);
                }
            }
            if (focus) {
                Instance.RequestFocus();
            }
            return true;
        }

        /// <summary>Show or hide AI-generated posts, and search again if the screen is open.</summary>
        public static void SetShowAI(bool show) {
            MovieBlockPlugin.ShowAI.Value = show;
            if (Instance != null) {
                Instance.RefreshOptions();
                Instance.RefreshButtons();
                Instance.Search(true);
            }
        }

        public static void Close() {
            if (Instance != null) {
                Destroy(Instance.gameObject);
            }
        }

        public static bool BringHere() {
            if (Instance == null) {
                return false;
            }
            Instance.PlaceInFront();
            return true;
        }

        public static void ToggleFocus() {
            if (Instance == null) {
                return;
            }
            if (Instance.focused || Instance.pendingFocus) {
                Instance.pendingFocus = false;
                Instance.SetFocus(false);
            } else if (MenusClosed()) {
                Instance.SetFocus(true);
            }
        }

        public static bool SearchFor(string tags) {
            if (Instance == null) {
                return false;
            }
            Instance.input.SetTextWithoutNotify(tags ?? "");
            Instance.Search(true);
            return true;
        }

        public static string Describe() {
            if (Instance == null) {
                return "No movie block. Use /movieblock e621 or /movieblock r34.";
            }
            var s = Instance;
            string what = s.current >= 0 && s.current < s.posts.Count ? $"playing #{s.posts[s.current].Id}" : $"{s.posts.Count} results on page {s.page + 1}";
            return $"Movie block on {s.source.Title}: \"{s.input.text}\", {what}.";
        }

        private static bool MenusClosed() =>
            ModKit.InLevel && MainMenu.GetCurrentMode() == MainMenu.MainMenuMode.None && !Pauser.GetPaused() && !ModPanel.IsOpen;

        // ================================================================== building

        private void Build() {
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            gameObject.AddComponent<GraphicRaycaster>();
            root = (RectTransform)transform;
            root.sizeDelta = new Vector2(W, H);
            root.pivot = new Vector2(0.5f, 0.5f);
            ApplySize();

            BuildFrame();

            // Accent rim + screen background.
            rimImage = AddImage(root, Theme.PanelSprite, new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.55f));
            var screen = NewRect("Screen", root);
            Stretch(screen, 5f, 5f, 5f, 5f);
            screenBgImage = AddImage(screen, Theme.PanelSprite, ScreenBg);

            BuildTopBar(screen);
            BuildStatusBar(screen);

            browseView = NewRect("Browse", screen);
            Stretch(browseView, 0f, BodyY, 0f, 0f);
            BuildBrowse(browseView);

            playerView = NewRect("Player", screen);
            Stretch(playerView, 0f, BodyY, 0f, 0f);
            BuildPlayer(playerView);
            playerView.gameObject.SetActive(false);

            optionsView = NewRect("Options", screen);
            Stretch(optionsView, 0f, BodyY, 0f, 0f);
            BuildOptions(optionsView);
            optionsView.gameObject.SetActive(false);

            // Shown while the screen isn't in use, so it's obvious how to get back in.
            banner = NewRect("Banner", screen);
            Place(banner, (W - 760f) / 2f, H - 150f, 760f, 80f);
            AddImage(banner, Theme.PanelSprite, new Color(0f, 0f, 0f, 0.78f)).raycastTarget = false;
            bannerText = AddText(banner, "Text", 0f, 0f, 760f, 80f, "", 34f, Theme.Accent, TextAlignmentOptions.Center);

            BuildSuggestions(screen); // last: drawn on top

            BuildVideo();
            try {
                visuals = new ImmersiveVisuals(transform);
            } catch (Exception e) {
                // No usable shader: immersive view stays flat (just bigger) instead of breaking the screen.
                visuals = null;
                MovieBlockPlugin.Log.LogWarning("Curved immersive view unavailable: " + e.Message);
            }
        }

        private void BuildFrame() {
            // The physical "block": a dark slab behind the canvas, a little larger than the screen.
            var frame = GameObject.CreatePrimitive(PrimitiveType.Cube);
            frame.name = "Frame";
            var collider = frame.GetComponent<Collider>();
            if (collider != null) {
                Destroy(collider); // visual only: nothing can bump into, grab or push it
            }
            frame.transform.SetParent(transform, false);
            frame.transform.localPosition = new Vector3(0f, 0f, 16f); // canvas faces -Z; +Z is behind it
            frame.transform.localScale = new Vector3(W + 50f, H + 50f, 28f);
            var renderer = frame.GetComponent<Renderer>();
            frameRenderer = renderer;
            var shader = FindShader("Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit",
                "Universal Render Pipeline/Unlit", "Standard", "Sprites/Default");
            if (renderer != null && shader != null) {
                var material = new Material(shader);
                var color = new Color(0.07f, 0.075f, 0.09f, 1f);
                material.color = color;
                if (material.HasProperty("_BaseColor")) {
                    material.SetColor("_BaseColor", color);
                }
                if (material.HasProperty("_Smoothness")) {
                    material.SetFloat("_Smoothness", 0.6f);
                }
                renderer.sharedMaterial = material;
            }
            if (renderer != null) {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
        }

        private static Shader FindShader(params string[] names) {
            foreach (var n in names) {
                var s = Shader.Find(n);
                if (s != null) {
                    return s;
                }
            }
            return null;
        }

        private PanelButton Btn(RectTransform parent, float x, float y, float w, float h, string label, string icon, Action onClick,
                                float font = 26f, Color? color = null) {
            var b = MakeButton(parent, "Button", x, y, w, h, Theme.PanelSprite, color ?? Theme.Button,
                icon != null ? Theme.FirstAvailable(icon) : null, label, font, () => onClick(), "button");
            if (b.Label != null && b.Icon.sprite == null) {
                b.Label.alignment = TextAlignmentOptions.Center;
                Place(b.Label.rectTransform, 6f, 0f, w - 12f, h);
            }
            return b;
        }

        private void BuildTopBar(RectTransform parent) {
            var bar = NewRect("Top", parent);
            topBar = bar;
            Place(bar, Pad, Pad, W - Pad * 2f - 10f, TopH);
            float h = TopH, x = 0f;

            sourceButton = Btn(bar, x, 0f, 140f, h, "e621", null, SwitchSource, 30f, Theme.TitleBar);
            x += 140f + Gap;

            // Right-hand group, laid out from the right edge.
            float right = W - Pad * 2f - 10f;
            right -= h;
            Btn(bar, right, 0f, h, h, null, "ico_x-out", Close, 26f, new Color(0.6f, 0.2f, 0.2f, 0.8f));
            right -= Gap + 160f;
            optionsButton = Btn(bar, right, 0f, 160f, h, "Options", "ico_gear", ToggleOptions, 24f);
            right -= Gap * 2f + 64f;
            nextPageButton = Btn(bar, right, 0f, 64f, h, ">", null, () => ChangePage(1), 34f);
            right -= 96f;
            pageLabel = AddText(bar, "Page", right, 0f, 96f, h, "1", 26f, Theme.Text, TextAlignmentOptions.Center);
            right -= 64f;
            prevPageButton = Btn(bar, right, 0f, 64f, h, "<", null, () => ChangePage(-1), 34f);
            right -= Gap * 2f + 190f;
            sortButton = Btn(bar, right, 0f, 190f, h, "Newest", "ico_star", CycleSort, 24f);
            right -= Gap + 150f;
            Btn(bar, right, 0f, 150f, h, "Search", "ico_magnifying_looking_glass", () => Search(true), 26f, Theme.TitleBar);
            right -= Gap;

            input = MakeInput(bar, x, 0f, right - x, h);
        }

        private TMP_InputField MakeInput(RectTransform parent, float x, float y, float w, float h,
                                         string hint = "tags, e.g. kobold solo -male", bool multiline = false) {
            var align = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
            var rect = NewRect("Tags", parent);
            Place(rect, x, y, w, h);
            var bg = AddRoleImage(rect, "control", Theme.PanelSprite, new Color(0f, 0f, 0f, 0.55f));
            var area = NewRect("TextArea", rect);
            Stretch(area, 18f, 8f, 18f, 8f);
            area.gameObject.AddComponent<RectMask2D>();
            var placeholder = AddText(area, "Placeholder", 0f, 0f, 0f, 0f, hint, 28f, Theme.TextDim, align);
            Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);
            placeholder.enableWordWrapping = multiline;
            placeholder.fontStyle = FontStyles.Italic;
            var text = AddText(area, "Text", 0f, 0f, 0f, 0f, "", 28f, Theme.Text, align);
            Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
            text.enableWordWrapping = multiline;
            text.richText = false;

            var field = rect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = bg;
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = placeholder;
            if (Theme.Font != null) {
                field.fontAsset = Theme.Font;
            }
            field.pointSize = 28f;
            field.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            field.characterLimit = multiline ? 4000 : 0;
            field.richText = false;
            field.onFocusSelectAll = false;
            field.restoreOriginalTextOnEscape = false;
            field.caretColor = Theme.Accent;
            field.customCaretColor = true;
            field.caretWidth = 3;
            field.selectionColor = new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.35f);
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            if (multiline) {
                return field;
            }
            field.onSubmit.AddListener(_ => Search(true));
            field.onValueChanged.AddListener(_ => {
                acPending = true;
                acDue = Time.unscaledTime + 0.45f;
            });
            return field;
        }

        private void BuildStatusBar(RectTransform parent) {
            statusText = AddText(parent, "Status", Pad + 6f, Pad + TopH + 2f, W - 520f, StatusH, "", 24f, Theme.TextDim,
                TextAlignmentOptions.MidlineLeft);
            statusText.enableWordWrapping = false;
            statusText.overflowMode = TextOverflowModes.Ellipsis;
            hintText = AddText(parent, "Hint", W - 500f, Pad + TopH + 2f, 480f, StatusH, "", 24f, Theme.Accent,
                TextAlignmentOptions.MidlineRight);
            hintText.enableWordWrapping = false;
        }

        private void BuildBrowse(RectTransform parent) {
            var scroll = NewRect("Grid", parent);
            Stretch(scroll, Pad, 0f, Pad, Pad);
            var viewport = NewRect("Viewport", scroll);
            Stretch(viewport, 0f, 0f, 0f, 0f);
            AddImage(viewport, null, new Color(0f, 0f, 0f, 0.001f), sliced: false);
            viewport.gameObject.AddComponent<RectMask2D>();
            gridContent = NewRect("Content", viewport);
            gridContent.anchorMin = new Vector2(0f, 1f);
            gridContent.anchorMax = new Vector2(1f, 1f);
            gridContent.pivot = new Vector2(0.5f, 1f);
            gridContent.anchoredPosition = Vector2.zero;
            gridContent.sizeDelta = new Vector2(0f, 10f);
            gridScroll = scroll.gameObject.AddComponent<ScrollRect>();
            gridScroll.viewport = viewport;
            gridScroll.content = gridContent;
            gridScroll.horizontal = false;
            gridScroll.vertical = true;
            gridScroll.movementType = ScrollRect.MovementType.Clamped;
            gridScroll.scrollSensitivity = 60f;

            browseMessage = AddText(parent, "Message", 80f, 80f, W - 160f, 600f, "", 32f, Theme.Text, TextAlignmentOptions.Top);
        }

        private void BuildPlayer(RectTransform parent) {
            float bodyH = H - BodyY;
            float videoW = W - Pad * 2f - TagsW - Gap;
            float videoH = bodyH - ControlsH - Pad - Gap;

            normalVideoW = videoW;
            normalVideoH = videoH;
            videoArea = NewRect("VideoArea", parent);
            Place(videoArea, Pad, 0f, videoW, videoH);
            videoAreaBg = AddImage(videoArea, null, Color.black, sliced: false);
            var videoRect = NewRect("Video", videoArea);
            Stretch(videoRect, 0f, 0f, 0f, 0f);
            videoImage = videoRect.gameObject.AddComponent<RawImage>();
            videoImage.raycastTarget = false;
            videoImage.enabled = false;
            overlayText = AddText(videoArea, "Overlay", 40f, 0f, videoW - 80f, videoH, "", 32f, Theme.Text, TextAlignmentOptions.Center);
            Stretch(overlayText.rectTransform, 40f, 0f, 40f, 0f);

            // Click the picture to play/pause.
            var clickCatcher = NewRect("Click", videoArea);
            Stretch(clickCatcher, 0f, 0f, 0f, 0f);
            AddImage(clickCatcher, null, Clear, sliced: false);
            var catcherButton = clickCatcher.gameObject.AddComponent<Button>();
            catcherButton.transition = Selectable.Transition.None;
            catcherButton.navigation = new Navigation { mode = Navigation.Mode.None };
            catcherButton.onClick.AddListener(OnVideoClicked); // click: play/pause, double-click: immersive

            // Tags column.
            var side = NewRect("Side", parent);
            sidePanel = side;
            Place(side, Pad + videoW + Gap, 0f, TagsW, videoH);
            AddImage(side, Theme.PanelSprite, new Color(0f, 0f, 0f, 0.35f));
            infoText = AddText(side, "Info", 14f, 8f, TagsW - 28f, 64f, "", 22f, Theme.Text, TextAlignmentOptions.TopLeft);
            Btn(side, 14f, 76f, TagsW - 28f, 46f, "Open post in browser", null, () => {
                if (currentPost != null) {
                    Application.OpenURL(currentPost.PageUrl);
                }
            }, 20f);
            AddText(side, "TagsHint", 14f, 128f, TagsW - 28f, 30f, "Click a tag to add it, - to exclude it", 18f, Theme.TextDim,
                TextAlignmentOptions.MidlineLeft);

            var scroll = NewRect("Tags", side);
            Stretch(scroll, 8f, 162f, 8f, 8f);
            var viewport = NewRect("Viewport", scroll);
            Stretch(viewport, 0f, 0f, 0f, 0f);
            AddImage(viewport, null, new Color(0f, 0f, 0f, 0.001f), sliced: false);
            viewport.gameObject.AddComponent<RectMask2D>();
            tagContent = NewRect("Content", viewport);
            tagContent.anchorMin = new Vector2(0f, 1f);
            tagContent.anchorMax = new Vector2(1f, 1f);
            tagContent.pivot = new Vector2(0.5f, 1f);
            tagContent.anchoredPosition = Vector2.zero;
            tagScroll = scroll.gameObject.AddComponent<ScrollRect>();
            tagScroll.viewport = viewport;
            tagScroll.content = tagContent;
            tagScroll.horizontal = false;
            tagScroll.vertical = true;
            tagScroll.movementType = ScrollRect.MovementType.Clamped;
            tagScroll.scrollSensitivity = 50f;

            // Controls row.
            var bar = NewRect("Controls", parent);
            controlsBar = bar;
            Place(bar, Pad, videoH + Gap, W - Pad * 2f, ControlsH);
            controlsBg = AddImage(bar, Theme.PanelSprite, Clear); // shows behind the controls in immersive view
            float h = ControlsH, x = 0f;
            ownerOnly.Add(Btn(bar, x, 0f, 130f, h, "Back", null, BackToResults, 26f).Button.gameObject);
            x += 130f + Gap;
            ownerOnly.Add(Btn(bar, x, 0f, 64f, h, "|<", null, () => PlayIndex(current - 1), 26f).Button.gameObject);
            x += 64f + Gap;
            playButton = Btn(bar, x, 0f, 120f, h, "Pause", null, TogglePlay, 26f, Theme.TitleBar);
            x += 120f + Gap;
            ownerOnly.Add(Btn(bar, x, 0f, 64f, h, ">|", null, () => PlayIndex(current + 1), 26f).Button.gameObject);
            x += 64f + Gap;
            ownerOnly.Add(Btn(bar, x, 0f, 84f, h, "-10s", null, () => SeekBy(-10), 22f).Button.gameObject);
            x += 84f + Gap;
            ownerOnly.Add(Btn(bar, x, 0f, 84f, h, "+10s", null, () => SeekBy(10), 22f).Button.gameObject);
            x += 84f + Gap;
            immersiveButton = Btn(bar, x, 0f, 150f, h, "Immersive", null, () => SetImmersiveView(!immersive), 22f);
            x += 150f + Gap * 2f;

            float rightW = 110f + Gap + 70f + Gap + 150f;
            float seekW = W - Pad * 2f - x - rightW - 170f - Gap * 2f;
            seekFill = MakeBar(bar, x, 0f, seekW, h, SeekTo);
            x += seekW + Gap;
            timeText = AddText(bar, "Time", x, 0f, 170f, h, "0:00 / 0:00", 22f, Theme.Text, TextAlignmentOptions.Center);
            timeText.enableWordWrapping = false;
            x += 170f + Gap;
            loopButton = Btn(bar, x, 0f, 110f, h, "Loop", "ico_refresh", () => {
                MovieBlockPlugin.Loop.Value = !MovieBlockPlugin.Loop.Value;
                RefreshButtons();
            }, 22f);
            x += 110f + Gap;
            muteButton = Btn(bar, x, 0f, 70f, h, null, "ico_speaker", () => {
                muted = !muted;
                RefreshButtons();
            }, 22f);
            x += 70f + Gap;
            volumeFill = MakeBar(bar, x, 0f, W - Pad * 2f - x, h, v => MovieBlockPlugin.Volume.Value = (float)Math.Round(v, 2));
        }

        /// <summary>A click/drag bar. Returns the fill rect (its anchorMax.x is the value).</summary>
        private RectTransform MakeBar(RectTransform parent, float x, float y, float w, float h, Action<float> onValue) {
            var rect = NewRect("Bar", parent);
            Place(rect, x, y, w, h);
            AddImage(rect, null, Clear, sliced: false); // hit area: the whole row height
            var track = NewRect("Track", rect);
            track.anchorMin = new Vector2(0f, 0.5f);
            track.anchorMax = new Vector2(1f, 0.5f);
            track.sizeDelta = new Vector2(0f, 16f);
            track.anchoredPosition = Vector2.zero;
            AddImage(track, Theme.PanelSprite, new Color(1f, 1f, 1f, 0.15f)).raycastTarget = false;
            var fill = NewRect("Fill", track);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(0f, 1f);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
            AddImage(fill, Theme.PanelSprite, Theme.Accent).raycastTarget = false;
            rect.gameObject.AddComponent<ClickBar>().OnValue = onValue;
            return fill;
        }

        private void BuildSuggestions(RectTransform parent) {
            var inputRect = (RectTransform)input.transform;
            suggestBox = NewRect("Suggestions", parent);
            float x = Pad + inputRect.anchoredPosition.x;
            float w = inputRect.sizeDelta.x;
            Place(suggestBox, x, Pad + TopH + 4f, w, MaxSuggestions * 50f + 12f);
            AddImage(suggestBox, Theme.PanelSprite, new Color(0.06f, 0.07f, 0.09f, 0.98f));
            for (int i = 0; i < MaxSuggestions; i++) {
                int index = i;
                var row = Btn(suggestBox, 6f, 6f + i * 50f, w - 12f, 46f, "", null, () => AcceptSuggestion(index), 24f, CellBg);
                row.Label.alignment = TextAlignmentOptions.MidlineLeft;
                Place(row.Label.rectTransform, 16f, 0f, w - 44f, 46f);
                row.Label.richText = true;
                suggestRows.Add(row);
            }
            suggestBox.gameObject.SetActive(false);
        }

        private void BuildVideo() {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.dopplerLevel = 0f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.minDistance = 2f;
            audioSource.maxDistance = 30f;
            try {
                if (GameManager.instance != null && GameManager.instance.soundEffectGroup != null) {
                    audioSource.outputAudioMixerGroup = GameManager.instance.soundEffectGroup; // follows the game's SFX volume
                }
            } catch {
                // Mixer not available: plain output.
            }

            player = gameObject.AddComponent<VideoPlayer>();
            player.playOnAwake = false;
            player.waitForFirstFrame = true;
            player.skipOnDrop = true;
            player.source = VideoSource.Url;
            player.renderMode = VideoRenderMode.RenderTexture;
            player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            player.controlledAudioTrackCount = 1;
            player.EnableAudioTrack(0, true);
            player.SetTargetAudioSource(0, audioSource);
            player.prepareCompleted += OnPrepared;
            player.errorReceived += OnVideoError;
            player.loopPointReached += OnLoopPoint;
            ApplyAudio();
        }

        // ================================================================== immersive view

        private bool immersive;
        private RectTransform topBar, sidePanel, controlsBar;
        private Image controlsBg;
        private PanelButton immersiveButton;
        private float normalVideoW, normalVideoH;
        private float lastAspect = 16f / 9f;
        private float lastVideoClick = -1f;
        private float lastActivity;
        private const float ControlsHideAfter = 2.5f;

        public static bool IsImmersive => Instance != null && Instance.immersive;

        /// <summary>For the command: null on success, otherwise why it couldn't.</summary>
        public static string SetImmersive(bool on) {
            if (Instance == null) {
                return "No movie block. Use /movieblock e621 or /movieblock r34.";
            }
            if (on && !Instance.playerView.gameObject.activeSelf) {
                return "Play a video first.";
            }
            Instance.SetImmersiveView(on);
            return null;
        }

        /// <summary>
        /// Immersive view: the video fills the whole screen (no search bar, tags or status line), the block grows by
        /// ImmersiveScale and re-centres in front of you; exiting puts it back where it was. The controls float over the bottom while you move the mouse.
        /// </summary>
        private void SetImmersiveView(bool on) {
            if (on && (playerView == null || !playerView.gameObject.activeSelf)) {
                return;
            }
            if (on == immersive) {
                return;
            }
            immersive = on;
            topBar.gameObject.SetActive(!on);
            statusText.gameObject.SetActive(!on);
            hintText.gameObject.SetActive(!on);
            sidePanel.gameObject.SetActive(!on);
            // The curved strip replaces the flat panel, its rim, background and frame.
            bool curved = on && visuals != null;
            rimImage.enabled = !curved;
            screenBgImage.enabled = !curved;
            videoAreaBg.enabled = !curved;
            videoImage.color = curved ? Clear : Color.white;
            if (frameRenderer != null) {
                frameRenderer.enabled = !curved;
            }
            HideSuggestions();
            if (on) {
                Stretch(playerView, 0f, 0f, 0f, 0f);
                Stretch(videoArea, 0f, 0f, 0f, 0f);
                Place(controlsBar, Pad, H - 10f - ControlsH - Pad * 2f, W - Pad * 2f - 10f, ControlsH);
                controlsBg.color = new Color(0f, 0f, 0f, 0.7f);
                lastActivity = Time.unscaledTime;
            } else {
                Stretch(playerView, 0f, BodyY, 0f, 0f);
                Place(videoArea, Pad, 0f, normalVideoW, normalVideoH);
                Place(controlsBar, Pad, normalVideoH + Gap, W - Pad * 2f, ControlsH);
                controlsBg.color = Clear;
                controlsBar.gameObject.SetActive(true);
            }
            FitVideo(lastAspect);
            if (TestCamera == null && !IsRemote) {
                if (on) {
                    // The bigger screen re-centres in front of you; exiting puts it back where it was.
                    var before = (transform.position, transform.rotation);
                    PlaceInFront();
                    poseBeforeImmersive = before;
                } else if (poseBeforeImmersive.HasValue) {
                    transform.SetPositionAndRotation(poseBeforeImmersive.Value.position, poseBeforeImmersive.Value.rotation);
                    followMoving = false;
                }
            }
            if (!on) {
                poseBeforeImmersive = null;
            }
            RefreshButtons();
        }

        private (Vector3 position, Quaternion rotation)? poseBeforeImmersive;
        private ImmersiveVisuals visuals;
        private Image rimImage, screenBgImage, videoAreaBg;
        private Renderer frameRenderer;

        /// <summary>LateUpdate: after the camera has moved, keep the curve, the darkening and the controls in place.</summary>
        private void LateUpdate() {
            if (visuals == null) {
                return;
            }
            var cam = TestCamera != null ? TestCamera : Camera.main;
            bool darken = immersive && MovieBlockPlugin.DarkenSurroundings.Value;
            visuals.UpdateDarkness(cam, darken ? MovieBlockPlugin.Darkness.Value : 0f);

            // The dark curved canvas covers the whole screen; the video is fitted above a strip for the controls.
            float screenW = W - 10f, screenH = H - 10f;
            float stripH = ControlsH + 24f;
            float availH = screenH - stripH;
            float vw = screenW, vh = screenW / Mathf.Max(0.1f, lastAspect);
            if (vh > availH) {
                vh = availH;
                vw = availH * lastAspect;
            }
            // From behind, show only the dark back of the screen, not the picture mirrored.
            bool fromBehind = cam != null && Vector3.Dot(cam.transform.position - transform.position, transform.forward) > 0f;
            bool showVideo = videoImage.enabled && !fromBehind;
            visuals.UpdateCurve(immersive, showVideo, renderTexture, screenW, screenH, vw, vh, stripH / 2f,
                MovieBlockPlugin.CurveAngle.Value);
            if (immersive) {
                // Controls sit in the strip along the bottom of the curved canvas.
                Place(controlsBar, Pad, availH + 12f, W - Pad * 2f - 10f, ControlsH);
            }
        }

        private void OnVideoClicked() {
            float now = Time.unscaledTime;
            if (now - lastVideoClick < 0.35f) {
                // Double-click: undo the first click's play/pause and switch views instead.
                lastVideoClick = -1f;
                TogglePlay();
                SetImmersiveView(!immersive);
                return;
            }
            lastVideoClick = now;
            TogglePlay();
        }

        /// <summary>Per frame: in immersive view, show the controls only while the mouse is in use.</summary>
        private void UpdateImmersiveControls() {
            if (!immersive) {
                return;
            }
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (focused && mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 0.5f || mouse.leftButton.isPressed)) {
                lastActivity = Time.unscaledTime;
            }
            bool show = focused && Time.unscaledTime - lastActivity < ControlsHideAfter;
            if (controlsBar.gameObject.activeSelf != show) {
                controlsBar.gameObject.SetActive(show);
            }
        }

        // ================================================================== options view

        private const int MaxBlacklistTags = 100;

        private RectTransform optionsView;
        private TMP_InputField blacklistInput;
        private TextMeshProUGUI blacklistCount;
        private readonly List<Action> optionRefreshers = new List<Action>();
        private bool optionsDirty;      // something that changes search results was changed
        private bool optionsFromPlayer;
        private bool pausedForOptions;

        private void BuildOptions(RectTransform parent) {
            const float leftW = 960f, rowH = 60f;
            float rightX = Pad + leftW + 30f;
            float rightW = W - 10f - rightX - Pad;

            // ---- left: tag blacklist (like the sites' own blacklist page)
            var title = AddText(parent, "BlacklistTitle", Pad, 4f, leftW, 46f, "<b>Tag blacklist</b>", 32f, Theme.Text,
                TextAlignmentOptions.MidlineLeft);
            title.enableWordWrapping = false;
            AddText(parent, "BlacklistHelp", Pad, 52f, leftW, 64f,
                $"Any post with a blacklisted tag is hidden. Separate tags with spaces, e.g. <noparse>vrchat gore scat</noparse>. " +
                $"Max {MaxBlacklistTags} tags.", 22f, Theme.TextDim, TextAlignmentOptions.TopLeft);
            blacklistInput = MakeInput(parent, Pad, 122f, leftW, 430f, "No tags blacklisted", multiline: true);
            blacklistInput.onValueChanged.AddListener(_ => UpdateBlacklistCount());
            blacklistCount = AddText(parent, "Count", Pad, 556f, leftW, 34f, "", 20f, Theme.TextDim, TextAlignmentOptions.MidlineRight);
            AddText(parent, "BlacklistNote", Pad, 556f, leftW - 180f, 80f,
                "Content involving minors is always hidden. Your blacklist on the e621 or rule34 website doesn't carry over: " +
                "the sites only apply it in their own pages, not to the API this screen uses.", 20f, Theme.TextDim,
                TextAlignmentOptions.TopLeft);
            Btn(parent, Pad, 650f, 220f, 70f, "Save", "ico_save", () => CloseOptions(save: true), 26f, Theme.TitleBar);
            Btn(parent, Pad + 220f + Gap, 650f, 160f, 70f, "Clear", "ico_trashcan", () => blacklistInput.text = "", 24f);
            Btn(parent, Pad + 380f + Gap * 2f, 650f, 160f, 70f, "Cancel", null, () => CloseOptions(save: false), 24f,
                new Color(Theme.Cancel.r, Theme.Cancel.g, Theme.Cancel.b, 0.6f));

            AddText(parent, "MultiplayerTitle", Pad, 736f, leftW, 40f, "<b>Multiplayer</b>  <size=20><color=#" +
                Theme.Hex(Theme.TextDim) + ">(online rooms, players with Movie Block)</color></size>", 26f, Theme.Text, TextAlignmentOptions.MidlineLeft);
            float half = (leftW - Gap) / 2f;
            ToggleRow(parent, Pad, 780f, half, "Share my screen", MovieBlockPlugin.Share, affectsSearch: false);
            ToggleRow(parent, Pad + half + Gap, 780f, half, "Show others' screens", MovieBlockPlugin.ShowOthers, affectsSearch: false);

            // ---- right: the screen's other settings (they apply straight away)
            AddText(parent, "SettingsTitle", rightX, 4f, rightW, 46f, "<b>Settings</b>", 32f, Theme.Text, TextAlignmentOptions.MidlineLeft);
            float y = 58f;
            ToggleRow(parent, rightX, y, rightW, "Show AI-generated content", MovieBlockPlugin.ShowAI, affectsSearch: true);
            y += rowH + Gap;
            ToggleRow(parent, rightX, y, rightW, "Loop videos", MovieBlockPlugin.Loop, affectsSearch: false);
            y += rowH + Gap;
            ToggleRow(parent, rightX, y, rightW, "Play next when a video ends", MovieBlockPlugin.AutoNext, affectsSearch: false);
            y += rowH + Gap;
            ToggleRow(parent, rightX, y, rightW, "3D sound from the screen", MovieBlockPlugin.SpatialAudio, affectsSearch: false);
            y += rowH + Gap;
            ToggleRow(parent, rightX, y, rightW, "Follow me", MovieBlockPlugin.Follow, affectsSearch: false);
            y += rowH + Gap;
            ToggleRow(parent, rightX, y, rightW, "Turn towards me", MovieBlockPlugin.FaceCamera, affectsSearch: false);
            y += rowH + Gap;
            ToggleRow(parent, rightX, y, rightW, "Darken around immersive view", MovieBlockPlugin.DarkenSurroundings, affectsSearch: false);
            y += rowH + Gap * 3f;

            AddText(parent, "QualityTitle", rightX, y, rightW, 36f, "e621 video quality", 24f, Theme.Text, TextAlignmentOptions.MidlineLeft);
            y += 40f;
            float qw = (rightW - Gap * 2f) / 3f;
            string[] qualities = { "480p", "720p", "original" };
            for (int i = 0; i < qualities.Length; i++) {
                string q = qualities[i];
                var b = Btn(parent, rightX + i * (qw + Gap), y, qw, rowH, i == 2 ? "Original" : q, null, () => {
                    if (MovieBlockPlugin.Quality.Value != q) {
                        MovieBlockPlugin.Quality.Value = q;
                        optionsDirty = true;
                    }
                    RefreshOptions();
                }, 24f, CellBg);
                optionRefreshers.Add(() => b.Background.color = MovieBlockPlugin.Quality.Value == q
                    ? new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.45f) : CellBg);
            }
            y += rowH + Gap * 2f;
            AddText(parent, "SettingsNote", rightX, y, rightW, 60f,
                "Settings save as you change them. The search runs again when you leave this page if the results would change.",
                20f, Theme.TextDim, TextAlignmentOptions.TopLeft);
        }

        private void ToggleRow(RectTransform parent, float x, float y, float w, string label,
                               BepInEx.Configuration.ConfigEntry<bool> entry, bool affectsSearch) {
            PanelButton row = null;
            row = Btn(parent, x, y, w, 60f, label, null, () => {
                entry.Value = !entry.Value;
                if (affectsSearch) {
                    optionsDirty = true;
                }
                RefreshOptions();
            }, 24f, CellBg);
            row.Label.alignment = TextAlignmentOptions.MidlineLeft;
            Place(row.Label.rectTransform, 18f, 0f, w - 130f, 60f);
            var state = AddText((RectTransform)row.Button.transform, "State", w - 112f, 0f, 96f, 60f, "", 22f, Theme.Text,
                TextAlignmentOptions.MidlineRight);
            optionRefreshers.Add(() => {
                bool on = entry.Value;
                row.Background.color = on ? new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.45f) : CellBg;
                state.text = on ? "<b>On</b>" : "Off";
            });
        }

        private void RefreshOptions() {
            foreach (var refresh in optionRefreshers) {
                refresh();
            }
        }

        private void UpdateBlacklistCount() {
            int count = SplitTags(blacklistInput.text).Count;
            blacklistCount.text = count > MaxBlacklistTags
                ? $"{Theme.Red}{count} / {MaxBlacklistTags} tags (extra ones are dropped)</color>"
                : $"{count} / {MaxBlacklistTags} tags";
        }

        private static List<string> SplitTags(string text) =>
            (text ?? "").Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim().TrimStart('-').ToLowerInvariant())
                        .Where(t => t.Length > 0)
                        .Distinct()
                        .ToList();

        private void ToggleOptions() {
            if (optionsView.gameObject.activeSelf) {
                CloseOptions(save: true);
            } else {
                OpenOptions();
            }
        }

        private void OpenOptions() {
            SetImmersiveView(false);
            optionsFromPlayer = playerView.gameObject.activeSelf;
            pausedForOptions = false;
            if (optionsFromPlayer && player != null && player.isPlaying) {
                player.Pause();
                pausedForOptions = true;
            }
            HideSuggestions();
            browseView.gameObject.SetActive(false);
            playerView.gameObject.SetActive(false);
            optionsView.gameObject.SetActive(true);
            blacklistInput.SetTextWithoutNotify(string.Join(" ", SplitTags(MovieBlockPlugin.Blacklist.Value).ToArray()));
            UpdateBlacklistCount();
            optionsDirty = false;
            RefreshOptions();
            RefreshButtons();
        }

        private void CloseOptions(bool save) {
            if (save) {
                string list = string.Join(" ", SplitTags(blacklistInput.text).Take(MaxBlacklistTags).ToArray());
                if (list != string.Join(" ", SplitTags(MovieBlockPlugin.Blacklist.Value).ToArray())) {
                    MovieBlockPlugin.Blacklist.Value = list;
                    optionsDirty = true;
                }
            }
            if (blacklistInput.isFocused) {
                blacklistInput.DeactivateInputField();
            }
            optionsView.gameObject.SetActive(false);
            if (optionsDirty) {
                optionsDirty = false;
                Search(true); // results change: back to the grid with a fresh search
            } else if (optionsFromPlayer) {
                playerView.gameObject.SetActive(true);
                if (pausedForOptions && player != null) {
                    player.Play();
                }
            } else {
                browseView.gameObject.SetActive(true);
            }
            RefreshButtons();
        }

        // ================================================================== placement

        private float shownSize;

        /// <summary>Screen width in metres right now: bigger in immersive view.</summary>
        private float TargetSize =>
            (IsRemote ? Mathf.Clamp(netSize, 0.5f, 15f) : Mathf.Clamp(MovieBlockPlugin.Size.Value, 0.5f, 10f)) *
            (immersive ? Mathf.Clamp(MovieBlockPlugin.ImmersiveScale.Value, 1f, 4f) : 1f);

        private void ApplySize() {
            float target = TargetSize;
            shownSize = shownSize <= 0f ? target : Mathf.Lerp(shownSize, target, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
            if (Mathf.Abs(shownSize - target) < 0.001f) {
                shownSize = target;
            }
            float scale = shownSize / W;
            transform.localScale = new Vector3(scale, scale, scale);
        }

        private bool IdealPose(out Vector3 position) {
            position = transform.position;
            var kobold = ModKit.LocalKobold;
            var cam = Camera.main;
            if (kobold == null) {
                return false;
            }
            Transform body = kobold.hip != null ? kobold.hip : kobold.transform;
            Vector3 forward = cam != null ? cam.transform.forward : kobold.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) {
                forward = Vector3.forward;
            }
            forward.Normalize();
            position = body.position + forward * Mathf.Clamp(MovieBlockPlugin.Distance.Value, 0.5f, 20f);
            float eye = cam != null ? cam.transform.position.y : body.position.y + 0.5f;
            position.y = Mathf.Max(eye, body.position.y + TargetSize * 0.3f);
            return true;
        }

        private void PlaceInFront() {
            if (!IdealPose(out var position)) {
                return;
            }
            transform.position = position;
            transform.rotation = FacingRotation(position);
            followMoving = false;
            poseBeforeImmersive = null; // moved on purpose: exiting immersive view leaves it here
        }

        private static Quaternion FacingRotation(Vector3 position) {
            var cam = Camera.main;
            Vector3 dir = cam != null ? position - cam.transform.position : Vector3.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) {
                dir = Vector3.forward;
            }
            // A world-space canvas reads correctly when viewed along its +Z.
            return Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        private void UpdatePlacement() {
            float dt = Time.unscaledDeltaTime;
            if (MovieBlockPlugin.Follow.Value && !focused && IdealPose(out var ideal)) {
                // Lazy follow: drift back in front only once it's well off to the side or far away.
                var cam = Camera.main;
                bool far = (transform.position - ideal).magnitude > 1.5f;
                bool offView = false;
                if (cam != null) {
                    Vector3 toScreen = transform.position - cam.transform.position;
                    toScreen.y = 0f;
                    Vector3 look = cam.transform.forward;
                    look.y = 0f;
                    offView = Vector3.Angle(toScreen, look) > 50f;
                }
                if (far || offView) {
                    followMoving = true;
                    poseBeforeImmersive = null; // it followed you; the old spot is stale
                }
                if (followMoving) {
                    followTarget = ideal;
                    transform.position = Vector3.Lerp(transform.position, followTarget, 1f - Mathf.Exp(-4f * dt));
                    if ((transform.position - followTarget).magnitude < 0.05f) {
                        followMoving = false;
                    }
                }
            }
            if (MovieBlockPlugin.FaceCamera.Value) {
                transform.rotation = Quaternion.Slerp(transform.rotation, FacingRotation(transform.position), 1f - Mathf.Exp(-6f * dt));
            }
        }

        // ================================================================== focus

        private void RequestFocus() {
            // Called from a chat command: the chat closes after the command runs and re-locks the cursor,
            // so wait until it is gone.
            pendingFocus = true;
            pendingFocusAt = Time.unscaledTime + 0.15f;
            pendingFocusUntil = Time.unscaledTime + 5f;
        }

        private void SetFocus(bool on) {
            if (on == focused) {
                return;
            }
            focused = on;
            if (on) {
                FocusedScreen = this;
            } else if (FocusedScreen == this) {
                FocusedScreen = null;
            }
            var controls = GameManager.GetPlayerControls();
            if (on) {
                GameManager.SetControlsActive(false);
                OrbitCamera.SetTracking(false);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                // Chat (Enter/T) and equipment live in the UI action map, which stays on: mute them while
                // the screen has the keyboard so typing tags doesn't open chat.
                controls.UI.Chat.Disable();
                controls.UI.Pause.Disable(); // Esc leaves the screen instead of pausing
                controls.UI.ViewStats.Disable();
            } else {
                controls.UI.Chat.Enable();
                controls.UI.Pause.Enable();
                controls.UI.ViewStats.Enable();
                HideSuggestions();
                if (input != null && input.isFocused) {
                    input.DeactivateInputField();
                }
                if (EventSystem.current != null) {
                    EventSystem.current.SetSelectedGameObject(null);
                }
                GameManager.SetControlsActive(true);
                if (MainMenu.GetCurrentMode() == MainMenu.MainMenuMode.None && !Pauser.GetPaused() && !ModPanel.IsOpen) {
                    OrbitCamera.SetTracking(true);
                    Cursor.lockState = CursorLockMode.Locked;
                }
            }
            RefreshHint();
        }

        private void UpdateFocus() {
            if (pendingFocus && Time.unscaledTime >= pendingFocusAt) {
                if (MenusClosed()) {
                    pendingFocus = false;
                    SetFocus(true);
                } else if (Time.unscaledTime > pendingFocusUntil) {
                    pendingFocus = false;
                }
            }
            var mouse = UnityEngine.InputSystem.Mouse.current;
            bool clicked = mouse != null && mouse.leftButton.wasPressedThisFrame;
            var cam = Camera.main;

            if (!focused) {
                if (FocusedScreen != null && FocusedScreen != this) {
                    aimed = false; // another screen has the mouse
                    return;
                }
                // Playing: is the crosshair (screen centre) on the screen? Then a click starts using it.
                aimed = !pendingFocus && cam != null && Cursor.lockState == CursorLockMode.Locked && MenusClosed() &&
                        HitsScreen(cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)), ClickReach);
                if (aimed && clicked) {
                    SetFocus(true);
                    focusedFrame = Time.frameCount;
                }
                return;
            }
            aimed = false;
            // Another menu (pause, mod panel...) took over: step aside and let it own the cursor.
            if (!MenusClosed()) {
                SetFocus(false);
                return;
            }
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) {
                SetFocus(false);
                return;
            }
            // Clicking anywhere off the screen goes back to the game.
            if (clicked && Time.frameCount != focusedFrame && cam != null &&
                !HitsScreen(cam.ScreenPointToRay(mouse.position.ReadValue()), float.MaxValue)) {
                SetFocus(false);
                return;
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private const float ClickReach = 12f;
        private bool aimed;
        private int focusedFrame = -1;

        /// <summary>True if <paramref name="ray"/> meets the screen's rectangle within <paramref name="maxDistance"/> metres.</summary>
        private bool HitsScreen(Ray ray, float maxDistance) {
            var plane = new Plane(transform.forward, transform.position);
            if (!plane.Raycast(ray, out float distance) || distance > maxDistance) {
                return false;
            }
            Vector3 local = transform.InverseTransformPoint(ray.GetPoint(distance)); // canvas units, pivot in the middle
            return Mathf.Abs(local.x) <= W / 2f + 25f && Mathf.Abs(local.y) <= H / 2f + 25f; // + the frame's border
        }

        private void RefreshHint() {
            if (hintText == null) {
                return;
            }
            string key = MovieBlockPlugin.FocusKey.Value;
            hintText.text = focused ? $"Esc, {key} or click away: back to the game" : $"Click or {key}: use the screen";
            if (banner != null) {
                banner.gameObject.SetActive(!focused && !immersive && TestCamera == null);
                bannerText.text = pendingFocus ? "Opening..."
                    : aimed ? "<b>Click</b> to use the screen"
                    : $"Click the screen or press <b>{key}</b> to use it";
            }
        }

        // ================================================================== searching

        private void SwitchSource() {
            var next = source is E621Source ? (Source)new Rule34Source() : new E621Source();
            SetSource(next);
        }

        private void SetSource(Source src) {
            source = src;
            sort = 0;
            page = 0;
            StopVideo();
            input.SetTextWithoutNotify(src.DefaultTags ?? "");
            RefreshButtons();
            Search(true);
        }

        private void CycleSort() {
            sort = (sort + 1) % source.Sorts.Length;
            RefreshButtons();
            Search(true);
        }

        private void ChangePage(int delta) {
            int next = page + delta;
            if (next < 0 || (delta > 0 && lastPage)) {
                return;
            }
            page = next;
            Search(false);
        }

        private void Search(bool resetPage) {
            if (resetPage) {
                page = 0;
            }
            HideSuggestions();
            acPending = false;
            acGen++;
            ShowPlayer(false);
            int gen = ++searchGen;
            string query = input.text.Trim();
            string problem = source.SetupProblem;
            if (problem != null) {
                ClearGrid();
                browseMessage.text = problem;
                SetStatus($"{source.Title}: setup needed");
                RefreshButtons();
                return;
            }
            browseMessage.text = "";
            SetStatus($"Searching {source.Title} for \"{query}\"...");
            source.Search(query, sort, page, result => {
                if (this == null || gen != searchGen) {
                    return;
                }
                OnResults(result, query);
            });
        }

        private void OnResults(SearchResult result, string query) {
            ClearGrid();
            if (result.Error != null) {
                lastPage = true;
                browseMessage.text = $"{Theme.Red}Couldn't search {source.Title}:</color>\n{result.Error}";
                SetStatus($"{source.Title}: error");
                RefreshButtons();
                return;
            }
            posts = result.Posts;
            lastPage = result.LastPage;
            current = -1;
            string hidden = result.Hidden > 0 ? $" ({result.Hidden} hidden: blocked tag or unplayable format)" : "";
            SetStatus($"{source.Title} \"{query}\": page {page + 1}, {posts.Count} videos{hidden}");
            if (posts.Count == 0) {
                browseMessage.text = page == 0 ? $"No videos found for \"{query}\"." : "No more results.";
            }
            BuildGrid();
            RefreshButtons();
        }

        private void ClearGrid() {
            thumbQueue.Clear();
            for (int i = gridContent.childCount - 1; i >= 0; i--) {
                Destroy(gridContent.GetChild(i).gameObject);
            }
            foreach (var t in thumbs) {
                if (t != null) {
                    Destroy(t);
                }
            }
            thumbs.Clear();
            gridContent.sizeDelta = new Vector2(0f, 10f);
            gridContent.anchoredPosition = Vector2.zero;
        }

        private void BuildGrid() {
            float width = W - Pad * 2f;
            float cw = (width - (Columns - 1) * Gap * 1.5f) / Columns;
            float ch = cw * 0.66f;
            for (int i = 0; i < posts.Count; i++) {
                int index = i;
                var post = posts[i];
                float x = (i % Columns) * (cw + Gap * 1.5f);
                float y = (i / Columns) * (ch + Gap * 1.5f);
                var cell = MakeButton(gridContent, "Post", x, y, cw, ch, Theme.PanelSprite, CellBg, null, null, 0f, () => PlayIndex(index));
                var thumbRect = NewRect("Thumb", (RectTransform)cell.Button.transform);
                Stretch(thumbRect, 4f, 4f, 4f, 4f);
                var thumb = thumbRect.gameObject.AddComponent<RawImage>();
                thumb.raycastTarget = false;
                thumb.color = new Color(1f, 1f, 1f, 0f);

                var band = NewRect("Band", (RectTransform)cell.Button.transform);
                band.anchorMin = new Vector2(0f, 0f);
                band.anchorMax = new Vector2(1f, 0f);
                band.pivot = new Vector2(0.5f, 0f);
                band.sizeDelta = new Vector2(-8f, 38f);
                band.anchoredPosition = new Vector2(0f, 4f);
                AddImage(band, null, new Color(0f, 0f, 0f, 0.65f), sliced: false).raycastTarget = false;
                string duration = post.Duration > 0 ? "   " + FormatTime(post.Duration) : "";
                var label = AddText(band, "Label", 10f, 0f, cw - 28f, 38f, $"<color=#ffd75e>{Signed(post.Score)}</color>{duration}   {post.Extension}",
                    20f, Theme.Text, TextAlignmentOptions.MidlineLeft);
                label.enableWordWrapping = false;

                if (!string.IsNullOrEmpty(post.PreviewUrl)) {
                    thumbQueue.Enqueue(new ThumbJob { Gen = searchGen, Url = post.PreviewUrl, Target = thumb, CellAspect = (cw - 8f) / (ch - 8f) });
                }
            }
            int rows = (posts.Count + Columns - 1) / Columns;
            gridContent.sizeDelta = new Vector2(0f, Mathf.Max(10f, rows * (ch + Gap * 1.5f)));
            gridScroll.verticalNormalizedPosition = 1f;
        }

        private void PumpThumbs() {
            while (thumbLoads < MaxThumbLoads && thumbQueue.Count > 0) {
                var job = thumbQueue.Dequeue();
                thumbLoads++;
                Net.Get(job.Url, bytes => bytes, bytes => {
                    thumbLoads--;
                    if (this == null || job.Gen != searchGen || job.Target == null) {
                        return;
                    }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(bytes)) {
                        Destroy(tex);
                        return;
                    }
                    tex.wrapMode = TextureWrapMode.Clamp;
                    thumbs.Add(tex);
                    job.Target.texture = tex;
                    job.Target.color = Color.white;
                    job.Target.uvRect = CoverRect(tex.width / (float)Mathf.Max(1, tex.height), job.CellAspect);
                }, _ => thumbLoads--, maxBytes: 4 * 1024 * 1024);
            }
        }

        /// <summary>uvRect that crops an image of <paramref name="imageAspect"/> to fill a cell of <paramref name="cellAspect"/>.</summary>
        private static Rect CoverRect(float imageAspect, float cellAspect) {
            if (imageAspect > cellAspect) {
                float w = cellAspect / imageAspect;
                return new Rect((1f - w) / 2f, 0f, w, 1f);
            }
            float h = imageAspect / cellAspect;
            return new Rect(0f, (1f - h) / 2f, 1f, h);
        }

        // ------------------------------------------------------------------ autocomplete

        private static string LastWord(string text) {
            int space = text.LastIndexOf(' ');
            return space >= 0 ? text.Substring(space + 1) : text;
        }

        private void UpdateAutocomplete() {
            if (!acPending || Time.unscaledTime < acDue) {
                return;
            }
            acPending = false;
            if (!input.isFocused) {
                return;
            }
            string prefix = LastWord(input.text).TrimStart('-', '~');
            if (prefix.Length < 2 || prefix.Contains(":")) {
                HideSuggestions();
                return;
            }
            int gen = ++acGen;
            source.Autocomplete(prefix, list => {
                if (this == null || gen != acGen) {
                    return;
                }
                ShowSuggestions(list);
            });
        }

        private void ShowSuggestions(List<TagSuggestion> list) {
            suggestNames.Clear();
            var blocked = new HashSet<string>(new[] { "young", "loli", "shota", "cub", "child", "toddler" });
            foreach (var s in list) {
                if (string.IsNullOrEmpty(s.Name) || blocked.Contains(s.Name)) {
                    continue;
                }
                if (suggestNames.Count >= MaxSuggestions) {
                    break;
                }
                // Tag names come from the site: strip anything that could act as a rich-text tag.
                string shown = s.Name.Replace("<", "").Replace(">", "");
                suggestRows[suggestNames.Count].SetLabel($"{shown}  <color=#{Theme.Hex(Theme.TextDim)}>{s.Count:N0}</color>");
                suggestNames.Add(s.Name);
            }
            for (int i = 0; i < suggestRows.Count; i++) {
                suggestRows[i].Button.gameObject.SetActive(i < suggestNames.Count);
            }
            if (suggestNames.Count == 0 || !focused) {
                HideSuggestions();
                return;
            }
            var rect = suggestBox;
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, suggestNames.Count * 50f + 12f);
            suggestBox.gameObject.SetActive(true);
        }

        private void HideSuggestions() {
            if (suggestBox != null) {
                suggestBox.gameObject.SetActive(false);
            }
        }

        private void AcceptSuggestion(int index) {
            if (index < 0 || index >= suggestNames.Count) {
                return;
            }
            string text = input.text;
            int space = text.LastIndexOf(' ');
            string head = space >= 0 ? text.Substring(0, space + 1) : "";
            string word = space >= 0 ? text.Substring(space + 1) : text;
            string prefix = word.StartsWith("-") ? "-" : word.StartsWith("~") ? "~" : "";
            input.SetTextWithoutNotify(head + prefix + suggestNames[index] + " ");
            HideSuggestions();
            acPending = false;
            acGen++;
            input.ActivateInputField();
            caretToEndFrames = 3;
        }

        private void AddTag(string tag, bool exclude) {
            var words = input.text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                             .Where(w => w != tag && w != "-" + tag)
                             .ToList();
            words.Add(exclude ? "-" + tag : tag);
            input.SetTextWithoutNotify(string.Join(" ", words.ToArray()));
            Search(true);
        }

        // ================================================================== playing

        private void ShowPlayer(bool show) {
            if (!show) {
                SetImmersiveView(false);
            }
            optionsView.gameObject.SetActive(false);
            browseView.gameObject.SetActive(!show);
            playerView.gameObject.SetActive(show);
            if (!show) {
                StopVideo();
                current = -1;
            }
        }

        private void BackToResults() => ShowPlayer(false);

        private void PlayIndex(int index) {
            if (index < 0 || index >= posts.Count) {
                return;
            }
            current = index;
            PlayPost(posts[index], $"{index + 1} of {posts.Count} on page {page + 1}");
        }

        private Post currentPost;

        /// <summary>Shows the player and downloads/plays <paramref name="post"/>.</summary>
        private void PlayPost(Post post, string subtitle) {
            HideSuggestions();
            StopVideo();
            currentPost = post;
            optionsView.gameObject.SetActive(false);
            browseView.gameObject.SetActive(false);
            playerView.gameObject.SetActive(true);
            BuildTags(post);
            string size = post.Width > 0 ? $"   {post.Width}x{post.Height}" : "";
            string duration = post.Duration > 0 ? "   " + FormatTime(post.Duration) : "";
            infoText.text = $"<b>#{post.Id}</b>   score <color=#ffd75e>{Signed(post.Score)}</color>{duration}{size}\n" +
                            $"<color=#{Theme.Hex(Theme.TextDim)}>{subtitle}</color>";
            SetStatus($"{source.Title} #{post.Id}");

            string path = VideoCache.PathFor(source.Key, post);
            int gen = ++playGen;
            if (File.Exists(path)) {
                VideoCache.Touch(path);
                StartPlayback(path);
                return;
            }
            VideoCache.Trim(playingPath);
            overlayText.text = "Downloading...";
            download = Net.Download(post.VideoUrl, path, post.Size, () => {
                if (this == null || gen != playGen) {
                    return;
                }
                download = null;
                StartPlayback(path);
            }, error => {
                if (this == null || gen != playGen) {
                    return;
                }
                download = null;
                overlayText.text = $"{Theme.Red}Download failed:</color> {error}";
            });
        }

        private void BuildTags(Post post) {
            for (int i = tagContent.childCount - 1; i >= 0; i--) {
                Destroy(tagContent.GetChild(i).gameObject);
            }
            if (IsRemote) {
                BuildTagLabels(post); // someone else's screen: tags are just shown, not clickable
                return;
            }
            float w = TagsW - 16f;
            float y = 0f;
            foreach (var tag in post.Tags.Take(120)) {
                string t = tag;
                var add = Btn(tagContent, 0f, y, w - 52f, 40f, t, null, () => AddTag(t, false), 20f, CellBg);
                add.Label.alignment = TextAlignmentOptions.MidlineLeft;
                add.Label.richText = false; // tag names come from the site
                Place(add.Label.rectTransform, 12f, 0f, w - 76f, 40f);
                Btn(tagContent, w - 46f, y, 46f, 40f, "-", null, () => AddTag(t, true), 26f, new Color(0.45f, 0.15f, 0.15f, 0.8f));
                y += 44f;
            }
            tagContent.sizeDelta = new Vector2(0f, Mathf.Max(10f, y));
            tagContent.anchoredPosition = Vector2.zero;
        }

        private void StartPlayback(string path) {
            playingPath = path;
            overlayText.text = "Loading...";
            videoImage.enabled = false;
            player.isLooping = MovieBlockPlugin.Loop.Value;
            player.url = path;
            player.Prepare();
        }

        private void StopVideo() {
            download?.Cancel();
            download = null;
            playGen++;
            if (player != null) {
                player.Stop();
            }
            if (videoImage != null) {
                videoImage.enabled = false;
            }
            if (overlayText != null) {
                overlayText.text = "";
            }
        }

        private void OnPrepared(VideoPlayer vp) {
            if (this == null || !playerView.gameObject.activeSelf) {
                return;
            }
            int w = (int)vp.width, h = (int)vp.height;
            if (w <= 0 || h <= 0) {
                w = 1280;
                h = 720;
            }
            float shrink = Mathf.Min(1f, 1920f / Mathf.Max(w, h));
            int rw = Mathf.Max(16, Mathf.RoundToInt(w * shrink));
            int rh = Mathf.Max(16, Mathf.RoundToInt(h * shrink));
            if (renderTexture == null || renderTexture.width != rw || renderTexture.height != rh) {
                if (renderTexture != null) {
                    vp.targetTexture = null;
                    renderTexture.Release();
                    Destroy(renderTexture);
                }
                renderTexture = new RenderTexture(rw, rh, 0, RenderTextureFormat.ARGB32);
                renderTexture.Create();
            }
            vp.targetTexture = renderTexture;
            videoImage.texture = renderTexture;
            lastAspect = w / (float)h;
            FitVideo(lastAspect);
            videoImage.enabled = true;
            overlayText.text = "";
            ApplyAudio();
            vp.Play();
            if (IsRemote) {
                OnRemotePrepared(); // jump to the owner's time, pause if they're paused
            }
            RefreshButtons();
        }

        private void FitVideo(float aspect) {
            var area = videoArea.rect;
            float aw = area.width, ah = area.height;
            float vw = aw, vh = aw / aspect;
            if (vh > ah) {
                vh = ah;
                vw = ah * aspect;
            }
            var rect = videoImage.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(vw, vh);
        }

        private void OnVideoError(VideoPlayer vp, string message) {
            MovieBlockPlugin.Log.LogWarning($"Video error ({playingPath}): {message}");
            if (this == null) {
                return;
            }
            videoImage.enabled = false;
            overlayText.text = $"{Theme.Red}Can't play this video.</color>\n{message}\n\nTry the next one, or /movieblock quality 480p.";
        }

        private void OnLoopPoint(VideoPlayer vp) {
            if (this == null || IsRemote || MovieBlockPlugin.Loop.Value || !MovieBlockPlugin.AutoNext.Value) {
                return;
            }
            if (current + 1 < posts.Count) {
                PlayIndex(current + 1);
            }
        }

        private void TogglePlay() {
            if (IsRemote) {
                return; // the owner decides; viewers follow
            }
            if (player == null || !player.isPrepared) {
                return;
            }
            if (player.isPlaying) {
                player.Pause();
            } else {
                player.Play();
            }
            RefreshButtons();
        }

        private void SeekBy(double seconds) {
            if (IsRemote) {
                return;
            }
            if (player == null || !player.isPrepared || !player.canSetTime) {
                return;
            }
            double length = VideoLength();
            player.time = Math.Max(0, Math.Min(length > 0 ? length - 0.1 : double.MaxValue, player.time + seconds));
        }

        private void SeekTo(float t) {
            if (IsRemote) {
                return;
            }
            if (player == null || !player.isPrepared || !player.canSetTime) {
                return;
            }
            double length = VideoLength();
            if (length > 0) {
                player.time = Math.Min(length - 0.1, t * length);
            }
        }

        private double VideoLength() {
            if (player == null) {
                return 0;
            }
            if (player.length > 0) {
                return player.length;
            }
            return player.frameRate > 0 ? player.frameCount / player.frameRate : 0;
        }

        private void ApplyAudio() {
            if (audioSource == null) {
                return;
            }
            audioSource.volume = muted ? 0f : Mathf.Clamp01(MovieBlockPlugin.Volume.Value);
            audioSource.spatialBlend = MovieBlockPlugin.SpatialAudio.Value ? 1f : 0f;
        }

        // ================================================================== per frame

        private void Update() {
            if (TestCamera != null) {
                canvas.worldCamera = TestCamera;
            } else {
                if (!ModKit.InLevel) {
                    return;
                }
                if (canvas.worldCamera == null || canvas.worldCamera != Camera.main) {
                    canvas.worldCamera = Camera.main;
                }
                ApplySize();
                if (IsRemote) {
                    UpdateRemote(); // placed and timed by its owner
                } else {
                    UpdatePlacement();
                }
                UpdateFocus();
            }
            UpdateAutocomplete();
            PumpThumbs();
            UpdateImmersiveControls();
            ApplyAudio();
            if (player != null && player.isLooping != MovieBlockPlugin.Loop.Value) {
                player.isLooping = MovieBlockPlugin.Loop.Value;
            }

            if (caretToEndFrames > 0 && --caretToEndFrames == 0 && input.isFocused) {
                input.MoveTextEnd(false);
            }
            if (suggestBox.gameObject.activeSelf && !input.isFocused && caretToEndFrames == 0 &&
                (EventSystem.current == null || EventSystem.current.currentSelectedGameObject == null ||
                 !EventSystem.current.currentSelectedGameObject.transform.IsChildOf(suggestBox))) {
                HideSuggestions();
            }

            if (playerView.gameObject.activeSelf) {
                if (download != null) {
                    float p = download.Progress;
                    double mb = download.Received / 1048576.0;
                    overlayText.text = p >= 0
                        ? $"Downloading... {p * 100f:0}%\n<size=24>{mb:0.0} / {download.Total / 1048576.0:0.0} MB</size>"
                        : $"Downloading... {mb:0.0} MB";
                }
                double length = VideoLength();
                double time = player.isPrepared ? player.time : 0;
                timeText.text = $"{FormatTime(time)} / {FormatTime(length)}";
                SetFill(seekFill, length > 0 ? (float)(time / length) : 0f);
            }
            SetFill(volumeFill, muted ? 0f : MovieBlockPlugin.Volume.Value);
            RefreshButtons();
        }

        private static void SetFill(RectTransform fill, float t) {
            if (fill != null) {
                fill.anchorMax = new Vector2(Mathf.Clamp01(t), 1f);
            }
        }

        private void RefreshButtons() {
            if (source == null) {
                return;
            }
            sourceButton.SetLabel(source.Title);
            sortButton.SetLabel(source.Sorts[Mathf.Clamp(sort, 0, source.Sorts.Length - 1)]);
            pageLabel.text = (page + 1).ToString();
            prevPageButton.Interactable = page > 0;
            nextPageButton.Interactable = !lastPage;
            SetToggleLook(optionsButton, optionsView != null && optionsView.gameObject.activeSelf);
            SetToggleLook(immersiveButton, immersive);
            immersiveButton.SetLabel(immersive ? "Exit immersive" : "Immersive");
            SetToggleLook(loopButton, MovieBlockPlugin.Loop.Value);
            SetToggleLook(muteButton, !muted);
            playButton.SetLabel(player != null && player.isPlaying ? "Pause" : "Play");
            RefreshHint();
        }

        private static void SetToggleLook(PanelButton button, bool on) {
            if (button != null) {
                button.Background.color = on ? new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.45f) : Theme.Button;
            }
        }

        private void SetStatus(string text) {
            if (statusText != null) {
                statusText.text = text;
            }
        }

        private static string Signed(int n) => n > 0 ? "+" + n : n.ToString();

        private static string FormatTime(double seconds) {
            if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) {
                return "0:00";
            }
            int s = (int)seconds;
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }

        private void OnDestroy() {
            if (focused) {
                // Removed while in use (closed or map unloaded): never leave the player frozen or chat muted.
                try {
                    var controls = GameManager.GetPlayerControls();
                    controls.UI.Chat.Enable();
                    controls.UI.Pause.Enable();
                    controls.UI.ViewStats.Enable();
                    GameManager.SetControlsActive(true);
                    if (MainMenu.GetCurrentMode() == MainMenu.MainMenuMode.None) {
                        Cursor.lockState = CursorLockMode.Locked;
                        OrbitCamera.SetTracking(true);
                    }
                } catch {
                    // Game shutting down.
                }
            }
            download?.Cancel();
            visuals?.Destroy();
            if (renderTexture != null) {
                renderTexture.Release();
                Destroy(renderTexture);
            }
            foreach (var t in thumbs) {
                if (t != null) {
                    Destroy(t);
                }
            }
            if (FocusedScreen == this) {
                FocusedScreen = null;
            }
            if (Instance == this) {
                Instance = null;
                MovieNet.SendClosed();
            }
            if (IsRemote && Remotes.TryGetValue(remoteActor, out var mine) && mine == this) {
                Remotes.Remove(remoteActor);
            }
        }
    }

    /// <summary>Click or drag along a bar to set a 0..1 value. Works on world-space canvases.</summary>
    internal class ClickBar : MonoBehaviour, IPointerDownHandler, IDragHandler {
        public Action<float> OnValue;

        public void OnPointerDown(PointerEventData eventData) => Handle(eventData);
        public void OnDrag(PointerEventData eventData) => Handle(eventData);

        private void Handle(PointerEventData eventData) {
            var rect = (RectTransform)transform;
            Camera cam = eventData.pressEventCamera;
            if (cam == null) {
                cam = eventData.enterEventCamera != null ? eventData.enterEventCamera : Camera.main;
            }
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, cam, out var local)) {
                OnValue?.Invoke(Mathf.Clamp01(Mathf.InverseLerp(rect.rect.xMin, rect.rect.xMax, local.x)));
            }
        }
    }

    /// <summary>Downloaded videos in %TEMP%\KoboldKare-MovieBlock, trimmed to the configured size.</summary>
    internal static class VideoCache {
        public static string Folder {
            get {
                string dir = Path.Combine(Path.GetTempPath(), "KoboldKare-MovieBlock");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string PathFor(string site, Post post) {
            // Only ever .mp4/.webm on disk, whatever the URL ends in.
            string ext = Path.GetExtension(new Uri(post.VideoUrl).AbsolutePath).ToLowerInvariant() == ".webm" ? ".webm" : ".mp4";
            // The URL picks the quality, so include a short hash of it.
            uint hash = 2166136261;
            foreach (char c in post.VideoUrl) {
                hash = (hash ^ c) * 16777619;
            }
            return Path.Combine(Folder, $"{site}_{post.Id}_{hash:x8}{ext}");
        }

        public static void Touch(string path) {
            try {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            } catch {
                // Read-only or in use; doesn't matter.
            }
        }

        public static void Trim(string keep) {
            try {
                long limit = (long)Math.Max(100, MovieBlockPlugin.MaxCacheMB.Value) * 1048576L;
                var files = new DirectoryInfo(Folder).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                long total = 0;
                foreach (var f in files) {
                    total += f.Length;
                    if (total > limit && f.FullName != keep) {
                        try {
                            f.Delete();
                        } catch {
                            // In use.
                        }
                    }
                }
            } catch (Exception e) {
                MovieBlockPlugin.Log.LogWarning("Cache trim failed: " + e.Message);
            }
        }

        public static long SizeBytes() {
            try {
                return new DirectoryInfo(Folder).GetFiles().Sum(f => f.Length);
            } catch {
                return 0;
            }
        }

        public static int Clear() {
            int n = 0;
            foreach (var f in new DirectoryInfo(Folder).GetFiles()) {
                try {
                    f.Delete();
                    n++;
                } catch {
                    // In use (the playing video).
                }
            }
            return n;
        }
    }
}
