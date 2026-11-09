// The parts of the mod manager that touch files: finding the game, reading plugins, turning them on and off,
// installing staged builds and mods from .zip/.dll files. ManagerForm.cs is only the window.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KoboldKareModManager
{
    class GameFolder
    {
        public const string SteamAppId = "1102930";

        public readonly string Root;
        GameFolder(string root) { Root = Path.GetFullPath(root).TrimEnd('\\'); }

        public string Exe => Path.Combine(Root, "KoboldKare.exe");
        public string BepInEx => Path.Combine(Root, "BepInEx");
        public string Plugins => Path.Combine(BepInEx, "plugins");
        // Outside plugins\, because BepInEx loads every DLL under plugins\ however deep it is.
        public string DisabledPlugins => Path.Combine(BepInEx, "plugins-disabled");
        public string Config => Path.Combine(BepInEx, "config");
        public string Patchers => Path.Combine(BepInEx, "patchers");
        public string Core => Path.Combine(BepInEx, "core");
        public string LogOutput => Path.Combine(BepInEx, "LogOutput.log");
        public string Doorstop => Path.Combine(Root, "doorstop_config.ini");
        public string Staged => Path.Combine(Root, @"ModSource\_staged");
        public string ManagerLog => Path.Combine(BepInEx, "modmanager.log");

        public static bool LooksLikeTheGame(string folder) =>
            File.Exists(Path.Combine(folder, "KoboldKare.exe")) && Directory.Exists(Path.Combine(folder, "KoboldKare_Data"));

        // The folder this exe is in, or the nearest one above it (so a debug build in ModSource\...\bin works too).
        public static GameFolder Find(string start)
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (LooksLikeTheGame(dir.FullName)) { return new GameFolder(dir.FullName); }
            }
            return null;
        }

        public string Relative(string path) =>
            path.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase) ? path.Substring(Root.Length + 1) : path;

        public bool GameIsRunning()
        {
            foreach (Process process in Process.GetProcessesByName("KoboldKare"))
            {
                try
                {
                    if (string.Equals(process.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase)) { return true; }
                }
                catch (Exception)
                {
                    // Can't inspect it (e.g. elevated): assume it's ours, since moving locked files would fail anyway.
                    return true;
                }
            }
            return false;
        }

        // BepInEx only runs while doorstop is enabled, so this is the "all mods on/off" switch.
        static readonly Regex DoorstopEnabled = new Regex(@"^(\s*enabled\s*=\s*)(\w+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        public bool? BepInExEnabled
        {
            get
            {
                if (!File.Exists(Doorstop)) { return null; }
                Match match = DoorstopEnabled.Match(File.ReadAllText(Doorstop));
                return match.Success ? match.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase) : (bool?)null;
            }
        }

        public void SetBepInExEnabled(bool on)
        {
            string text = File.ReadAllText(Doorstop);
            string updated = DoorstopEnabled.Replace(text, m => m.Groups[1].Value + (on ? "true" : "false"), 1);
            if (updated != text) { File.WriteAllText(Doorstop, updated); }
        }

        public void Log(string message)
        {
            try { File.AppendAllText(ManagerLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine); }
            catch (Exception) { }
        }
    }

    class Dependency
    {
        public string Guid, MinVersion;
        public bool Hard;
    }

    // One [BepInPlugin] class.
    class PluginInfo
    {
        public string Guid, Name, Version, Dll;
        public List<Dependency> Dependencies = new List<Dependency>();
        public List<string> Incompatible = new List<string>();
        public List<string> Processes = new List<string>();
    }

    // One folder (or loose DLL) under plugins\ or plugins-disabled\. This is what gets ticked on and off.
    class ModEntry
    {
        public string Name, Path, Title, Version = "", Author = "", Description = "", Website = "", Error;
        public bool IsLoose, Installed, Enabled;   // Installed: in plugins\ now. Enabled: what the tick says.
        public List<PluginInfo> Plugins = new List<PluginInfo>();
        public List<string> Dlls = new List<string>();
        public int FileCount;

        public string Line => Version == "" ? Title : Title + "  v" + Version;
        public IEnumerable<string> Guids => Plugins.Select(p => p.Guid);
    }

    class Problem
    {
        public readonly string Text;
        public readonly bool Serious;
        public Problem(string text, bool serious = false) { Text = text; Serious = serious; }
    }

    static class Scanner
    {
        public static List<ModEntry> Load(GameFolder game)
        {
            var found = new List<ModEntry>();
            // Where Cecil may look for assemblies a plugin's attributes refer to (BepInEx.dll for DependencyFlags).
            var dirs = new List<string> { game.Core, Path.Combine(game.BepInEx, "unstripped_corlib"), Path.Combine(game.Root, @"KoboldKare_Data\Managed") };
            foreach (string top in new[] { game.Plugins, game.DisabledPlugins }.Where(Directory.Exists))
            {
                dirs.Add(top);
                dirs.AddRange(Directory.GetDirectories(top, "*", SearchOption.AllDirectories));
            }
            PluginReader.Begin(dirs);
            try
            {
                Scan(game.Plugins, true, found);
                Scan(game.DisabledPlugins, false, found);
            }
            finally
            {
                PluginReader.End();
            }
            return found.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static void Scan(string folder, bool installed, List<ModEntry> into)
        {
            if (!Directory.Exists(folder)) { return; }
            foreach (string dir in Directory.GetDirectories(folder))
            {
                into.Add(Read(dir, false, installed));
            }
            foreach (string dll in Directory.GetFiles(folder, "*.dll"))
            {
                into.Add(Read(dll, true, installed));
            }
        }

        static ModEntry Read(string path, bool loose, bool installed)
        {
            var mod = new ModEntry
            {
                Path = path,
                IsLoose = loose,
                Installed = installed,
                Enabled = installed,
                Name = System.IO.Path.GetFileName(path),
            };
            mod.Title = loose ? System.IO.Path.GetFileNameWithoutExtension(path) : mod.Name;
            try
            {
                string[] files = loose ? new[] { path } : Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                mod.FileCount = files.Length;
                mod.Dlls = files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (string dll in mod.Dlls)
                {
                    try { mod.Plugins.AddRange(PluginReader.Read(dll)); }
                    catch (BadImageFormatException) { } // A native DLL shipped alongside; not a plugin.
                }
                if (!loose) { ReadManifest(mod); }
            }
            catch (Exception error)
            {
                mod.Error = error.Message;
            }

            if (mod.Plugins.Count == 1)
            {
                mod.Title = mod.Plugins[0].Name;
                if (mod.Version == "") { mod.Version = mod.Plugins[0].Version; }
            }
            else if (mod.Plugins.Count > 1 && mod.Version == "")
            {
                mod.Version = mod.Plugins[0].Version;
            }
            if (mod.Description == "") { mod.Description = PluginReader.Description(mod.Dlls) ?? ""; }
            return mod;
        }

        // Thunderstore-style manifest.json (name, version_number, description, website_url, author).
        static void ReadManifest(ModEntry mod)
        {
            string file = System.IO.Path.Combine(mod.Path, "manifest.json");
            if (!File.Exists(file)) { return; }
            try
            {
                var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                string Get(string key) => json.TryGetValue(key, out object v) && v != null ? v.ToString() : "";
                mod.Version = Get("version_number") != "" ? Get("version_number") : Get("version");
                mod.Description = Get("description");
                mod.Website = Get("website_url");
                mod.Author = Get("author");
            }
            catch (Exception) { }
        }

        // Which mods BepInEx loaded the last time the game ran, by "Name Version", read from LogOutput.log.
        public static HashSet<string> LastLoaded(GameFolder game, out int errors, out DateTime? when)
        {
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            errors = 0;
            when = null;
            if (!File.Exists(game.LogOutput)) { return null; }
            when = File.GetLastWriteTime(game.LogOutput);
            // The game holds the log open while running, so share it.
            using (var stream = new FileStream(game.LogOutput, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    Match match = Regex.Match(line, @"^\[Info\s*:\s*BepInEx\] Loading \[(.+)\]$");
                    if (match.Success) { loaded.Add(match.Groups[1].Value); }
                    else if (line.StartsWith("[Error") || line.StartsWith("[Fatal")) { errors++; }
                }
            }
            return loaded;
        }

        public static List<string> StagedMods(GameFolder game) =>
            Directory.Exists(game.Staged)
                ? Directory.GetDirectories(game.Staged).Where(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any())
                    .Select(System.IO.Path.GetFileName).ToList()
                : new List<string>();

        public static string ConfigFile(GameFolder game, ModEntry mod) =>
            mod.Guids.Select(g => System.IO.Path.Combine(game.Config, g + ".cfg")).FirstOrDefault(File.Exists);

        // Warnings about the mods as they are ticked right now.
        public static List<Problem> Check(GameFolder game, List<ModEntry> mods)
        {
            var problems = new List<Problem>();
            var on = mods.Where(m => m.Enabled).ToList();
            var providers = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry mod in mods.OrderBy(m => m.Enabled ? 0 : 1))
            {
                foreach (string guid in mod.Guids) { if (!providers.ContainsKey(guid)) { providers[guid] = mod; } }
            }

            foreach (ModEntry mod in mods)
            {
                if (mod.Error != null) { problems.Add(new Problem(mod.Title + " could not be read: " + mod.Error, true)); }
            }
            foreach (var group in mods.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                problems.Add(new Problem(group.Key + " is in both plugins and plugins-disabled. Remove one copy.", true));
            }

            foreach (ModEntry mod in on)
            {
                if (mod.Plugins.Count == 0 && mod.Dlls.Count > 0 && mod.Error == null)
                {
                    // Fine for a library another mod uses; worth knowing otherwise.
                    problems.Add(new Problem(mod.Title + " has no BepInEx plugin in it (it may be a library for another mod)."));
                }
                foreach (PluginInfo plugin in mod.Plugins)
                {
                    foreach (Dependency dep in plugin.Dependencies.Where(d => d.Hard))
                    {
                        if (!providers.TryGetValue(dep.Guid, out ModEntry other))
                        {
                            problems.Add(new Problem(plugin.Name + " needs " + dep.Guid + ", which isn't installed. BepInEx will skip " + plugin.Name + ".", true));
                        }
                        else if (!other.Enabled)
                        {
                            problems.Add(new Problem(plugin.Name + " needs " + other.Title + ", which is turned off. BepInEx will skip " + plugin.Name + ".", true));
                        }
                        else if (!string.IsNullOrEmpty(dep.MinVersion))
                        {
                            PluginInfo target = other.Plugins.First(p => p.Guid.Equals(dep.Guid, StringComparison.OrdinalIgnoreCase));
                            if (Version.TryParse(target.Version, out Version have) && Version.TryParse(dep.MinVersion, out Version want) && have < want)
                            {
                                problems.Add(new Problem(plugin.Name + " needs " + other.Title + " " + dep.MinVersion + " or newer (you have " + target.Version + ").", true));
                            }
                        }
                    }
                    foreach (string guid in plugin.Incompatible)
                    {
                        if (providers.TryGetValue(guid, out ModEntry other) && other.Enabled)
                        {
                            problems.Add(new Problem(plugin.Name + " doesn't work with " + other.Title + ". BepInEx will skip " + plugin.Name + ".", true));
                        }
                    }
                    if (plugin.Processes.Count > 0 && !plugin.Processes.Any(p => p.Equals("KoboldKare.exe", StringComparison.OrdinalIgnoreCase) || p.Equals("KoboldKare", StringComparison.OrdinalIgnoreCase)))
                    {
                        problems.Add(new Problem(plugin.Name + " is made for " + string.Join(", ", plugin.Processes) + ", not KoboldKare, so BepInEx won't load it.", true));
                    }
                }
            }

            foreach (var group in on.SelectMany(m => m.Plugins.Select(p => new { m, p })).GroupBy(x => x.p.Guid, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                problems.Add(new Problem(string.Join(" and ", group.Select(x => x.m.Name).Distinct()) + " both contain " + group.Key + ". BepInEx loads only one copy.", true));
            }
            return problems;
        }

        // Mods that need this one (hard dependency), for "Needed by" and for the unticking warning.
        public static List<ModEntry> Dependents(List<ModEntry> mods, ModEntry mod)
        {
            var guids = new HashSet<string>(mod.Guids, StringComparer.OrdinalIgnoreCase);
            return mods.Where(m => m != mod && m.Plugins.Any(p => p.Dependencies.Any(d => d.Hard && guids.Contains(d.Guid)))).ToList();
        }

        public static List<ModEntry> HardDependencies(List<ModEntry> mods, ModEntry mod)
        {
            var guids = new HashSet<string>(mod.Plugins.SelectMany(p => p.Dependencies).Where(d => d.Hard).Select(d => d.Guid), StringComparer.OrdinalIgnoreCase);
            return mods.Where(m => m != mod && m.Guids.Any(guids.Contains)).ToList();
        }
    }

    // Kept apart from Scanner so Mono.Cecil (loaded at runtime from BepInEx\core) is only touched here.
    static class PluginReader
    {
        // Resolves referenced assemblies from memory too, so nothing under plugins\ ends up locked.
        class MemoryResolver : Mono.Cecil.IAssemblyResolver
        {
            readonly List<string> dirs;
            readonly Dictionary<string, Mono.Cecil.AssemblyDefinition> cache = new Dictionary<string, Mono.Cecil.AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
            public MemoryResolver(List<string> dirs) { this.dirs = dirs; }

            public Mono.Cecil.AssemblyDefinition Resolve(Mono.Cecil.AssemblyNameReference name) => Resolve(name, null);

            public Mono.Cecil.AssemblyDefinition Resolve(Mono.Cecil.AssemblyNameReference name, Mono.Cecil.ReaderParameters parameters)
            {
                if (cache.TryGetValue(name.Name, out var known)) { return known; }
                foreach (string dir in dirs)
                {
                    string file = Path.Combine(dir, name.Name + ".dll");
                    if (!File.Exists(file)) { continue; }
                    var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(file)), new Mono.Cecil.ReaderParameters { AssemblyResolver = this });
                    cache[name.Name] = assembly;
                    return assembly;
                }
                throw new Mono.Cecil.AssemblyResolutionException(name);
            }

            public void Dispose()
            {
                foreach (var assembly in cache.Values) { assembly.Dispose(); }
                cache.Clear();
            }
        }

        static MemoryResolver resolver;
        public static void Begin(List<string> dirs) { End(); resolver = new MemoryResolver(dirs); }
        public static void End() { resolver?.Dispose(); resolver = null; }

        static Mono.Cecil.AssemblyDefinition Open(string dll)
        {
            if (resolver == null) { resolver = new MemoryResolver(new List<string>()); }
            // Read from memory so the file is never locked: it may need moving a moment later.
            return Mono.Cecil.AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(dll)), new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver });
        }

        public static List<PluginInfo> Read(string dll)
        {
            var result = new List<PluginInfo>();
            using (var assembly = Open(dll))
            {
                foreach (Mono.Cecil.TypeDefinition type in assembly.MainModule.GetTypes())
                {
                    if (!type.HasCustomAttributes) { continue; }
                    var plugin = new PluginInfo { Dll = dll };
                    foreach (Mono.Cecil.CustomAttribute attribute in type.CustomAttributes)
                    {
                        if (!attribute.AttributeType.FullName.StartsWith("BepInEx.BepIn")) { continue; }
                        Mono.Collections.Generic.Collection<Mono.Cecil.CustomAttributeArgument> args;
                        // An attribute that can't be decoded (a missing referenced assembly) is skipped, not fatal.
                        try { args = attribute.ConstructorArguments; }
                        catch (Exception) { continue; }
                        switch (attribute.AttributeType.FullName)
                        {
                            case "BepInEx.BepInPlugin":
                                plugin.Guid = args.Count > 0 ? args[0].Value as string : null;
                                plugin.Name = args.Count > 1 ? args[1].Value as string : null;
                                plugin.Version = args.Count > 2 ? args[2].Value as string : "";
                                break;
                            case "BepInEx.BepInDependency":
                                var dep = new Dependency { Guid = args[0].Value as string, Hard = true };
                                if (args.Count > 1 && args[1].Value is string min) { dep.MinVersion = min; }
                                // DependencyFlags: HardDependency = 1, SoftDependency = 2.
                                else if (args.Count > 1) { dep.Hard = (Convert.ToInt32(args[1].Value) & 1) != 0; }
                                plugin.Dependencies.Add(dep);
                                break;
                            case "BepInEx.BepInIncompatibility":
                                plugin.Incompatible.Add(args[0].Value as string);
                                break;
                            case "BepInEx.BepInProcess":
                                plugin.Processes.Add(args[0].Value as string);
                                break;
                        }
                    }
                    if (!string.IsNullOrEmpty(plugin.Guid))
                    {
                        if (string.IsNullOrEmpty(plugin.Name)) { plugin.Name = plugin.Guid; }
                        result.Add(plugin);
                    }
                }
            }
            return result;
        }

        // The first DLL's [AssemblyDescription], e.g. <Description> in the mod's .csproj.
        public static string Description(IEnumerable<string> dlls)
        {
            foreach (string dll in dlls)
            {
                try
                {
                    using (var assembly = Open(dll))
                    {
                        var attribute = assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "System.Reflection.AssemblyDescriptionAttribute");
                        string text = attribute?.ConstructorArguments[0].Value as string;
                        if (!string.IsNullOrWhiteSpace(text)) { return text; }
                    }
                }
                catch (Exception) { }
            }
            return null;
        }
    }

    static class Mover
    {
        // Moves each mod into plugins\ or plugins-disabled\ to match its tick, then installs staged builds.
        // Returns what went wrong; anything that worked stays done.
        public static List<string> Apply(GameFolder game, List<ModEntry> mods, bool installStaged)
        {
            var errors = new List<string>();
            foreach (ModEntry mod in mods.Where(m => m.Enabled != m.Installed))
            {
                string target = Path.Combine(mod.Enabled ? game.Plugins : game.DisabledPlugins, mod.Name);
                try
                {
                    if (File.Exists(target) || Directory.Exists(target)) { throw new IOException(game.Relative(target) + " already exists."); }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (mod.IsLoose) { File.Move(mod.Path, target); } else { Directory.Move(mod.Path, target); }
                    game.Log((mod.Enabled ? "Turned on " : "Turned off ") + mod.Line);
                    mod.Path = target;
                    mod.Installed = mod.Enabled;
                }
                catch (Exception error)
                {
                    errors.Add("Could not turn " + (mod.Enabled ? "on " : "off ") + mod.Title + ": " + error.Message);
                }
            }

            if (installStaged)
            {
                foreach (string name in Scanner.StagedMods(game))
                {
                    // A staged build of a mod that's turned off goes next to it, so it stays off.
                    ModEntry existing = mods.FirstOrDefault(m => !m.IsLoose && m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    string target = existing != null ? existing.Path : Path.Combine(game.Plugins, name);
                    string source = Path.Combine(game.Staged, name);
                    try
                    {
                        CopyTree(source, target);
                        Directory.Delete(source, true);
                        game.Log("Installed staged build of " + name);
                    }
                    catch (Exception error)
                    {
                        errors.Add("Could not install the staged build of " + name + ": " + error.Message);
                    }
                }
                try
                {
                    if (Directory.Exists(game.Staged) && !Directory.EnumerateFileSystemEntries(game.Staged).Any()) { Directory.Delete(game.Staged); }
                }
                catch (Exception) { }
            }
            return errors;
        }

        public static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(to, file.Substring(from.Length).TrimStart('\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(file, dest, true);
            }
        }

        // To the Recycle Bin, so a removal can be undone. The mod's config file is left in BepInEx\config.
        public static void Recycle(string path)
        {
            if (File.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else if (Directory.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
        }
    }

    // Installs a mod from a .dll or a .zip. Understands the usual layouts:
    //   BepInEx\plugins\<Mod>\...  (plus BepInEx\config and BepInEx\patchers)
    //   plugins\<Mod>\...          (Thunderstore; manifest.json kept with the mod)
    //   <Mod>\*.dll  or  *.dll     (a single mod, named after the folder, manifest or zip)
    static class Installer
    {
        public class Plan
        {
            public string Temp;
            public List<KeyValuePair<string, string>> Mods = new List<KeyValuePair<string, string>>(); // name -> source folder
            public string Config, Patchers;
        }

        public static string ModNameFromFile(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            // "Author-Mod-1.2.3" or "Mod-1.2.3" (Thunderstore downloads) -> "Mod"
            name = Regex.Replace(name, @"[-_ ]v?\d+(\.\d+)+$", "");
            int dash = name.IndexOf('-');
            if (dash > 0 && dash < name.Length - 1 && Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase)) { name = name.Substring(dash + 1); }
            return Clean(name);
        }

        static string Clean(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) { name = name.Replace(c.ToString(), ""); }
            return name.Trim().Length > 0 ? name.Trim() : "Mod";
        }

        // Unpacks into a temporary folder and works out what goes where, without touching the game yet.
        public static Plan Prepare(string file)
        {
            var plan = new Plan { Temp = Path.Combine(Path.GetTempPath(), "KoboldKareModManager-" + Guid.NewGuid().ToString("N")) };
            string name = ModNameFromFile(file);
            Directory.CreateDirectory(plan.Temp);

            if (Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                string folder = Path.Combine(plan.Temp, name);
                Directory.CreateDirectory(folder);
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                plan.Mods.Add(new KeyValuePair<string, string>(name, folder));
                return plan;
            }

            string root = Path.Combine(plan.Temp, "zip");
            Extract(file, root);
            string manifest = Path.Combine(root, "manifest.json");
            if (File.Exists(manifest))
            {
                try
                {
                    var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifest));
                    if (json.TryGetValue("name", out object n) && n is string s && s.Trim() != "") { name = Clean(s); }
                }
                catch (Exception) { }
            }

            string bep = FindDir(root, "BepInEx");
            string pluginsDir = bep != null ? FindDir(bep, "plugins") : FindDir(root, "plugins");
            if (pluginsDir != null)
            {
                string bepRoot = Path.GetDirectoryName(pluginsDir);
                plan.Config = FindDir(bepRoot, "config");
                plan.Patchers = FindDir(bepRoot, "patchers");
                var folders = Directory.GetDirectories(pluginsDir);
                var loose = Directory.GetFiles(pluginsDir);
                if (loose.Length > 0 || folders.Length == 0)
                {
                    // Loose files in plugins\ become one folder named after the mod.
                    string folder = Path.Combine(plan.Temp, "loose", name);
                    Directory.CreateDirectory(folder);
                    foreach (string f in loose) { File.Copy(f, Path.Combine(folder, Path.GetFileName(f))); }
                    plan.Mods.Add(new KeyValuePair<string, string>(name, folder));
                }
                foreach (string folder in folders) { plan.Mods.Add(new KeyValuePair<string, string>(Path.GetFileName(folder), folder)); }
                if (plan.Mods.Count == 1 && File.Exists(manifest)) { File.Copy(manifest, Path.Combine(plan.Mods[0].Value, "manifest.json"), true); }
            }
            else
            {
                // A single mod. Unwrap one folder level when that's all the zip holds (besides readme-type files).
                string content = root;
                var dirs = Directory.GetDirectories(root);
                bool onlyExtras = Directory.GetFiles(root).All(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                if (dirs.Length == 1 && onlyExtras)
                {
                    content = dirs[0];
                    if (!File.Exists(manifest)) { name = Clean(Path.GetFileName(dirs[0])); }
                    if (File.Exists(manifest)) { File.Copy(manifest, Path.Combine(content, "manifest.json"), true); }
                }
                plan.Mods.Add(new KeyValuePair<string, string>(name, content));
            }

            if (!plan.Mods.Any(m => Directory.EnumerateFiles(m.Value, "*.dll", SearchOption.AllDirectories).Any()))
            {
                throw new InvalidDataException("There's no .dll in " + Path.GetFileName(file) + ", so it isn't a BepInEx mod.");
            }
            return plan;
        }

        static string FindDir(string parent, string name) =>
            Directory.Exists(parent) ? Directory.GetDirectories(parent).FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;

        // Like ZipFile.ExtractToDirectory, but refuses entries that would land outside the folder.
        static void Extract(string zip, string to)
        {
            string full = Path.GetFullPath(to).TrimEnd('\\') + "\\";
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string dest = Path.GetFullPath(Path.Combine(full, entry.FullName));
                    if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("The zip has a file that points outside its folder: " + entry.FullName); }
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                }
            }
        }

        // Mods that would replace something already installed, as (name, existing path).
        public static List<ModEntry> Conflicts(Plan plan, List<ModEntry> mods) =>
            mods.Where(m => plan.Mods.Any(p => p.Key.Equals(m.Name, StringComparison.OrdinalIgnoreCase))).ToList();

        // Puts the mods in place. A mod being replaced keeps its on/off state; its old folder goes to the Recycle Bin.
        public static List<string> Install(GameFolder game, Plan plan, List<ModEntry> mods)
        {
            var installed = new List<string>();
            foreach (var pair in plan.Mods)
            {
                ModEntry old = mods.FirstOrDefault(m => m.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                string target = old != null && !old.IsLoose ? old.Path : Path.Combine(game.Plugins, pair.Key);
                if (old != null) { Mover.Recycle(old.Path); }
                Mover.CopyTree(pair.Value, target);
                installed.Add(pair.Key);
                game.Log("Installed " + pair.Key + " to " + game.Relative(target));
            }
            // Config: only files the player doesn't have yet, so their settings are never overwritten.
            if (plan.Config != null)
            {
                foreach (string file in Directory.GetFiles(plan.Config, "*", SearchOption.AllDirectories))
                {
                    string dest = Path.Combine(game.Config, file.Substring(plan.Config.Length).TrimStart('\\'));
                    if (File.Exists(dest)) { continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest);
                }
            }
            if (plan.Patchers != null) { Mover.CopyTree(plan.Patchers, game.Patchers); }
            return installed;
        }

        public static void Cleanup(Plan plan)
        {
            try { if (plan != null && Directory.Exists(plan.Temp)) { Directory.Delete(plan.Temp, true); } }
            catch (Exception) { }
        }
    }
}
