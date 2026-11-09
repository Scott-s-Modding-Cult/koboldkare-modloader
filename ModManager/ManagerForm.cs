// The mod manager window: tick mods on and off, see what each needs, add and remove mods, then save and play.
// Nothing on disk changes until Save (except Add Mod and Remove, which say so).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;


namespace KoboldKareModManager
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        const string Caption = "KoboldKare Mod Manager";

        // Set once the game folder is known, so crash reports can go to BepInEx\modmanager.log.
        static GameFolder game;

        [STAThread]
        static void Main(string[] args)
        {
            // A WinExe that throws before its window opens just vanishes; show and log the error instead.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception);
            try
            {
                Run();
            }
            catch (Exception error)
            {
                ReportCrash(error);
            }
        }

        static void Run()
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            game = GameFolder.Find(AppDomain.CurrentDomain.BaseDirectory);
            if (game == null)
            {
                MessageBox.Show("Put KoboldKare Mod Manager.exe in the KoboldKare game folder (next to KoboldKare.exe).",
                    Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // Mono.Cecil comes with BepInEx; use that copy rather than shipping another.
            // UnsafeLoadFrom, not LoadFrom: a BepInEx zip unpacked with Explorer keeps the "downloaded from the internet"
            // mark, and .NET Framework refuses to LoadFrom such a file (FileLoadException, HRESULT 0x80131515).
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string file = Path.Combine(game.Core, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.UnsafeLoadFrom(file) : null;
            };
            if (!Directory.Exists(game.BepInEx))
            {
                MessageBox.Show("BepInEx isn't installed in this game folder, so there are no mods to manage.",
                    Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(Path.Combine(game.Core, "Mono.Cecil.dll")))
            {
                MessageBox.Show("BepInEx looks incomplete: BepInEx\\core\\Mono.Cecil.dll is missing, and the mod manager needs it to read mods.\n\n" +
                    "Extract the whole BepInEx 5 (x64) zip into the game folder again, start the game once, then open the mod manager. " +
                    "If the file keeps disappearing, check your antivirus quarantine.",
                    Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Application.Run(new ManagerForm(game));
        }

        static bool reporting;

        static void ReportCrash(Exception error)
        {
            if (reporting) { return; }
            reporting = true;
            try
            {
                string details = error == null ? "Unknown error." : error.ToString();
                bool logged = false;
                if (game != null && Directory.Exists(game.BepInEx))
                {
                    game.Log("Crashed: " + details);
                    logged = File.Exists(game.ManagerLog);
                }
                string summary = error == null ? "Unknown error." : error.GetType().Name + ": " + error.Message;
                string message = "The mod manager hit an error and has to close.\n\n" + summary + "\n\n" +
                    (logged ? "Full details are in BepInEx\\modmanager.log." : details);
                MessageBox.Show(message, Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception) { }
            Environment.Exit(1);
        }
    }

    class ManagerForm : Form
    {
        const string Caption = "KoboldKare Mod Manager";

        readonly GameFolder game;
        List<ModEntry> mods;
        HashSet<string> lastLoaded;
        int lastErrors;
        DateTime? lastRun;

        readonly CheckedListBox list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, AllowDrop = true };
        readonly TextBox details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
        readonly TextBox warnings = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
        readonly Button add = new Button { Text = "Add Mod...", AutoSize = true };
        readonly Button remove = new Button { Text = "Remove", AutoSize = true };
        readonly Button config = new Button { Text = "Open Config", AutoSize = true };
        readonly Button folder = new Button { Text = "Open Mod Folder", AutoSize = true };
        readonly Button log = new Button { Text = "Open Log", AutoSize = true };
        readonly CheckBox bepinex = new CheckBox { Text = "Load mods (BepInEx)", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(6, 10, 3, 3) };
        readonly Button play = new Button { Text = "Save and Play", AutoSize = true };
        readonly Button close = new Button { Text = "Save and Close", AutoSize = true };
        readonly ToolTip tips = new ToolTip();
        bool filling, running;

        public ManagerForm(GameFolder game)
        {
            this.game = game;
            Text = Caption;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(800, 520);
            MinimumSize = new Size(600, 400);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            add.Click += (s, e) => AddMod();
            remove.Click += (s, e) => RemoveSelected();
            config.Click += (s, e) => { string file = Selected == null ? null : Scanner.ConfigFile(game, Selected); if (file != null) { Open(file); } };
            folder.Click += (s, e) => Open(Selected != null ? (Selected.IsLoose ? Path.GetDirectoryName(Selected.Path) : Selected.Path) : game.Plugins);
            log.Click += (s, e) => { if (File.Exists(game.LogOutput)) { Open(game.LogOutput); } };
            play.Click += (s, e) => { if (Save()) { StartGame(); Close(); } };
            close.Click += (s, e) => { if (Save()) { Close(); } };
            bepinex.CheckedChanged += (s, e) => { if (!filling) { ShowWarnings(); } };
            list.ItemCheck += ListItemCheck;
            list.SelectedIndexChanged += (s, e) => ShowDetails();
            list.DragEnter += (s, e) => e.Effect = DroppedFiles(e.Data).Any() ? DragDropEffects.Copy : DragDropEffects.None;
            list.DragDrop += (s, e) => { foreach (string file in DroppedFiles(e.Data)) { InstallFile(file); } };
            // Pick up the game starting or stopping, and mods added by hand, whenever the window comes back.
            Activated += (s, e) => Reload(false);

            tips.SetToolTip(add, "Install a mod from a .zip or .dll (or drop files on the list).");
            tips.SetToolTip(remove, "Send the selected mod to the Recycle Bin. Its config file is kept.");
            tips.SetToolTip(bepinex, "Off starts the game without any mods (sets enabled in doorstop_config.ini).");
            tips.SetToolTip(play, "Save, then start KoboldKare through Steam so workshop mods load too.");

            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            left.Controls.AddRange(new Control[] { add, remove, config, folder, log });
            var right = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            right.Controls.AddRange(new Control[] { play, close });

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(list, 0, 0);
            layout.Controls.Add(details, 1, 0);
            layout.Controls.Add(warnings, 0, 1);
            layout.SetColumnSpan(warnings, 2);
            layout.Controls.Add(left, 0, 2);
            layout.SetColumnSpan(left, 2);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bottom.Controls.Add(bepinex, 0, 0);
            bottom.Controls.Add(right, 1, 0);
            layout.Controls.Add(bottom, 0, 3);
            layout.SetColumnSpan(bottom, 2);
            Controls.Add(layout);

            mods = Scanner.Load(game);
            filling = true;
            bepinex.Checked = game.BepInExEnabled ?? true;
            bepinex.Enabled = game.BepInExEnabled.HasValue;
            filling = false;
            Reload(true);
        }

        ModEntry Selected => list.SelectedIndex >= 0 && list.SelectedIndex < mods.Count ? mods[list.SelectedIndex] : null;

        static IEnumerable<string> DroppedFiles(IDataObject data) =>
            (data.GetData(DataFormats.FileDrop) as string[] ?? new string[0])
                .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        static void Open(string path)
        {
            try { Process.Start(path); }
            catch (Exception error) { MessageBox.Show(error.Message, Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // Re-reads the folders, keeping ticks the player changed but hasn't saved.
        void Reload(bool first)
        {
            running = game.GameIsRunning();
            lastLoaded = Scanner.LastLoaded(game, out lastErrors, out lastRun);
            if (!first)
            {
                var wanted = mods.Where(m => m.Enabled != m.Installed).ToDictionary(m => m.Name, m => m.Enabled, StringComparer.OrdinalIgnoreCase);
                string selected = Selected?.Name;
                mods = Scanner.Load(game);
                foreach (ModEntry mod in mods) { if (wanted.TryGetValue(mod.Name, out bool on)) { mod.Enabled = on; } }
                Fill(mods.FindIndex(m => m.Name == selected));
            }
            else
            {
                Fill(0);
            }
            remove.Enabled = add.Enabled = !running;
        }

        void Fill(int selected)
        {
            filling = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (ModEntry mod in mods) { list.Items.Add(mod.Line, mod.Enabled); }
            list.EndUpdate();
            filling = false;
            if (mods.Count > 0) { list.SelectedIndex = Math.Max(0, Math.Min(selected, mods.Count - 1)); }
            else { ShowDetails(); }
            ShowWarnings();
        }

        void ListItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (filling) { return; }
            ModEntry mod = mods[e.Index];
            mod.Enabled = e.NewValue == CheckState.Checked;
            if (mod.Enabled)
            {
                // Turning a mod on turns on what it can't run without.
                BeginInvoke((Action)(() =>
                {
                    foreach (ModEntry need in Scanner.HardDependencies(mods, mod).Where(m => !m.Enabled))
                    {
                        need.Enabled = true;
                        filling = true;
                        list.SetItemChecked(mods.IndexOf(need), true);
                        filling = false;
                    }
                    ShowDetails();
                    ShowWarnings();
                }));
            }
            else
            {
                BeginInvoke((Action)(() => { ShowDetails(); ShowWarnings(); }));
            }
        }

        string TitleFor(string guid)
        {
            ModEntry mod = mods.FirstOrDefault(m => m.Guids.Contains(guid, StringComparer.OrdinalIgnoreCase));
            return mod != null ? mod.Title : guid + " (not installed)";
        }

        void ShowDetails()
        {
            ModEntry mod = Selected;
            config.Enabled = mod != null && Scanner.ConfigFile(game, mod) != null;
            remove.Enabled = mod != null && !running;
            if (mod == null)
            {
                details.Text = "No mods yet. Use Add Mod, or drop a .zip or .dll here.";
                return;
            }

            var lines = new List<string> { mod.Line };
            if (mod.Author != "") { lines.Add("By " + mod.Author); }
            lines.Add("");
            if (mod.Description != "") { lines.Add(mod.Description); lines.Add(""); }

            foreach (PluginInfo plugin in mod.Plugins)
            {
                if (mod.Plugins.Count > 1) { lines.Add("Plugin: " + plugin.Name + " v" + plugin.Version); }
                lines.Add("ID: " + plugin.Guid);
            }
            var hard = mod.Plugins.SelectMany(p => p.Dependencies).Where(d => d.Hard).Select(d => TitleFor(d.Guid)).Distinct().ToList();
            var soft = mod.Plugins.SelectMany(p => p.Dependencies).Where(d => !d.Hard).Select(d => TitleFor(d.Guid)).Distinct().ToList();
            var dependents = Scanner.Dependents(mods, mod).Select(m => m.Title).ToList();
            if (hard.Count > 0) { lines.Add("Needs: " + string.Join(", ", hard)); }
            if (soft.Count > 0) { lines.Add("Works with (optional): " + string.Join(", ", soft)); }
            if (dependents.Count > 0) { lines.Add("Needed by: " + string.Join(", ", dependents)); }

            if (lastLoaded != null && mod.Plugins.Count > 0)
            {
                bool loaded = mod.Plugins.All(p => lastLoaded.Contains(p.Name + " " + p.Version));
                lines.Add("Last run" + (lastRun.HasValue ? " (" + lastRun.Value.ToString("g") + ")" : "") + ": " + (loaded ? "loaded" : "not loaded"));
            }
            lines.Add("");
            if (mod.Website != "") { lines.Add("Website: " + mod.Website); }
            string cfg = Scanner.ConfigFile(game, mod);
            if (cfg != null) { lines.Add("Config: " + game.Relative(cfg)); }
            lines.Add((mod.IsLoose ? "File: " : "Folder: ") + game.Relative(mod.Path));
            if (!mod.IsLoose)
            {
                string dlls = string.Join(", ", mod.Dlls.Select(Path.GetFileName));
                int others = mod.FileCount - mod.Dlls.Count;
                lines.Add("Files: " + (dlls == "" ? "no DLLs" : dlls) + (others > 0 ? " + " + others + " other file" + (others == 1 ? "" : "s") : ""));
            }
            details.Text = string.Join(Environment.NewLine, lines);
        }

        void ShowWarnings()
        {
            var lines = new List<string>();
            if (running) { lines.Add("KoboldKare is running. Close it before saving: it locks the mod files."); }
            if (!bepinex.Checked) { lines.Add("Load mods is off: the game will start with no mods at all."); }

            List<string> staged = Scanner.StagedMods(game);
            if (staged.Count > 0) { lines.Add("New builds of " + string.Join(", ", staged) + " are waiting in ModSource\\_staged and will be installed when you save."); }

            lines.AddRange(Scanner.Check(game, mods).OrderBy(p => p.Serious ? 0 : 1).Select(p => p.Text));

            int changes = mods.Count(m => m.Enabled != m.Installed);
            if (changes > 0) { lines.Add(changes + " change" + (changes == 1 ? "" : "s") + " not saved yet."); }
            if (lastErrors > 0) { lines.Add("The last run logged " + lastErrors + " error" + (lastErrors == 1 ? "" : "s") + ". Open Log shows BepInEx\\LogOutput.log."); }
            if (lines.Count == 0) { lines.Add("No problems. BepInEx loads mods in the order their dependencies need, so there's no order to set."); }
            warnings.Text = string.Join(Environment.NewLine, lines);
            log.Enabled = File.Exists(game.LogOutput);
        }

        bool Save()
        {
            running = game.GameIsRunning();
            bool changesMods = mods.Any(m => m.Enabled != m.Installed) || Scanner.StagedMods(game).Count > 0;
            bool changesSwitch = game.BepInExEnabled.HasValue && game.BepInExEnabled.Value != bepinex.Checked;
            if (running && (changesMods || changesSwitch))
            {
                ShowWarnings();
                MessageBox.Show("KoboldKare is running and has the mod files open. Close the game, then save again.",
                    Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var errors = new List<string>();
            try { if (changesSwitch) { game.SetBepInExEnabled(bepinex.Checked); game.Log("BepInEx " + (bepinex.Checked ? "on" : "off")); } }
            catch (Exception error) { errors.Add("Could not change doorstop_config.ini: " + error.Message); }
            errors.AddRange(Mover.Apply(game, mods, installStaged: true));

            if (errors.Count > 0)
            {
                Reload(false);
                MessageBox.Show(string.Join("\n\n", errors), Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        void StartGame()
        {
            // Through Steam, or Steam workshop mods won't load.
            try { Process.Start("steam://rungameid/" + GameFolder.SteamAppId); }
            catch (Exception)
            {
                Process.Start(new ProcessStartInfo(game.Exe) { WorkingDirectory = game.Root, UseShellExecute = false });
            }
        }

        void AddMod()
        {
            using (var dialog = new OpenFileDialog { Title = "Add a KoboldKare mod", Filter = "BepInEx mods (*.zip;*.dll)|*.zip;*.dll", Multiselect = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                foreach (string file in dialog.FileNames) { InstallFile(file); }
            }
        }

        void InstallFile(string file)
        {
            if (game.GameIsRunning())
            {
                MessageBox.Show("Close KoboldKare before adding mods.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Installer.Plan plan = null;
            try
            {
                plan = Installer.Prepare(file);
                var conflicts = Installer.Conflicts(plan, mods);
                if (conflicts.Count > 0)
                {
                    string names = string.Join(", ", conflicts.Select(m => m.Line));
                    if (MessageBox.Show("Replace " + names + "?\n\nThe old version goes to the Recycle Bin. Its settings are kept.",
                        Caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { return; }
                }
                List<string> installed = Installer.Install(game, plan, mods);
                Reload(false);
                int index = mods.FindIndex(m => installed.Contains(m.Name, StringComparer.OrdinalIgnoreCase));
                if (index >= 0) { list.SelectedIndex = index; }
            }
            catch (Exception error)
            {
                game.Log("Could not install " + file + ": " + error);
                MessageBox.Show("Could not install " + Path.GetFileName(file) + ":\n\n" + error.Message, Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Installer.Cleanup(plan);
            }
        }

        void RemoveSelected()
        {
            ModEntry mod = Selected;
            if (mod == null) { return; }
            if (game.GameIsRunning())
            {
                MessageBox.Show("Close KoboldKare before removing mods.", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var dependents = Scanner.Dependents(mods, mod).Select(m => m.Title).ToList();
            string message = "Send " + mod.Line + " to the Recycle Bin?" + Environment.NewLine + Environment.NewLine + "Its config file stays in BepInEx\\config.";
            if (dependents.Count > 0) { message += Environment.NewLine + Environment.NewLine + string.Join(", ", dependents) + " need" + (dependents.Count == 1 ? "s" : "") + " it and won't load without it."; }
            if (MessageBox.Show(message, Caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { return; }
            try
            {
                Mover.Recycle(mod.Path);
                game.Log("Removed " + mod.Line);
            }
            catch (Exception error)
            {
                MessageBox.Show("Could not remove " + mod.Title + ": " + error.Message, Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Reload(false);
        }
    }
}
