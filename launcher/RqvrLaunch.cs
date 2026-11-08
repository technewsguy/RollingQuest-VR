// "Rolling Quest VR" shortcut. Lives in BepInEx\plugins\RollingQuestVR next to openvr_api.dll.
//  1. Starts SteamVR if needed.
//  2. Asks SteamVR to launch the "Rolling Quest VR" library entry, which runs this launcher with --noop. SteamVR closes
//     its dashboard ("Resume Game" menu) when it launches an app; the placeholder stays connected as the scene app
//     until the game takes over, because with no scene app SteamVR brings the dashboard back.
//  3. Starts the game itself. The game must not be started by SteamVR: SteamVR's processes carry Steam's overlay,
//     which then hooks the game and lets Steam Input take the PS4 controller away from it.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Valve.VR;

static class RqvrLaunch
{
    const string AppKey = "rollingquestvr.game";
    static readonly string Here = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string GameDir = Path.GetFullPath(Path.Combine(Here, @"..\..\.."));   // <game>\BepInEx\plugins\RollingQuestVR
    static readonly string Exe = Path.Combine(GameDir, "RollingQuest.exe");
    static readonly string Self = Path.Combine(Here, "RollingQuestVR-launch.exe");
    static bool StartedBySteam;

    static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(Here, "launcher.log"), DateTime.Now.ToString("HH:mm:ss.f ") + s + "\r\n"); } catch (Exception) { }
    }

    static int Main(string[] args)
    {
        string steamGame = Environment.GetEnvironmentVariable("SteamGameId") ?? "";
        Log("start " + string.Join(" ", args) + (steamGame.Length > 0 ? "  (Steam game id " + steamGame + ")" : ""));
        if (args.Length > 0 && args[0] == "--noop") return HoldUntilGameConnects();
        if (Process.GetProcessesByName("RollingQuest").Length > 0) { Log("game already running"); FocusGame(); return 0; }

        // Started by Steam as the "Rolling Quest VR" shortcut (not via SteamVR's own app): Steam treats the game as
        // that shortcut, so Steam Input hands it the controller. Started any other way (desktop shortcut, SteamVR's
        // library entry) while that Steam shortcut exists: go through Steam the same way, so it behaves identically.
        StartedBySteam = steamGame.Length > 0 && !steamGame.StartsWith("250820");
        string gidFile = Path.Combine(Here, "steam_gameid.txt");
        if (!StartedBySteam && File.Exists(gidFile) && Process.GetProcessesByName("steam").Length > 0)
        {
            string gid = File.ReadAllText(gidFile).Trim();
            Log("handing over to the Steam shortcut " + gid);
            Process.Start("steam://rungameid/" + gid);
            return 0;
        }
        try
        {
            if (Connect())
            {
                try
                {
                    string manifest = WriteManifest();
                    Log("manifest: " + OpenVR.Applications.AddApplicationManifest(manifest, false));
                    var e = OpenVR.Applications.LaunchApplication(AppKey);
                    for (int i = 0; i < 10 && e == EVRApplicationError.IPCFailed; i++) { Thread.Sleep(1500); e = OpenVR.Applications.LaunchApplication(AppKey); }
                    Log("LaunchApplication (closes the dashboard): " + e);
                }
                finally { OpenVR.Shutdown(); }
            }
            else Log("SteamVR unavailable");
        }
        catch (Exception ex) { Log("error: " + ex); }
        return Run();
    }

    // SteamVR library entry: runs this launcher as a placeholder (rewritten each time so it follows the install folder).
    static string WriteManifest()
    {
        string path = Path.Combine(Here, "rollingquestvr.vrmanifest");
        File.WriteAllText(path,
            "{\n  \"source\": \"builtin\",\n  \"applications\": [{\n" +
            "    \"app_key\": \"" + AppKey + "\",\n" +
            "    \"launch_type\": \"binary\",\n" +
            "    \"binary_path_windows\": \"" + Self.Replace("\\", "\\\\") + "\",\n" +
            "    \"arguments\": \"--noop\",\n" +
            "    \"working_directory\": \"" + Here.TrimEnd('\\').Replace("\\", "\\\\") + "\",\n" +
            "    \"is_dashboard_overlay\": false,\n" +
            (File.Exists(Path.Combine(Here, @"art\header.jpg")) ? "    \"image_path\": \"" + Path.Combine(Here, @"art\header.jpg").Replace("\\", "\\\\") + "\",\n" : "") +
            "    \"strings\": { \"en_us\": { \"name\": \"Rolling Quest VR\", \"description\": \"Rolling Quest with the Rolling Quest VR mod. Start it from the Rolling Quest VR desktop shortcut.\" } }\n" +
            "  }]\n}\n");
        return path;
    }

    // Connects to SteamVR as a background app, starting SteamVR (through Steam) first if it isn't running.
    static bool Connect()
    {
        var err = EVRInitError.None;
        if (Process.GetProcessesByName("vrserver").Length > 0)
        {
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
            if (err == EVRInitError.None) return true;
        }
        Log("starting SteamVR");
        try { Process.Start("steam://rungameid/250820"); } catch (Exception) { return false; }
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(2000);
            if (Process.GetProcessesByName("vrserver").Length == 0) continue;
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
            if (err == EVRInitError.None) { Thread.Sleep(4000); return true; }
        }
        return false;
    }

    // The placeholder SteamVR "launched": stay the scene app until the game has the headset (or gives up).
    static int HoldUntilGameConnects()
    {
        var err = EVRInitError.None;
        OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
        if (err != EVRInitError.None) { Log("hold: init failed " + err); return 0; }
        try
        {
            var sw = Stopwatch.StartNew();
            bool selfStarted = false;
            while (sw.Elapsed.TotalSeconds < 240)
            {
                Thread.Sleep(500);
                var game = Process.GetProcessesByName("RollingQuest");
                // Started from SteamVR's library directly (no launcher is starting the game): start it the normal way.
                if (!selfStarted && game.Length == 0 && sw.Elapsed.TotalSeconds > 5 && Process.GetProcessesByName("RollingQuestVR-launch").Length < 2)
                {
                    selfStarted = true;
                    string gidFile = Path.Combine(Here, "steam_gameid.txt");
                    if (File.Exists(gidFile)) { Log("hold: starting the game through Steam"); Process.Start("steam://rungameid/" + File.ReadAllText(gidFile).Trim()); }
                    else { Log("hold: starting the game"); Run(); }
                }
                if (game.Length == 0 && sw.Elapsed.TotalSeconds > 90) { Log("hold: no game after 90 s"); break; }
                uint focus = OpenVR.Compositor != null ? OpenVR.Compositor.GetCurrentSceneFocusProcess() : 0;
                bool gameHasFocus = false;
                foreach (var p in game) if (p.Id == focus) gameHasFocus = true;
                if (gameHasFocus) { Log("hold: game connected after " + sw.Elapsed.TotalSeconds.ToString("0") + " s"); break; }
            }
        }
        finally { OpenVR.Shutdown(); }
        return 0;
    }

    static int Run()
    {
        var psi = new ProcessStartInfo(Exe) { WorkingDirectory = GameDir, UseShellExecute = false };
        // Never let the game look like SteamVR itself (Steam Input would then take the controller away from it).
        if (!StartedBySteam)
            foreach (var v in new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId", "SteamClientLaunch", "SteamEnv" })
                psi.EnvironmentVariables.Remove(v);
        AllowSetForegroundWindow(-1);
        var game = Process.Start(psi);
        Log("game started");
        FocusGame();
        // Started by Steam: stay until the game exits, so Steam shows it as running (and counts play time).
        if (StartedBySteam && game != null) { game.WaitForExit(); Log("game exited"); }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    // Bring the game window (not the BepInEx console) to the front so the keyboard works too.
    static void FocusGame()
    {
        for (int i = 0; i < 120; i++)
        {
            Thread.Sleep(1000);
            foreach (var p in Process.GetProcessesByName("RollingQuest"))
            {
                p.Refresh();
                if (p.MainWindowHandle == IntPtr.Zero || p.MainWindowTitle != "RollingQuest") continue;
                ShowWindow(p.MainWindowHandle, 9);
                Log("focus game window: " + SetForegroundWindow(p.MainWindowHandle));
                return;
            }
        }
    }
}
