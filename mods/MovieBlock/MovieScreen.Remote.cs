using System;
using System.Collections.Generic;
using System.Linq;
using KoboldKare.ModKit;
using Photon.Pun;
using TMPro;
using UnityEngine;
using static KoboldKare.ModKit.UiFactory;

namespace MovieBlock {
    /// <summary>
    /// Viewer copies: another player's shared screen, shown in your world where they put it. The video is downloaded
    /// and played locally, kept in step with the owner's play/pause/seek using the room's shared clock. Your own
    /// filters (blacklist, AI, minors) and volume apply; only the owner can change what's playing.
    /// </summary>
    internal partial class MovieScreen {
        /// <summary>Viewer copies by the owner's Photon actor number.</summary>
        internal static readonly Dictionary<int, MovieScreen> Remotes = new Dictionary<int, MovieScreen>();

        /// <summary>The screen that currently has the mouse (only one at a time).</summary>
        private static MovieScreen FocusedScreen;

        private readonly List<GameObject> ownerOnly = new List<GameObject>();

        internal bool IsRemote;
        private int remoteActor;
        private string remoteOwner = "";
        private ScreenState remoteState;
        private long remotePostId = -1;
        private string remoteUrl;
        private bool remoteReady;
        private bool remotePoseSet;
        private float netSize = 2.4f;
        private Vector3 netPosition;
        private Quaternion netRotation = Quaternion.identity;
        private float nextDriftCheck;

        // ================================================================== static API (MovieNet)

        public static void ApplyRemote(int actor, string owner, ScreenState state) {
            if (!ModKit.InLevel) {
                return;
            }
            if (!Remotes.TryGetValue(actor, out var screen) || screen == null) {
                Theme.Load();
                var go = new GameObject("MovieBlock_Shared_" + actor);
                go.SetActive(false);
                screen = go.AddComponent<MovieScreen>();
                screen.IsRemote = true;
                screen.remoteActor = actor;
                screen.remoteOwner = owner;
                screen.Build();
                screen.ConfigureViewer();
                Remotes[actor] = screen;
                go.SetActive(true);
                MovieBlockPlugin.Log.LogInfo($"Showing {owner}'s shared movie screen.");
            }
            screen.remoteOwner = owner;
            screen.OnRemoteState(state);
        }

        public static void RemoveRemote(int actor) {
            if (Remotes.TryGetValue(actor, out var screen)) {
                Remotes.Remove(actor);
                if (screen != null) {
                    Destroy(screen.gameObject);
                }
            }
        }

        public static void RemoveAllRemotes() {
            foreach (var actor in Remotes.Keys.ToList()) {
                RemoveRemote(actor);
            }
        }

        // ================================================================== owner side

        /// <summary>This (own) screen's state for viewers.</summary>
        internal ScreenState CaptureState() {
            var state = new ScreenState {
                Position = transform.position,
                Rotation = transform.rotation,
                Size = TargetSize,
                Loop = MovieBlockPlugin.Loop.Value,
            };
            if (playerView.gameObject.activeSelf && currentPost != null && source != null && !string.IsNullOrEmpty(currentPost.VideoUrl)) {
                state.HasVideo = true;
                state.Site = source.Key;
                state.PostId = currentPost.Id;
                state.Url = currentPost.VideoUrl;
                state.Tags = currentPost.Tags.Take(150).ToArray();
                state.Playing = player != null && player.isPlaying;
                state.Time = player != null && player.isPrepared ? Math.Max(0, player.time) : 0;
            }
            return state;
        }

        // ================================================================== viewer side

        /// <summary>A viewer copy shows only the player: no search, no Options, no changing what plays.</summary>
        private void ConfigureViewer() {
            topBar.gameObject.SetActive(false);
            browseView.gameObject.SetActive(false);
            optionsView.gameObject.SetActive(false);
            playerView.gameObject.SetActive(true);
            foreach (var go in ownerOnly) {
                go.SetActive(false);
            }
            loopButton?.Button.gameObject.SetActive(false);
            playButton?.Button.gameObject.SetActive(false);
            var seek = seekFill != null ? seekFill.parent?.parent?.GetComponent<ClickBar>() : null;
            if (seek != null) {
                seek.enabled = false; // the seek bar just shows the owner's position
            }
            overlayText.text = $"Waiting for {remoteOwner} to play a video...";
            SetStatus($"{remoteOwner}'s screen");
        }

        private void OnRemoteState(ScreenState state) {
            netSize = state.Size;
            netPosition = state.Position;
            netRotation = state.Rotation;
            if (!remotePoseSet) {
                remotePoseSet = true;
                transform.position = netPosition;
                transform.rotation = netRotation;
            }
            SetStatus($"{remoteOwner}'s screen");

            if (!state.HasVideo) {
                if (remotePostId != -1) {
                    StopVideo();
                    SetImmersiveView(false);
                    remotePostId = -1;
                    remoteUrl = null;
                    remoteReady = false;
                    currentPost = null;
                }
                remoteState = state;
                overlayText.text = $"{remoteOwner} is choosing a video...";
                return;
            }

            if (state.PostId != remotePostId || state.Url != remoteUrl) {
                remotePostId = state.PostId;
                remoteUrl = state.Url;
                remoteReady = false;
                source = state.Site == "r34" ? (Source)new Rule34Source() : new E621Source();
                if (Source.BlockedForViewer(state.Tags)) {
                    StopVideo();
                    currentPost = null;
                    overlayText.text = $"{remoteOwner} is playing a video your filters hide.\n<size=24>(blacklist, AI setting or always-hidden content)</size>";
                    remoteState = state;
                    return;
                }
                var post = new Post {
                    Id = state.PostId,
                    VideoUrl = state.Url,
                    Extension = System.IO.Path.GetExtension(state.Url.Split('?')[0]).TrimStart('.').ToLowerInvariant(),
                    Tags = state.Tags.ToList(),
                    // Built from the number only: a URL from another player is never opened in your browser.
                    PageUrl = state.Site == "r34"
                        ? "https://rule34.xxx/index.php?page=post&s=view&id=" + state.PostId
                        : "https://e621.net/posts/" + state.PostId,
                };
                remoteState = state;
                PlayPost(post, $"Shared by {remoteOwner}");
                return;
            }

            remoteState = state;
            if (remoteReady) {
                SyncPlayback(force: false);
            }
        }

        private void OnRemotePrepared() {
            remoteReady = true;
            SyncPlayback(force: true);
        }

        /// <summary>Where the owner's video is right now, from their last message and the room clock.</summary>
        private double OwnerTime() {
            if (remoteState == null) {
                return 0;
            }
            double time = remoteState.Time;
            if (remoteState.Playing) {
                time += unchecked(PhotonNetwork.ServerTimestamp - remoteState.Stamp) / 1000.0;
            }
            double length = VideoLength();
            if (length > 0.1) {
                time = remoteState.Loop ? time % length : Math.Min(time, length - 0.05);
            }
            return Math.Max(0, time);
        }

        private void SyncPlayback(bool force) {
            if (player == null || !player.isPrepared || remoteState == null) {
                return;
            }
            player.isLooping = remoteState.Loop;
            if (remoteState.Playing && !player.isPlaying) {
                player.Play();
            } else if (!remoteState.Playing && player.isPlaying) {
                player.Pause();
            }
            double target = OwnerTime();
            if (player.canSetTime && (force || Math.Abs(player.time - target) > 0.6)) {
                player.time = target;
            }
            RefreshButtons();
        }

        /// <summary>Per frame for viewer copies: glide to the owner's pose and correct drift now and then.</summary>
        private void UpdateRemote() {
            float k = 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, netPosition, k);
            transform.rotation = Quaternion.Slerp(transform.rotation, netRotation, k);
            if (remoteReady && Time.unscaledTime >= nextDriftCheck) {
                nextDriftCheck = Time.unscaledTime + 1f;
                SyncPlayback(force: false);
            }
        }

        /// <summary>Tags on someone else's screen: shown, not clickable (they'd change your search, not theirs).</summary>
        private void BuildTagLabels(Post post) {
            float w = TagsW - 16f;
            float y = 0f;
            foreach (var tag in post.Tags.Take(120)) {
                var label = AddText(tagContent, "Tag", 12f, y, w - 24f, 34f, tag, 20f, Theme.TextDim, TextAlignmentOptions.MidlineLeft);
                label.richText = false;
                label.enableWordWrapping = false;
                y += 36f;
            }
            tagContent.sizeDelta = new Vector2(0f, Mathf.Max(10f, y));
            tagContent.anchoredPosition = Vector2.zero;
        }
    }
}
