using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Photon.Pun;

namespace KoboldKare.ModKit {
    /// <summary>
    /// Entry point for mods built on ModKit. Add
    /// <c>[BepInDependency(ModKit.Guid)]</c> to your plugin, then use:
    /// <list type="bullet">
    /// <item><see cref="Tick"/> / <see cref="MapLoaded"/> for per-frame work and map setup,</item>
    /// <item><see cref="ModCommands"/> for chat commands,</item>
    /// <item><see cref="ModPanel"/> for a tab in the shared in-game panel,</item>
    /// <item><see cref="Nav"/> and <see cref="SelfCheck"/> helpers.</item>
    /// </list>
    /// </summary>
    public static class ModKit {
        public const string Guid = "koboldkare.modkit";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

        /// <summary>
        /// Raised every frame from a runner that survives map loads (KoboldKare destroys plugin
        /// objects while loading). Each handler is isolated: if one throws, it's logged once and
        /// the others keep running.
        /// </summary>
        public static event Action Tick {
            add => TickHandlers.Add(value);
            remove => TickHandlers.Remove(value);
        }

        /// <summary>Raised when a map's command/chat system comes up (after every map load).</summary>
        public static event Action MapLoaded {
            add => MapLoadedHandlers.Add(value);
            remove => MapLoadedHandlers.Remove(value);
        }

        /// <summary>True while a map is loaded (not on the main menu).</summary>
        public static bool InLevel {
            get {
                try {
                    return GameManager.InLevel();
                } catch {
                    return false;
                }
            }
        }

        /// <summary>True in single player or when we host the room. Most world changes should require this.</summary>
        public static bool IsHost => !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;

        /// <summary>True in single player or when nobody else is in the room.</summary>
        public static bool IsAlone =>
            PhotonNetwork.OfflineMode || PhotonNetwork.CurrentRoom == null || PhotonNetwork.CurrentRoom.PlayerCount <= 1;

        /// <summary>The local player's kobold, or null.</summary>
        public static Kobold LocalKobold => PhotonNetwork.LocalPlayer?.TagObject as Kobold;

        // ------------------------------------------------------------------ internals

        private static readonly HandlerList TickHandlers = new HandlerList("Tick");
        private static readonly HandlerList MapLoadedHandlers = new HandlerList("MapLoaded");

        internal static void RaiseTick() => TickHandlers.Invoke();
        internal static void RaiseMapLoaded() => MapLoadedHandlers.Invoke();

        private class HandlerList {
            private readonly string name;
            private readonly List<Action> handlers = new List<Action>();
            private readonly HashSet<Action> failed = new HashSet<Action>();
            private Action[] snapshot = new Action[0];

            public HandlerList(string name) {
                this.name = name;
            }

            public void Add(Action a) {
                if (a == null) {
                    return;
                }
                handlers.Add(a);
                snapshot = handlers.ToArray();
            }

            public void Remove(Action a) {
                handlers.Remove(a);
                snapshot = handlers.ToArray();
            }

            public void Invoke() {
                foreach (var handler in snapshot) {
                    try {
                        handler();
                    } catch (Exception e) {
                        if (failed.Add(handler)) {
                            Log?.LogError($"{name} handler {handler.Method.DeclaringType?.FullName}.{handler.Method.Name} threw " +
                                          $"(further errors from it are suppressed): {e}");
                        }
                    }
                }
            }
        }
    }
}
