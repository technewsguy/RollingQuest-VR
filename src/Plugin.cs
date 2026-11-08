// Rolling Quest VR - BepInEx 6 (IL2CPP) plugin entry point.
using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace RollingQuestVR
{
    [BepInPlugin("rollingquestvr.mod", "Rolling Quest VR", "1.0.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource L;
        internal static ConfigEntry<float> WorldScale;
        internal static ConfigEntry<KeyCode> KeyRecenter, KeyView;
        internal static ConfigEntry<bool> Diorama, DioramaSteering;
        internal static ConfigEntry<float> DioramaSize, DioramaDistance, DioramaHeight;
        internal static ConfigEntry<bool> EnableVR, DesktopUi;
        internal static ConfigEntry<float> UiWidth, UiDistance, UiHeight, UiFollowAngle;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        public override void Load()
        {
            L = Log;
            EnableVR = Config.Bind("General", "EnableVR", true, "false = play the normal flat game.");
            WorldScale = Config.Bind("Camera", "WorldScale", 1f, "How big you are relative to the level (bigger = the level looks smaller).");
            KeyRecenter = Config.Bind("Keys", "Recenter", KeyCode.F8, "Recenter the VR view.");
            UiWidth = Config.Bind("UI", "Width", 1.6f, "Width of the floating menu/HUD screen (meters).");
            UiDistance = Config.Bind("UI", "Distance", 1.4f, "Distance of the floating screen from your head (meters).");
            UiHeight = Config.Bind("UI", "VerticalOffset", -0.1f, "Height of the floating screen relative to eye level (meters).");
            UiFollowAngle = Config.Bind("UI", "FollowAngle", 35f, "When you look more than this many degrees away, the screen turns to face you again.");
            Diorama = Config.Bind("Camera", "Diorama", false, "true = the level is a model on a table in front of you; false = follow behind the ball. Share button / F9 switches.");
            DioramaSteering = Config.Bind("Diorama", "PushToSteer", true, "true = in diorama mode the ball goes where you push the stick (the model keeps its direction; right stick flicks turn it 90 degrees); false = the game's own turn/forward controls, with the model turning so the ball always faces away from you.");
            DioramaSize = Config.Bind("Diorama", "Size", 1.0f, "Width of the level model (meters). The right stick up/down changes it in game.");
            DioramaDistance = Config.Bind("Diorama", "Distance", 0.9f, "Distance from your head to the middle of the model (meters).");
            DioramaHeight = Config.Bind("Diorama", "Height", -0.7f, "Height of the middle of the model relative to eye level (meters).");
            KeyView = Config.Bind("Keys", "SwitchView", KeyCode.F9, "Switch between follow-behind and diorama.");
            DesktopUi = Config.Bind("UI", "ShowOnDesktop", true, "Also show the menus and HUD in the desktop window.");
            if (!EnableVR.Value) { L.LogInfo("EnableVR=false: flat game."); return; }

            // openvr_api.dll ships next to this plugin; load it so the C# bindings' DllImport("openvr_api") finds it.
            string dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (LoadLibrary(Path.Combine(dir, "openvr_api.dll")) == IntPtr.Zero) L.LogError("Couldn't load openvr_api.dll from " + dir);

            try { new HarmonyLib.Harmony("rollingquestvr.mod").PatchAll(typeof(DioramaSteer)); }
            catch (Exception e) { L.LogError("Diorama steering unavailable: " + e); }
            ClassInjector.RegisterTypeInIl2Cpp<VrRenderer>();
            var go = new GameObject("RQVR_Renderer");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<VrRenderer>();
            L.LogInfo("Rolling Quest VR loaded.");
        }
    }
}
