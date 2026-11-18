// Rolling Quest VR installer and uninstaller: one self-contained exe. Embedded resources: "bepinex.zip" (BepInEx 6
// IL2CPP, laid out like the game folder) and "payload/<path inside the game folder>" (the plugin, openvr_api.dll,
// controller bindings, artwork). See build.ps1.
//
// Double-click: finds Rolling Quest (itch.io game, so: Steam's non-Steam games, the itch app's folder, usual game
// folders, or Browse), installs, puts "Rolling Quest VR Uninstall.exe" in the game folder, and adds one non-Steam game
// "Rolling Quest VR" (in Steam's and SteamVR's VR library, with artwork) that starts RollingQuest.exe.
//
// Command line (Scoop etc.): /S = silent (log in %TEMP%\rqvr-setup.log), /GAMEDIR=<folder>, /UNINSTALL.
// /STEAM-WAIT = background helper: adds the Steam entry once Steam has closed.
// Exit codes: 0 = success, 1 = error, 2 = game not found (silent mode).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

static partial class RqvrSetup
{
    const string GameName = "Rolling Quest";
    const string EntryName = "Rolling Quest VR";
    const string GameExe = "RollingQuest.exe";
    const string UninstallerName = "Rolling Quest VR Uninstall.exe";
    const string PluginRel = @"BepInEx\plugins\RollingQuestVR";
    const string Backup = ".rqvr-backup";
    // BepInEx 6 files in the game folder (removed with the mod unless other BepInEx mods use them).
    static readonly string[] LoaderFiles = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };
    static readonly string[] LoaderDirs = { "BepInEx", "dotnet" };

    internal static Action<string> Log = s => { };
    static bool Silent;

    class NotFound : Exception { public NotFound(string m) : base(m) { } }

    // ---------------------------------------------------------------- entry point

    [STAThread]
    static int Main(string[] args)
    {
        string gameDir = null;
        bool uninstall = Path.GetFileName(SelfPath).IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
        bool wait = false;
        foreach (var a in args)
        {
            if (a.Equals("/S", StringComparison.OrdinalIgnoreCase)) Silent = true;
            else if (a.Equals("/UNINSTALL", StringComparison.OrdinalIgnoreCase)) uninstall = true;
            else if (a.Equals("/STEAM-WAIT", StringComparison.OrdinalIgnoreCase)) wait = true;
            else if (a.StartsWith("/GAMEDIR=", StringComparison.OrdinalIgnoreCase)) gameDir = a.Substring(9).Trim('"');
        }
        if (gameDir == null && File.Exists(Path.Combine(Path.GetDirectoryName(SelfPath), GameExe)))
            gameDir = Path.GetDirectoryName(SelfPath);   // the uninstaller copy in the game folder
        if (wait) return WaitAndAddSteamEntry(gameDir);

        if (Silent) return RunSilent(gameDir, uninstall);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm(gameDir, uninstall));
        return 0;
    }

    static int RunSilent(string gameDir, bool uninstall)
    {
        string logPath = Path.Combine(Path.GetTempPath(), "rqvr-setup.log");
        var sb = new StringBuilder();
        Log = s => sb.AppendLine(s);
        int code = 0;
        try
        {
            if (gameDir == null)
            {
                var found = FindGames();
                if (found.Count == 0) throw new NotFound("Couldn't find " + GameName + ". Pass /GAMEDIR=<folder with " + GameExe + ">.");
                gameDir = found[0];
            }
            if (uninstall) Uninstall(gameDir); else Install(gameDir);
        }
        catch (NotFound e) { Log("ERROR: " + e.Message); code = 2; }
        catch (Exception e) { Log("ERROR: " + e.Message); code = 1; }
        try { File.WriteAllText(logPath, sb.ToString()); } catch { }
        Console.Write(sb.ToString());
        return code;
    }

    static string SelfPath { get { return Assembly.GetExecutingAssembly().Location; } }

    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

    static bool? _wine;
    internal static bool IsWine
    {
        get
        {
            if (_wine == null)
            {
                try { var h = GetModuleHandle("ntdll.dll"); _wine = h != IntPtr.Zero && GetProcAddress(h, "wine_get_version") != IntPtr.Zero; }
                catch { _wine = false; }
            }
            return _wine.Value;
        }
    }

    // ---------------------------------------------------------------- finding the game

    // Rolling Quest isn't a Steam game: look where people put it. Folders found through existing Steam entries first.
    internal static List<string> FindGames()
    {
        var games = new List<string>();
        Action<string> add = d =>
        {
            try
            {
                if (string.IsNullOrEmpty(d)) return;
                d = Path.GetFullPath(d.Trim('"')).TrimEnd('\\');
                if (File.Exists(Path.Combine(d, GameExe)) && !games.Exists(g => g.Equals(d, StringComparison.OrdinalIgnoreCase))) games.Add(d);
            }
            catch { }
        };

        // 1. Steam non-Steam games: Rolling Quest itself, or v1.0's launcher inside <game>\BepInEx\plugins\RollingQuestVR
        foreach (var f in SteamShortcuts.AllShortcutFiles())
        {
            try
            {
                foreach (var e in SteamShortcuts.Entries(SteamShortcuts.Load(f)))
                {
                    string exe = (e.S("Exe") ?? e.S("exe") ?? "").Trim('"');
                    if (exe.EndsWith("\\" + GameExe, StringComparison.OrdinalIgnoreCase)) add(Path.GetDirectoryName(exe));
                    else if (exe.EndsWith(@"\RollingQuestVR-launch.exe", StringComparison.OrdinalIgnoreCase))
                        add(Path.Combine(Path.GetDirectoryName(exe), @"..\..\.."));
                }
            }
            catch { }
        }

        // 2. The itch app's install folder and the usual places (two levels deep)
        var bases = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"itch\apps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string r = drive.RootDirectory.FullName;
                bases.Add(Path.Combine(r, "Games")); bases.Add(Path.Combine(r, "itch")); bases.Add(Path.Combine(r, "Program Files"));
                bases.Add(Path.Combine(r, "Program Files (x86)")); bases.Add(r);
            }
            catch { }
        }
        foreach (var b in bases) Search(b, 2, add);
        return games;
    }

    static void Search(string dir, int depth, Action<string> add)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            if (File.Exists(Path.Combine(dir, GameExe))) add(dir);
            if (depth == 0) return;
            foreach (var d in Directory.GetDirectories(dir))
            {
                string n = Path.GetFileName(d);
                if (n.StartsWith("$") || n.Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                Search(d, depth - 1, add);
            }
        }
        catch { }
    }

    internal static bool IsInstalled(string gameDir)
    {
        return !string.IsNullOrEmpty(gameDir) && File.Exists(Path.Combine(gameDir, PluginRel + @"\RollingQuestVR.dll"));
    }

    static void CheckGameDir(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir) || !File.Exists(Path.Combine(gameDir, GameExe)))
            throw new Exception("That folder doesn't contain " + GameExe + ". Pick the " + GameName + " folder.");
        if (Process.GetProcessesByName("RollingQuest").Length > 0) throw new Exception(GameName + " is running. Close it first.");
    }

    // ---------------------------------------------------------------- install

    internal static void Install(string gameDir)
    {
        CheckGameDir(gameDir);
        gameDir = Path.GetFullPath(gameDir).TrimEnd('\\');
        Log("Installing Rolling Quest VR " + ModVersion + " into " + gameDir);
        RemoveOldVersionLeftovers(gameDir, false);

        var asm = Assembly.GetExecutingAssembly();
        int n = 0;
        using (var s = asm.GetManifestResourceStream("bepinex.zip"))
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
        {
            foreach (var e in zip.Entries)
            {
                string dest = Path.Combine(gameDir, e.FullName.Replace('/', '\\'));
                if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                e.ExtractToFile(dest, true);
                n++;
            }
        }
        Directory.CreateDirectory(Path.Combine(gameDir, @"BepInEx\patchers"));
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith("payload/")) continue;
            string dest = Path.Combine(gameDir, res.Substring(8).Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            using (var s = asm.GetManifestResourceStream(res))
            using (var o = File.Create(dest)) s.CopyTo(o);
            n++;
        }
        Log("Copied " + n + " files (BepInEx 6 mod loader, Rolling Quest VR, openvr_api.dll).");

        string uninst = Path.Combine(gameDir, UninstallerName);
        if (!string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(SelfPath, uninst, true);
            Log("Uninstaller: " + uninst);
        }

        if (IsWine)
        {
            Log("");
            Log("Done. Linux / Steam Deck: add " + GameExe + " to Steam yourself (Add a Non-Steam Game), then in its Properties:");
            Log("  1. Compatibility: tick 'Force the use of a specific Steam Play compatibility tool' and pick Proton.");
            Log("  2. Launch Options: " + LinuxLaunchOption);
            Log("  3. Shortcut: tick 'Include in VR Library'.");
            return;
        }
        AddSteamEntryNowOrLater(gameDir, uninst);
        Log("");
        Log("Done. Start '" + EntryName + "' from Steam or SteamVR: it plays in VR (SteamVR starts by itself).");
        Log("The first start takes a minute or two while BepInEx prepares the game (it needs the internet that one time).");
    }

    internal const string LinuxLaunchOption = "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%";

    // Steam's list of non-Steam games can only be changed while Steam is closed: do it now, restart Steam (asked), or
    // let the background helper (the uninstaller copy with /STEAM-WAIT) do it after Steam next closes.
    static void AddSteamEntryNowOrLater(string gameDir, string helperExe)
    {
        if (SteamShortcuts.UserConfigDir() == null) { Log("Steam isn't installed (or never signed in), so no Steam entry was added."); return; }
        try
        {
            if (!SteamRunning()) { AddSteamEntry(gameDir); return; }
            if (!Silent && MessageBox.Show("To add '" + EntryName + "' to your Steam and SteamVR libraries, Steam has to close and reopen once.\r\n\r\nRestart Steam now?\r\n" +
                    "(If you choose No, it's added the next time Steam restarts.)", "Rolling Quest VR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                WithSteamClosed(() => AddSteamEntry(gameDir));
                return;
            }
        }
        catch (Exception e) { Log("Couldn't add the Steam entry: " + e.Message); return; }
        StartDetached(helperExe, "/STEAM-WAIT");
        Log("'" + EntryName + "' appears in your Steam and SteamVR libraries after Steam next restarts.");
    }

    static void AddSteamEntry(string gameDir)
    {
        string cfg = SteamShortcuts.UserConfigDir();
        string file = Path.Combine(cfg, "shortcuts.vdf");
        var root = SteamShortcuts.Load(file);
        bool existed = SteamShortcuts.FindByName(root, EntryName) != null;
        string art = Path.Combine(gameDir, PluginRel + @"\art");
        string icon = File.Exists(Path.Combine(art, "icon.png")) ? Path.Combine(art, "icon.png") : Path.Combine(gameDir, GameExe);
        uint id = SteamShortcuts.Upsert(root, EntryName, Path.Combine(gameDir, GameExe), gameDir, icon, "", true);
        SteamShortcuts.Save(file, root, Backup);

        // Artwork file names Steam looks for: <id>.jpg header, <id>p.jpg cover, <id>_hero.jpg background, <id>_logo.png, <id>_icon.png
        string grid = Path.Combine(cfg, "grid");
        Directory.CreateDirectory(grid);
        var pairs = new[] { new[] { "header.jpg", id + ".jpg" }, new[] { "capsule.jpg", id + "p.jpg" }, new[] { "hero.jpg", id + "_hero.jpg" },
                            new[] { "logo.png", id + "_logo.png" }, new[] { "icon.png", id + "_icon.png" } };
        foreach (var p in pairs)
            if (File.Exists(Path.Combine(art, p[0]))) File.Copy(Path.Combine(art, p[0]), Path.Combine(grid, p[1]), true);
        Log((existed ? "Updated" : "Added") + " '" + EntryName + "' in Steam (with artwork; it's in the VR library).");
    }

    static int WaitAndAddSteamEntry(string gameDir)
    {
        if (gameDir == null || !File.Exists(Path.Combine(gameDir, GameExe))) return 1;
        bool created;
        using (var m = new System.Threading.Mutex(true, @"Local\RQVR-SteamWait", out created))
        {
            if (!created) return 0;
            while (SteamRunning()) System.Threading.Thread.Sleep(2000);
            System.Threading.Thread.Sleep(2000);
            try { AddSteamEntry(gameDir); } catch { return 1; }
            return 0;
        }
    }

    // ---------------------------------------------------------------- uninstall

    internal static void Uninstall(string gameDir)
    {
        CheckGameDir(gameDir);
        gameDir = Path.GetFullPath(gameDir).TrimEnd('\\');
        Log("Removing Rolling Quest VR from " + gameDir);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UninstallerName)))   // a waiting helper
            try { if (p.Id != Process.GetCurrentProcess().Id) p.Kill(); } catch { }
        RemoveOldVersionLeftovers(gameDir, true);
        if (!IsWine) RemoveSteamEntry(gameDir);

        TryDeleteDir(Path.Combine(gameDir, PluginRel));
        string plugins = Path.Combine(gameDir, @"BepInEx\plugins");
        bool others = Directory.Exists(plugins) && Directory.GetFileSystemEntries(plugins).Length > 0;
        if (!others)
        {
            foreach (var d in LoaderDirs) TryDeleteDir(Path.Combine(gameDir, d));
            foreach (var f in LoaderFiles) TryDeleteFile(Path.Combine(gameDir, f));
            Log("Removed Rolling Quest VR and the BepInEx mod loader.");
        }
        else Log("Removed Rolling Quest VR. Kept the BepInEx mod loader: other mods use it.");

        string uninst = Path.Combine(gameDir, UninstallerName);
        if (string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del \"" + uninst + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }
        else TryDeleteFile(uninst);
        Log("");
        Log("Done. Your Rolling Quest saves are untouched.");
    }

    // Only our entry for this game folder (not one for another copy of the game).
    static void RemoveSteamEntry(string gameDir)
    {
        string cfg = SteamShortcuts.UserConfigDir();
        if (cfg == null) return;
        var entry = SteamShortcuts.FindByName(SteamShortcuts.Load(Path.Combine(cfg, "shortcuts.vdf")), EntryName);
        string exe = entry == null ? null : (entry.S("Exe") ?? entry.S("exe") ?? "").Trim('"');
        if (exe == null || !string.Equals(Path.GetFullPath(Path.GetDirectoryName(exe)).TrimEnd('\\'), gameDir, StringComparison.OrdinalIgnoreCase)) return;
        Action remove = () =>
        {
            string file = Path.Combine(cfg, "shortcuts.vdf");
            var root = SteamShortcuts.Load(file);
            uint id = SteamShortcuts.Remove(root, EntryName);
            SteamShortcuts.Save(file, root, Backup);
            string grid = Path.Combine(cfg, "grid");
            if (id != 0 && Directory.Exists(grid)) foreach (var f in Directory.GetFiles(grid, id + "*")) TryDeleteFile(f);
            Log("Removed '" + EntryName + "' from Steam.");
        };
        try
        {
            if (!SteamRunning()) { remove(); return; }
            if (!Silent && MessageBox.Show("To remove '" + EntryName + "' from your Steam library, Steam has to close and reopen once.\r\n\r\nRestart Steam now?",
                    "Rolling Quest VR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                WithSteamClosed(remove);
                return;
            }
        }
        catch (Exception e) { Log("Couldn't remove the Steam entry: " + e.Message); }
        Log("Remove '" + EntryName + "' from Steam yourself: right-click it > Manage > Remove non-Steam game from your library.");
    }

    // ---------------------------------------------------------------- leftovers of v1.0 (launcher, SteamVR entry, shortcuts)

    static void RemoveOldVersionLeftovers(string gameDir, bool uninstalling)
    {
        string pd = Path.Combine(gameDir, PluginRel);
        foreach (var f in new[] { "RollingQuestVR-launch.exe", "SteamShortcut.exe", "add-to-steam.ps1", "uninstall.ps1", "launcher.log", "steam_gameid.txt" })
            TryDeleteFile(Path.Combine(pd, f));
        foreach (var f in new[] { "Rolling Quest VR - Add to Steam.bat", "Rolling Quest VR - Uninstall.bat", "Rolling Quest VR - README.txt" })
            TryDeleteFile(Path.Combine(gameDir, f));
        if (!IsWine)
        {
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), EntryName + ".lnk");
            if (File.Exists(lnk)) { TryDeleteFile(lnk); }
        }

        // v1.0's own SteamVR library entry. SteamVR keeps its list in Steam\config\appconfig.json and rewrites it on
        // exit, so it's only edited while SteamVR is closed; otherwise the mod removes it the next time it runs in VR.
        string manifest = Path.Combine(pd, "rollingquestvr.vrmanifest");
        if (File.Exists(manifest))
        {
            if (Process.GetProcessesByName("vrserver").Length == 0)
            {
                if (RemoveFromSteamVRConfig(manifest)) Log("Removed v1.0's separate 'Rolling Quest VR' entry from SteamVR's library.");
                TryDeleteFile(manifest);
            }
            else if (uninstalling)
                Log("Note: SteamVR is running, so v1.0's separate SteamVR library entry stays. Close SteamVR and run the uninstaller again to remove it.");
        }
    }

    static bool RemoveFromSteamVRConfig(string manifest)
    {
        try
        {
            string steam = SteamShortcuts.SteamPath();
            if (steam == null) return false;
            string cfg = Path.Combine(steam, @"config\appconfig.json");
            if (!File.Exists(cfg)) return false;
            string text = File.ReadAllText(cfg);
            var arr = System.Text.RegularExpressions.Regex.Match(text, "(\"manifest_paths\"\\s*:\\s*\\[)(.*?)(\\])", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!arr.Success) return false;
            var keep = new List<string>();
            bool removed = false;
            string want = Path.GetFullPath(manifest);
            foreach (System.Text.RegularExpressions.Match e in System.Text.RegularExpressions.Regex.Matches(arr.Groups[2].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                string p = e.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                bool same;
                try { same = string.Equals(Path.GetFullPath(p), want, StringComparison.OrdinalIgnoreCase); } catch { same = false; }
                if (same) removed = true; else keep.Add(e.Value);
            }
            if (!removed) return false;
            if (!File.Exists(cfg + Backup)) File.Copy(cfg, cfg + Backup);
            string body = keep.Count == 0 ? "" : "\n      " + string.Join(",\n      ", keep.ToArray()) + "\n   ";
            File.WriteAllText(cfg, text.Substring(0, arr.Index) + arr.Groups[1].Value + body + arr.Groups[3].Value + text.Substring(arr.Index + arr.Length));
            return true;
        }
        catch (Exception e) { Log("Couldn't edit SteamVR's app list: " + e.Message); return false; }
    }

    // ---------------------------------------------------------------- Steam process helpers

    static bool SteamRunning() { return Process.GetProcessesByName("steam").Length > 0; }

    static void WithSteamClosed(Action work)
    {
        string steam = SteamShortcuts.SteamPath();
        bool wasRunning = SteamRunning();
        if (wasRunning)
        {
            Log("Closing Steam...");
            Process.Start(Path.Combine(steam, "steam.exe"), "-shutdown");
            var sw = Stopwatch.StartNew();
            while (SteamRunning())
            {
                if (sw.Elapsed.TotalSeconds > 90) throw new Exception("Steam didn't close. Close it yourself and run this again.");
                System.Threading.Thread.Sleep(300);
                Application.DoEvents();
            }
            System.Threading.Thread.Sleep(1500);
        }
        try { work(); }
        finally { if (wasRunning) { Log("Starting Steam..."); try { Process.Start(Path.Combine(steam, "steam.exe")); } catch { } } }
    }

    // Through WMI, so it isn't a child of this process (PowerShell's Start-Process -Wait, used by Scoop, would wait for it).
    static void StartDetached(string exe, string args)
    {
        try
        {
            string cmd = "\"" + exe + "\" " + args;
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"([wmiclass]'Win32_Process').Create('" + cmd.Replace("'", "''").Replace("\"", "\\\"") + "') | Out-Null\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using (var p = Process.Start(psi)) p.WaitForExit(15000);
        }
        catch { }
    }

    static void TryDeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch (Exception e) { Log("Couldn't delete " + p + ": " + e.Message); } }
    static void TryDeleteDir(string p) { try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch (Exception e) { Log("Couldn't delete " + p + ": " + e.Message); } }
}

// ---------------------------------------------------------------- window

class SetupForm : Form
{
    readonly ComboBox _dir = new ComboBox();
    readonly TextBox _log = new TextBox();
    readonly Button _install = new Button(), _uninstall = new Button(), _close = new Button(), _browse = new Button(), _copy = new Button();

    public SetupForm(string gameDir, bool uninstallMode)
    {
        Text = "Rolling Quest VR " + RqvrSetup.ModVersion + (uninstallMode ? " - Uninstall" : " - Setup");
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(620, 400);
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

        var title = new Label { Text = "VR mod for Rolling Quest", Font = new Font("Segoe UI", 12f, FontStyle.Bold), AutoSize = true, Location = new Point(12, 10) };
        var lbl = new Label { Text = "Game folder:", AutoSize = true, Location = new Point(12, 48) };
        _dir.Location = new Point(12, 68); _dir.Width = 500; _dir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _browse.Text = "Browse..."; _browse.Location = new Point(520, 67); _browse.Width = 88; _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical; _log.BackColor = SystemColors.Window;
        _log.Location = new Point(12, 104); _log.Size = new Size(596, 244); _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _install.Text = "Install"; _uninstall.Text = "Uninstall"; _close.Text = "Close"; _copy.Text = "Copy launch option";
        foreach (var b in new[] { _install, _uninstall, _close }) { b.Width = 96; b.Height = 30; b.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; }
        _close.Location = new Point(512, 358); _uninstall.Location = new Point(408, 358); _install.Location = new Point(304, 358);
        _copy.Width = 150; _copy.Height = 30; _copy.Location = new Point(12, 358); _copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; _copy.Visible = false;
        Controls.AddRange(new Control[] { title, lbl, _dir, _browse, _log, _install, _uninstall, _close, _copy });

        RqvrSetup.Log = s => { _log.AppendText(s + "\r\n"); _log.Update(); };
        _browse.Click += (s, e) =>
        {
            using (var d = new FolderBrowserDialog { Description = "Pick the Rolling Quest folder (the one with RollingQuest.exe)" })
                if (d.ShowDialog(this) == DialogResult.OK) { _dir.Text = d.SelectedPath; UpdateButtons(); }
        };
        _dir.TextChanged += (s, e) => UpdateButtons();
        _close.Click += (s, e) => Close();
        _copy.Click += (s, e) => { try { Clipboard.SetText(RqvrSetup.LinuxLaunchOption); } catch { } };
        _install.Click += (s, e) => Run(() => RqvrSetup.Install(_dir.Text.Trim()), true);
        _uninstall.Click += (s, e) => Run(() => RqvrSetup.Uninstall(_dir.Text.Trim()), false);

        Cursor = Cursors.WaitCursor;
        var found = RqvrSetup.FindGames();
        Cursor = Cursors.Default;
        foreach (var g in found) _dir.Items.Add(g);
        if (!string.IsNullOrEmpty(gameDir)) _dir.Text = gameDir;
        else if (found.Count > 0) _dir.SelectedIndex = 0;

        if (uninstallMode) RqvrSetup.Log("Click Uninstall to remove Rolling Quest VR from this game folder.");
        else if (found.Count == 0) RqvrSetup.Log("Couldn't find Rolling Quest. Click Browse and pick the game folder (the one with RollingQuest.exe).");
        else
        {
            RqvrSetup.Log("Found Rolling Quest" + (found.Count > 1 ? " (" + found.Count + " copies; pick one above)" : "") + ".");
            RqvrSetup.Log("Click Install. It also adds 'Rolling Quest VR' to your Steam and SteamVR libraries.");
        }
        if (RqvrSetup.IsWine) RqvrSetup.Log("(Running under Proton/Wine.)");
        UpdateButtons();
        AcceptButton = uninstallMode ? _uninstall : _install;
    }

    void UpdateButtons()
    {
        string d = _dir.Text.Trim();
        bool hasGame = d.Length > 0 && File.Exists(Path.Combine(d, "RollingQuest.exe"));
        _install.Enabled = hasGame;
        _install.Text = RqvrSetup.IsInstalled(d) ? "Reinstall" : "Install";
        _uninstall.Enabled = hasGame && RqvrSetup.IsInstalled(d);
    }

    void Run(Action action, bool installing)
    {
        _install.Enabled = _uninstall.Enabled = _browse.Enabled = _dir.Enabled = false;
        _log.Clear();
        Cursor = Cursors.WaitCursor;
        try
        {
            action();
            if (installing && RqvrSetup.IsWine) _copy.Visible = true;
        }
        catch (Exception e) { RqvrSetup.Log(""); RqvrSetup.Log("Failed: " + e.Message); }
        finally { Cursor = Cursors.Default; _browse.Enabled = _dir.Enabled = true; UpdateButtons(); }
    }
}
