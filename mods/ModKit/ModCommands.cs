using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace KoboldKare.ModKit {
    /// <summary>What a command handler gets: who ran it, the arguments, and where to write output.</summary>
    public class CommandContext {
        /// <summary>The kobold of the player who typed the command (always the local player).</summary>
        public Kobold Caller { get; internal set; }
        /// <summary>Full argument list, e.g. {"/npc-ai", "on"}.</summary>
        public string[] Args { get; internal set; }
        public StringBuilder Output { get; internal set; }

        /// <summary>Argument by index (0 = the command itself), or null if missing.</summary>
        public string Arg(int index) => index < Args.Length ? Args[index] : null;

        /// <summary>Write a line to the chat/command log.</summary>
        public void Reply(string line) => Output.Append(line).Append('\n');

        /// <summary>Stop and show an error line (orange, like the game's own command errors).</summary>
        public void Fail(string message) => throw new CheatsProcessor.CommandException(message);

        /// <summary>Fail unless this player is the host (or in single player).</summary>
        public void RequireHost() {
            if (!ModKit.IsHost) {
                Fail("Only the host can use this command.");
            }
        }

        /// <summary>Fail unless cheats are enabled (same rule as the game's cheat commands).</summary>
        public void RequireCheats() {
            if (!CheatsProcessor.GetCheatsEnabled()) {
                Fail("Cheats are not enabled, use `/cheats 1` to enable cheats.");
            }
        }
    }

    /// <summary>
    /// Registers chat commands with the game's CheatsProcessor. Registration survives map loads.
    /// <code>
    /// ModCommands.Register("/hello", "Say hello", ctx => ctx.Reply("Hello " + ctx.Caller.name));
    /// </code>
    /// </summary>
    public static class ModCommands {
        public class Info {
            public string Name;
            public string Description;
            public string Owner;
            internal Command Command;
        }

        private static readonly List<Info> Registered = new List<Info>();
        private static FieldInfo instanceField;
        private static FieldInfo commandsField;
        private static bool reflectionFailed;
        private static object syncedProcessor;
        private static int syncedCount = -1;

        public static IReadOnlyList<Info> All => Registered;

        /// <summary>Register a command. <paramref name="name"/> must start with '/'.</summary>
        /// <param name="autocomplete">Optional: (argumentIndex, currentText) -> suggestions for that argument.</param>
        public static void Register(string name, string description, Action<CommandContext> run,
                                    Func<int, string, IEnumerable<string>> autocomplete = null) {
            if (string.IsNullOrEmpty(name) || name[0] != '/') {
                throw new ArgumentException("Command names must start with '/'", nameof(name));
            }
            if (run == null) {
                throw new ArgumentNullException(nameof(run));
            }
            Add(new Info {
                Name = name,
                Description = description ?? "",
                Owner = Assembly.GetCallingAssembly().GetName().Name,
                Command = new DelegateCommand(name, run, autocomplete),
            });
        }

        /// <summary>Register a hand-written subclass of the game's <see cref="Command"/>.</summary>
        public static void Register(Command command, string description = "") {
            Add(new Info {
                Name = command.GetArg0(),
                Description = description ?? "",
                Owner = Assembly.GetCallingAssembly().GetName().Name,
                Command = command,
            });
        }

        private static readonly HashSet<Command> Owned = new HashSet<Command>();

        private static void Add(Info info) {
            Owned.Add(info.Command);
            Registered.RemoveAll(r => r.Name == info.Name);
            Registered.Add(info);
            syncedCount = -1; // force a re-sync
            SyncWithGame();
        }

        internal static void RegisterBuiltins() {
            Register("/mods", "List ModKit mods' commands and panel pages.", ctx => {
                ctx.Reply($"<b>ModKit {ModKit.Version}</b>  (panel: {ModKitPlugin.PanelHotkeyName})");
                foreach (var group in Registered.GroupBy(r => r.Owner)) {
                    ctx.Reply($"<#7dfff0>{group.Key}</color>");
                    foreach (var cmd in group) {
                        ctx.Reply($"\t{cmd.Name}  {cmd.Description}");
                    }
                }
                var pages = ModPanel.PageTitles;
                if (pages.Count > 0) {
                    ctx.Reply("Panel pages: " + string.Join(", ", pages.ToArray()));
                }
            });
        }

        /// <summary>Adds any registered commands missing from the current CheatsProcessor. Cheap when nothing changed.</summary>
        internal static void SyncWithGame() {
            if (reflectionFailed) {
                return;
            }
            try {
                if (instanceField == null || commandsField == null) {
                    const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
                    instanceField = typeof(CheatsProcessor).GetField("instance", flags);
                    commandsField = typeof(CheatsProcessor).GetField("commands", flags);
                    if (instanceField == null || commandsField == null) {
                        reflectionFailed = true;
                        ModKit.Log.LogError("CheatsProcessor changed (fields not found); chat commands can't be registered.");
                        return;
                    }
                }
                var processor = instanceField.GetValue(null);
                if (processor == null || (ReferenceEquals(processor, syncedProcessor) && syncedCount == Registered.Count)) {
                    return;
                }
                if (!(commandsField.GetValue(processor) is List<Command> commands)) {
                    return;
                }
                int added = 0;
                foreach (var info in Registered) {
                    if (commands.Contains(info.Command)) {
                        continue;
                    }
                    // Drop an older instance of the same mod command (re-registered under the same name).
                    commands.RemoveAll(c => c != null && c != info.Command && Owned.Contains(c) && c.GetArg0() == info.Name);
                    if (commands.Any(c => c != null && !Owned.Contains(c) && c.GetArg0() == info.Name)) {
                        ModKit.Log.LogWarning($"{info.Owner}: {info.Name} clashes with a built-in game command; the game's one will run.");
                    }
                    commands.Add(info.Command);
                    added++;
                }
                syncedProcessor = processor;
                syncedCount = Registered.Count;
                if (added > 0) {
                    ModKit.Log.LogInfo($"Registered {added} command(s): {string.Join(", ", Registered.Select(r => r.Name).ToArray())}");
                }
            } catch (Exception e) {
                reflectionFailed = true;
                ModKit.Log.LogError("Failed to register commands: " + e);
            }
        }

        [Serializable]
        private class DelegateCommand : Command {
            private readonly string name;
            private readonly Action<CommandContext> run;
            private readonly Func<int, string, IEnumerable<string>> autocomplete;

            public DelegateCommand(string name, Action<CommandContext> run, Func<int, string, IEnumerable<string>> autocomplete) {
                this.name = name;
                this.run = run;
                this.autocomplete = autocomplete;
            }

            public override string GetArg0() => name;

            public override void Execute(StringBuilder output, Kobold k, string[] args) {
                var ctx = new CommandContext { Caller = k, Args = args, Output = output };
                try {
                    run(ctx);
                } catch (CheatsProcessor.CommandException) {
                    throw;
                } catch (Exception e) {
                    ModKit.Log.LogError($"Command {name} threw: {e}");
                    throw new CheatsProcessor.CommandException($"{name} failed: {e.GetType().Name}: {e.Message}");
                }
            }

            public override IEnumerable<AutocompleteResult> Autocomplete(int argumentIndex, string[] arguments, string text) {
                if (autocomplete == null) {
                    return Array.Empty<AutocompleteResult>();
                }
                try {
                    return (autocomplete(argumentIndex, text ?? "") ?? Enumerable.Empty<string>())
                        .Select(s => new AutocompleteResult(s))
                        .ToArray();
                } catch (Exception e) {
                    ModKit.Log.LogError($"Autocomplete for {name} threw: {e}");
                    return Array.Empty<AutocompleteResult>();
                }
            }
        }
    }
}
