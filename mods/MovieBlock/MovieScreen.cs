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
    internal class MovieScreen : MonoBehaviour {
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
        private PanelButton sourceButton, sortButton, followButton, prevPageButton, nextPageButton;
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
            AddImage(root, Theme.PanelSprite, new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.55f));
            var screen = NewRect("Screen", root);
            Stretch(screen, 5f, 5f, 5f, 5f);
            AddImage(screen, Theme.PanelSprite, ScreenBg);

            BuildTopBar(screen);
            BuildStatusBar(screen);

            browseView = NewRect("Browse", screen);
            Stretch(browseView, 0f, BodyY, 0f, 0f);
            BuildBrowse(browseView);

            playerView = NewRect("Player", screen);
            Stretch(playerView, 0f, BodyY, 0f, 0f);
            BuildPlayer(playerView);
            playerView.gameObject.SetActive(false);

            // Shown while the screen isn't in use, so it's obvious how to get back in.
            banner = NewRect("Banner", screen);
            Place(banner, (W - 760f) / 2f, H - 150f, 760f, 80f);
            AddImage(banner, Theme.PanelSprite, new Color(0f, 0f, 0f, 0.78f)).raycastTarget = false;
            bannerText = AddText(banner, "Text", 0f, 0f, 760f, 80f, "", 34f, Theme.Accent, TextAlignmentOptions.Center);

            BuildSuggestions(screen); // last: drawn on top

            BuildVideo();
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
            Place(bar, Pad, Pad, W - Pad * 2f - 10f, TopH);
            float h = TopH, x = 0f;

            sourceButton = Btn(bar, x, 0f, 140f, h, "e621", null, SwitchSource, 30f, Theme.TitleBar);
            x += 140f + Gap;

            // Right-hand group, laid out from the right edge.
            float right = W - Pad * 2f - 10f;
            right -= h;
            Btn(bar, right, 0f, h, h, null, "ico_x-out", Close, 26f, new Color(0.6f, 0.2f, 0.2f, 0.8f));
            right -= Gap + 130f;
            followButton = Btn(bar, right, 0f, 130f, h, "Follow", "ico_pinned", () => {
                MovieBlockPlugin.Follow.Value = !MovieBlockPlugin.Follow.Value;
                RefreshButtons();
            }, 24f);
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

        private TMP_InputField MakeInput(RectTransform parent, float x, float y, float w, float h) {
            var rect = NewRect("Tags", parent);
            Place(rect, x, y, w, h);
            var bg = AddRoleImage(rect, "control", Theme.PanelSprite, new Color(0f, 0f, 0f, 0.55f));
            var area = NewRect("TextArea", rect);
            Stretch(area, 18f, 8f, 18f, 8f);
            area.gameObject.AddComponent<RectMask2D>();
            var placeholder = AddText(area, "Placeholder", 0f, 0f, 0f, 0f, "tags, e.g. kobold solo -male", 28f, Theme.TextDim,
                TextAlignmentOptions.MidlineLeft);
            Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);
            placeholder.enableWordWrapping = false;
            placeholder.fontStyle = FontStyles.Italic;
            var text = AddText(area, "Text", 0f, 0f, 0f, 0f, "", 28f, Theme.Text, TextAlignmentOptions.MidlineLeft);
            Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
            text.enableWordWrapping = false;
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
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.richText = false;
            field.onFocusSelectAll = false;
            field.restoreOriginalTextOnEscape = false;
            field.caretColor = Theme.Accent;
            field.customCaretColor = true;
            field.caretWidth = 3;
            field.selectionColor = new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.35f);
            field.navigation = new Navigation { mode = Navigation.Mode.None };
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

            videoArea = NewRect("VideoArea", parent);
            Place(videoArea, Pad, 0f, videoW, videoH);
            AddImage(videoArea, null, Color.black, sliced: false);
            var videoRect = NewRect("Video", videoArea);
            Stretch(videoRect, 0f, 0f, 0f, 0f);
            videoImage = videoRect.gameObject.AddComponent<RawImage>();
            videoImage.raycastTarget = false;
            videoImage.enabled = false;
            overlayText = AddText(videoArea, "Overlay", 40f, 0f, videoW - 80f, videoH, "", 32f, Theme.Text, TextAlignmentOptions.Center);

            // Click the picture to play/pause.
            var clickCatcher = NewRect("Click", videoArea);
            Stretch(clickCatcher, 0f, 0f, 0f, 0f);
            AddImage(clickCatcher, null, Clear, sliced: false);
            var catcherButton = clickCatcher.gameObject.AddComponent<Button>();
            catcherButton.transition = Selectable.Transition.None;
            catcherButton.navigation = new Navigation { mode = Navigation.Mode.None };
            catcherButton.onClick.AddListener(TogglePlay);

            // Tags column.
            var side = NewRect("Side", parent);
            Place(side, Pad + videoW + Gap, 0f, TagsW, videoH);
            AddImage(side, Theme.PanelSprite, new Color(0f, 0f, 0f, 0.35f));
            infoText = AddText(side, "Info", 14f, 8f, TagsW - 28f, 64f, "", 22f, Theme.Text, TextAlignmentOptions.TopLeft);
            Btn(side, 14f, 76f, TagsW - 28f, 46f, "Open post in browser", null, () => {
                if (current >= 0 && current < posts.Count) {
                    Application.OpenURL(posts[current].PageUrl);
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
            Place(bar, Pad, videoH + Gap, W - Pad * 2f, ControlsH);
            float h = ControlsH, x = 0f;
            Btn(bar, x, 0f, 130f, h, "Back", null, BackToResults, 26f);
            x += 130f + Gap;
            Btn(bar, x, 0f, 64f, h, "|<", null, () => PlayIndex(current - 1), 26f);
            x += 64f + Gap;
            playButton = Btn(bar, x, 0f, 120f, h, "Pause", null, TogglePlay, 26f, Theme.TitleBar);
            x += 120f + Gap;
            Btn(bar, x, 0f, 64f, h, ">|", null, () => PlayIndex(current + 1), 26f);
            x += 64f + Gap;
            Btn(bar, x, 0f, 84f, h, "-10s", null, () => SeekBy(-10), 22f);
            x += 84f + Gap;
            Btn(bar, x, 0f, 84f, h, "+10s", null, () => SeekBy(10), 22f);
            x += 84f + Gap * 2f;

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

        // ================================================================== placement

        private void ApplySize() {
            float scale = Mathf.Clamp(MovieBlockPlugin.Size.Value, 0.5f, 10f) / W;
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
            position.y = Mathf.Max(eye, body.position.y + MovieBlockPlugin.Size.Value * 0.3f);
            return true;
        }

        private void PlaceInFront() {
            if (!IdealPose(out var position)) {
                return;
            }
            transform.position = position;
            transform.rotation = FacingRotation(position);
            followMoving = false;
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
            if (!focused) {
                return;
            }
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
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void RefreshHint() {
            if (hintText == null) {
                return;
            }
            string key = MovieBlockPlugin.FocusKey.Value;
            hintText.text = focused ? $"{key} or Esc: back to the game" : $"{key}: use the screen";
            if (banner != null) {
                banner.gameObject.SetActive(!focused && TestCamera == null);
                bannerText.text = pendingFocus ? "Opening..." : $"Press <b>{key}</b> to use the screen";
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
            HideSuggestions();
            StopVideo();
            current = index;
            var post = posts[index];
            browseView.gameObject.SetActive(false);
            playerView.gameObject.SetActive(true);
            BuildTags(post);
            string size = post.Width > 0 ? $"   {post.Width}x{post.Height}" : "";
            string duration = post.Duration > 0 ? "   " + FormatTime(post.Duration) : "";
            infoText.text = $"<b>#{post.Id}</b>   score <color=#ffd75e>{Signed(post.Score)}</color>{duration}{size}\n" +
                            $"<color=#{Theme.Hex(Theme.TextDim)}>{index + 1} of {posts.Count} on page {page + 1}</color>";
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
            FitVideo(w / (float)h);
            videoImage.enabled = true;
            overlayText.text = "";
            ApplyAudio();
            vp.Play();
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
            if (this == null || MovieBlockPlugin.Loop.Value || !MovieBlockPlugin.AutoNext.Value) {
                return;
            }
            if (current + 1 < posts.Count) {
                PlayIndex(current + 1);
            }
        }

        private void TogglePlay() {
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
            if (player == null || !player.isPrepared || !player.canSetTime) {
                return;
            }
            double length = VideoLength();
            player.time = Math.Max(0, Math.Min(length > 0 ? length - 0.1 : double.MaxValue, player.time + seconds));
        }

        private void SeekTo(float t) {
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
                UpdatePlacement();
                UpdateFocus();
            }
            UpdateAutocomplete();
            PumpThumbs();
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
            SetToggleLook(followButton, MovieBlockPlugin.Follow.Value);
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
            if (renderTexture != null) {
                renderTexture.Release();
                Destroy(renderTexture);
            }
            foreach (var t in thumbs) {
                if (t != null) {
                    Destroy(t);
                }
            }
            if (Instance == this) {
                Instance = null;
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
