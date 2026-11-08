// Renders each eye with its own camera into a texture and hands the frames to SteamVR directly (the approach that
// worked for NSLT-VR). Unity has no VR support compiled into this game, so the mod drives SteamVR itself:
//   onBeforeRender (main thread): latest head pose -> eye cameras follow the game's camera + your head
//   URP renders the eye cameras into their textures along with everything else
//   end of frame: Submit both textures + WaitGetPoses on Unity's render thread (GL.IssuePluginEvent)
using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using Valve.VR;

namespace RollingQuestVR
{
    public class VrRenderer : MonoBehaviour
    {
        public VrRenderer(IntPtr ptr) : base(ptr) { }

        private delegate void RenderEventFn(int eventId);
        private static readonly RenderEventFn s_fn = OnRenderEvent;
        private static readonly IntPtr s_fnPtr = Marshal.GetFunctionPointerForDelegate(s_fn);
        private static readonly IntPtr[] s_tex = new IntPtr[2];
        private static VRTextureBounds_t s_bounds;
        private static volatile bool s_quitting;
        private static int s_submits, s_submitErrors;
        private static EVRCompositorError s_lastErr;
        private static readonly TrackedDevicePose_t[] s_poseBuf = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private static readonly TrackedDevicePose_t[] s_gameBuf = new TrackedDevicePose_t[0];

        private readonly TrackedDevicePose_t[] _render = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private readonly TrackedDevicePose_t[] _game = new TrackedDevicePose_t[0];
        private readonly Camera[] _eyes = new Camera[2];
        private bool _inputWarned;

        // ---- Diorama: the level as a model on a table in front of you ----
        private float _nextBounds, _dioYaw, _dioZoom = 1f;
        private Quaternion _dioRot = Quaternion.identity;
        private bool _dioRotSet;
        private Vector3 _dioPivot;
        private Vector3 _dispFwd = Vector3.forward, _dispUp = Vector3.up;   // push-to-steer: the model's "away" and "up" in the level
        private bool _dispSet;
        private int _flick;
        private bool _flickHeld;
        internal static Vector3 DisplayForward = Vector3.forward, DisplayUp = Vector3.up;
        internal static bool DisplaySet;

        // No ball (menus): look at the scene from the same side as the game's camera, so e.g. the title reads correctly.
        private Quaternion MenuFacing()
        {
            if (_game3d == null) return Quaternion.identity;
            Vector3 f = _game3d.transform.forward; f.y = 0f;
            // (The menu's block lettering reads correctly from the side opposite to where the game's menu camera looks.)
            return f.sqrMagnitude > 0.01f ? Quaternion.LookRotation(-NearestAxis(f), Vector3.up) : Quaternion.identity;
        }

        // The level's grid axis (±X, ±Y, ±Z) closest to v.
        private static Vector3 NearestAxis(Vector3 v)
        {
            float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
            if (ax >= ay && ax >= az) return new Vector3(Mathf.Sign(v.x), 0f, 0f);
            if (ay >= az) return new Vector3(0f, Mathf.Sign(v.y), 0f);
            return new Vector3(0f, 0f, Mathf.Sign(v.z));
        }
        private Bounds _level;
        private bool _hasLevel;
        internal static bool DioramaActive;     // the level is currently shown as a model
        internal static Transform ViewEye;      // left eye camera (where you're looking from, in game-world space)
        private string _boundsScene;

        // The level's blocks (cube-side layers 6-11 and the Block layer 17).
        private void UpdateLevelBounds()
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            bool found = false;
            Bounds b = default;
            foreach (var r in FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                int l = r.gameObject.layer;
                if (!((l >= 6 && l <= 11) || l == 17)) continue;
                if (!found) { b = r.bounds; found = true; } else b.Encapsulate(r.bounds);
            }
            if (!found) { _hasLevel = false; return; }
            // Moving parts shouldn't make the model wobble: only follow real changes (new level, or the level growing).
            bool changed = !_hasLevel || scene != _boundsScene
                || (b.center - _level.center).magnitude > 0.05f * _level.size.magnitude
                || Mathf.Abs(b.size.magnitude - _level.size.magnitude) > 0.05f * _level.size.magnitude;
            if (!changed) return;
            if (scene != _boundsScene)
            {
                // Start each level seen from the same side as the game's own camera.
                _dioYaw = 0f;
                _dioRotSet = false;
                _dispSet = false;
                if (Plugin.DioramaSteering.Value && _game3d != null)
                {
                    Vector3 f = _game3d.transform.forward; f.y = 0f;
                    if (f.sqrMagnitude > 0.01f) _dioYaw = Quaternion.LookRotation(f).eulerAngles.y;
                }
            }
            _level = b; _hasLevel = true; _boundsScene = scene;
            Plugin.L.LogInfo("Level bounds in '" + scene + "': center " + b.center + " size " + b.size);
        }

        // Development aid (F11): saves the textures of the UI images on screen (e.g. the title logo) as PNGs.
        private void DumpUiImages()
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "rqvr_shots", "ui");
            Directory.CreateDirectory(dir);
            var done = new System.Collections.Generic.HashSet<string>();
            foreach (var img in FindObjectsOfType<UnityEngine.UI.Image>())
            {
                try
                {
                    if (img == null || img.sprite == null || img.sprite.texture == null) continue;
                    var tex = img.sprite.texture;
                    if (tex.width < 128 || !done.Add(tex.name)) continue;
                    var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(tex, rt);
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                    copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                    copy.Apply();
                    RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                    string safe = string.Join("_", tex.name.Split(Path.GetInvalidFileNameChars()));
                    File.WriteAllBytes(Path.Combine(dir, safe + ".png"), ImageConversion.EncodeToPNG(copy));
                    Plugin.L.LogInfo("UI image: '" + img.name + "' sprite '" + img.sprite.name + "' texture '" + tex.name + "' " + tex.width + "x" + tex.height);
                }
                catch (Exception e) { Plugin.L.LogWarning("UI image dump: " + e.Message); }
            }
        }

        // Diagnostics: which controllers the game sees, and which of them are actually sending button presses.
        private string _devList = "";
        private float _nextDevCheck;
        private readonly System.Collections.Generic.Dictionary<string, int> _presses = new System.Collections.Generic.Dictionary<string, int>();
        private void WatchControllers()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                foreach (var pad in UnityEngine.InputSystem.Gamepad.all)
                {
                    if (pad == null || !pad.wasUpdatedThisFrame) continue;
                    bool any = pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame || pad.buttonWest.wasPressedThisFrame
                        || pad.buttonNorth.wasPressedThisFrame || pad.startButton.wasPressedThisFrame || pad.selectButton.wasPressedThisFrame
                        || pad.dpad.up.wasPressedThisFrame || pad.dpad.down.wasPressedThisFrame;
                    if (!any) continue;
                    string k = pad.displayName;
                    _presses[k] = (_presses.TryGetValue(k, out int n) ? n : 0) + 1;
                    if (n == 0) Plugin.L.LogInfo("Controller input from '" + k + "' (current gamepad: '" + UnityEngine.InputSystem.Gamepad.current?.displayName + "')");
                }
                if (now < _nextDevCheck) return;
                _nextDevCheck = now + 1f;
                var sb = new System.Text.StringBuilder();
                foreach (var d in UnityEngine.InputSystem.InputSystem.devices)
                    if (d != null && !(d is UnityEngine.InputSystem.Keyboard) && !(d is UnityEngine.InputSystem.Mouse))
                        sb.Append(" '").Append(d.displayName).Append("' (").Append(d.layout).Append(d.enabled ? "" : ", disabled").Append(")");
                string list = sb.ToString();
                if (list != _devList) { _devList = list; Plugin.L.LogInfo("Controllers the game sees:" + (list.Length > 0 ? list : " none")); }
            }
            catch (Exception e) { if (!_inputWarned) { _inputWarned = true; Plugin.L.LogWarning("Controller check failed: " + e.Message); } }
        }

        private void DioramaControls()
        {
            bool toggle = Input.GetKeyDown(Plugin.KeyView.Value);
            Vector2 stick = Vector2.zero;
            try
            {
                var pad = UnityEngine.InputSystem.Gamepad.current;
                if (pad != null)
                {
                    if (pad.selectButton.wasPressedThisFrame) toggle = true;   // Share on a PS4 controller
                    stick = pad.rightStick.ReadValue();
                }
            }
            catch (Exception) { }
            if (toggle)
            {
                Plugin.Diorama.Value = !Plugin.Diorama.Value;
                Plugin.L.LogInfo("View: " + (Plugin.Diorama.Value ? "diorama" : "follow behind"));
            }
            if (!Plugin.Diorama.Value) return;
            float dt = Time.unscaledDeltaTime;
            if (Plugin.DioramaSteering.Value)
            {
                // A flick turns the model a quarter turn (keeps the grid lined up with you).
                if (!_flickHeld && Mathf.Abs(stick.x) > 0.7f) { _flick = stick.x > 0 ? -1 : 1; _flickHeld = true; }
                else if (Mathf.Abs(stick.x) < 0.3f) _flickHeld = false;
            }
            else if (Mathf.Abs(stick.x) > 0.2f) _dioYaw += stick.x * 90f * dt;                  // turn the model
            if (Mathf.Abs(stick.y) > 0.2f) _dioZoom = Mathf.Clamp(_dioZoom * Mathf.Exp(stick.y * 0.8f * dt), 0.3f, 4f);   // bigger/smaller
        }

        // World units per meter in diorama mode.
        private float DioramaScale()
        {
            float extent = Mathf.Max(_level.size.x, _level.size.z, _level.size.y * 0.7f, 1f);
            return extent / (Mathf.Max(0.1f, Plugin.DioramaSize.Value) * _dioZoom);
        }
        private readonly Camera[] _uiEyes = new Camera[2];    // the floating menu screen, drawn last into each eye so the level never covers it
        private readonly Camera[] _skyEyes = new Camera[2];   // the game's "3D skybox" backdrop, drawn first into each eye
        private Camera _gameSky;
        private readonly RenderTexture[] _rt = new RenderTexture[2];
        private readonly Vector3[] _eyePos = new Vector3[2];
        private readonly float[][] _tan = new float[2][];
        private GameObject _root;
        private UiPanel _ui;
        private Camera _game3d;
        private bool _ok, _initTried;
        private float _nextLog = 10f, _nextScan;
        private int _frames;

        private bool _hookedBeforeRender;

        private void Start()
        {
            try
            {
                Application.add_onBeforeRender(DelegateSupport.ConvertDelegate<UnityAction>(new Action(BeforeRender)));
                _hookedBeforeRender = true;
                Plugin.L.LogInfo("Hooked Application.onBeforeRender");
            }
            catch (Exception e) { Plugin.L.LogWarning("onBeforeRender unavailable (" + e.Message + "); updating in LateUpdate instead"); }
            try { this.StartCoroutine(EndOfFrameLoop().WrapToIl2Cpp()); Plugin.L.LogInfo("End-of-frame loop started"); }
            catch (Exception e) { Plugin.L.LogError("End-of-frame coroutine failed: " + e); }
        }

        private void LateUpdate()
        {
            if (!_hookedBeforeRender) BeforeRender();
        }

        private void InitVR()
        {
            _initTried = true;
            var err = EVRInitError.None;
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
            if (err != EVRInitError.None) { Plugin.L.LogError("OpenVR.Init failed: " + err); return; }
            OpenVR.Compositor.SetTrackingSpace(ETrackingUniverseOrigin.TrackingUniverseSeated);
            uint w = 0, h = 0;
            OpenVR.System.GetRecommendedRenderTargetSize(ref w, ref h);

            _root = new GameObject("RQVR_Root");
            DontDestroyOnLoad(_root);
            _ui = new UiPanel(_root.transform);
            for (int i = 0; i < 2; i++)
            {
                var eye = i == 0 ? EVREye.Eye_Left : EVREye.Eye_Right;
                _rt[i] = new RenderTexture((int)w, (int)h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                _rt[i].name = "RQVR_" + eye;
                _rt[i].Create();
                s_tex[i] = _rt[i].GetNativeTexturePtr();

                var go = new GameObject("RQVR_" + eye);
                go.transform.SetParent(_root.transform, false);
                var c = go.AddComponent<Camera>();
                c.targetTexture = _rt[i];
                c.stereoTargetEye = StereoTargetEyeMask.None;
                c.nearClipPlane = 0.02f;
                c.depth = 50 + i;
                _eyes[i] = c;

                var ugo = new GameObject("RQVR_UI_" + eye);
                ugo.transform.SetParent(go.transform, false);
                var uc = ugo.AddComponent<Camera>();
                uc.targetTexture = _rt[i];
                uc.stereoTargetEye = StereoTargetEyeMask.None;
                uc.clearFlags = CameraClearFlags.Depth;
                uc.cullingMask = 1 << UiPanel.PanelLayer;
                uc.nearClipPlane = 0.02f;
                uc.farClipPlane = 100f;
                uc.depth = 60 + i;
                uc.allowHDR = false;
                _uiEyes[i] = uc;

                // Backdrop camera for this eye: same texture, rendered just before the level (lower depth).
                var sgo = new GameObject("RQVR_Sky_" + eye);
                DontDestroyOnLoad(sgo);
                var sc = sgo.AddComponent<Camera>();
                sc.targetTexture = _rt[i];
                sc.stereoTargetEye = StereoTargetEyeMask.None;
                sc.depth = 40 + i;
                sc.enabled = false;
                _skyEyes[i] = sc;

                float l = 0, r = 0, t = 0, b = 0;
                OpenVR.System.GetProjectionRaw(eye, ref l, ref r, ref t, ref b);
                _tan[i] = new[] { l, r, t, b };
                var m = OpenVR.System.GetEyeToHeadTransform(eye);
                _eyePos[i] = new Vector3(m.m3, m.m7, -m.m11);
            }
            // Unity renders into textures bottom-up on D3D11; SteamVR reads them top-down.
            s_bounds = new VRTextureBounds_t { uMin = 0f, uMax = 1f, vMin = 1f, vMax = 0f };
            _ok = true;
            Plugin.L.LogInfo(string.Format("VR ready: {0}x{1} per eye, ipd {2:0.000} m", w, h, _eyePos[1].x - _eyePos[0].x));
            VrGamepad.Init(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "input", "actions.json"));
        }

        private void Update()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            if (!_initTried && Time.realtimeSinceStartup > 8f) InitVR();
            if (!_ok) return;
            float now = Time.realtimeSinceStartup;
            if (now >= _nextScan)
            {
                _nextScan = now + 0.5f;
                var main = Camera.main;
                if (main != null && main != _game3d)
                {
                    _game3d = main;
                    Plugin.L.LogInfo("Following camera '" + main.name + "' clear=" + main.clearFlags + " mask=0x" + main.cullingMask.ToString("X"));
                }
                // The backdrop camera: an enabled game camera drawing before the main one, with a "Skybox" name.
                Camera sky = null;
                foreach (var c in Camera.allCameras)
                    if (c != null && !c.name.StartsWith("RQVR_") && c.name.IndexOf("Skybox", StringComparison.OrdinalIgnoreCase) >= 0) { sky = c; break; }
                if (sky != _gameSky)
                {
                    _gameSky = sky;
                    Plugin.L.LogInfo("Backdrop camera: " + (sky != null ? "'" + sky.name + "' mask=0x" + sky.cullingMask.ToString("X") + " clear=" + sky.clearFlags : "none"));
                }
                // In the headset the game window often isn't the focused desktop window (SteamVR starts it), and Unity's
                // Input System drops gamepad input from unfocused windows by default. Keep the controller working regardless.
                try
                {
                    var settings = UnityEngine.InputSystem.InputSystem.settings;
                    if (settings != null && settings.backgroundBehavior != UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus)
                    {
                        settings.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
                        Plugin.L.LogInfo("Controller input enabled while the game window is unfocused.");
                    }
                }
                catch (Exception e) { if (!_inputWarned) { _inputWarned = true; Plugin.L.LogWarning("Couldn't change input focus behavior: " + e.Message); } }
                if (now >= _nextBounds) { _nextBounds = now + 2f; UpdateLevelBounds(); }
                LogUiChanges();
                if (_ui != null) _ui.Scan();
                UiPanel.KeepOffGameCameras();
            }
            VrGamepad.Update();
            if ((Input.GetKeyDown(Plugin.KeyRecenter.Value) || VrGamepad.RecenterRequested) && OpenVR.Chaperone != null)
            { OpenVR.Chaperone.ResetZeroPose(ETrackingUniverseOrigin.TrackingUniverseSeated); if (_ui != null) _ui.Recenter(); }
            VrGamepad.RecenterRequested = false;
            if (Input.GetKeyDown(KeyCode.F12)) Screenshot();
            if (Input.GetKeyDown(KeyCode.F11)) DumpUiImages();
            DioramaControls();
            WatchControllers();
            if (now >= _nextLog)
            {
                _nextLog = now + 10f;
                Plugin.L.LogInfo(string.Format("fps={0:0} frames/10s={1} submits={2} errors={3} last={4} following={5}",
                    1f / Mathf.Max(0.0001f, Time.smoothDeltaTime), _frames, s_submits, s_submitErrors, s_lastErr, _game3d != null ? _game3d.name : "none"));
                _frames = 0;
            }
        }

        // After all LateUpdates (the game's camera has moved), right before Unity renders.
        private void BeforeRender()
        {
            if (!_ok || s_quitting) return;
            OpenVR.Compositor.GetLastPoses(_render, _game);
            Vector3 headPos = Vector3.zero;
            Quaternion headRot = Quaternion.identity;
            if (_render[0].bPoseIsValid) ToUnity(_render[0].mDeviceToAbsoluteTracking, out headPos, out headRot);

            float scale = Mathf.Max(0.01f, Plugin.WorldScale.Value);
            DioramaActive = Plugin.Diorama.Value && _hasLevel;
            ViewEye = _eyes[0].transform;
            if (DioramaActive)
            {
                // Model on a table: the level's middle sits at a fixed spot in front of where you sat when recentered.
                scale = DioramaScale();
                // The model turns (and tips over for walls/undersides) so the ball always faces away from you, rolling
                // on a surface that's up. Smoothly, so turns and flips read as the model rotating. The right stick adds a turn.
                // The model is also centred on the ball, so it never ends up behind the rest of the level.
                Quaternion target = _dioRot;
                Vector3 pivotTarget = _level.center;
                bool haveBall = DioramaSteer.TryGetBallBasis(out Vector3 bf, out Vector3 bu, out Vector3 bp);
                if (haveBall) pivotTarget = bp;
                bool basisOk = haveBall && Vector3.Cross(bf, bu).sqrMagnitude > 0.01f;
                if (Plugin.DioramaSteering.Value)
                {
                    // Push-to-steer: the model keeps a steady, grid-aligned direction (so the stick always means the same
                    // way on the table) and only tips when the ball moves onto another face, so that face is on top.
                    // The model's direction is set once per level from the level's own grid axes (ball facing away,
                    // its starting face on top) and then never changes on its own, not even when the ball rolls onto a
                    // wall or underneath. Only a right-stick flick turns it.
                    Vector3 upAxis = NearestAxis(bu);
                    if (basisOk && !_dispSet && Vector3.Angle(bu, upAxis) < 15f)
                    {
                        _dispUp = upAxis;
                        _dispFwd = NearestAxis(Vector3.ProjectOnPlane(bf, upAxis));
                        _dispSet = true;
                    }
                    if (_flick != 0 && _dispSet) _dispFwd = NearestAxis(Quaternion.AngleAxis(90f * _flick, _dispUp) * _dispFwd);
                    _flick = 0;
                    target = _dispSet ? Quaternion.LookRotation(_dispFwd, _dispUp) : MenuFacing();
                    DisplayForward = _dispFwd; DisplayUp = _dispUp; DisplaySet = _dispSet;
                }
                else if (basisOk) target = Quaternion.LookRotation(bf, bu);
                else if (!_dioRotSet) target = MenuFacing();
                float dt = Time.unscaledDeltaTime;
                // Big jumps (respawn, teleport, new level) snap; normal rolling is followed closely but smoothly.
                if (!_dioRotSet || (pivotTarget - _dioPivot).magnitude > 0.5f * Mathf.Max(1f, _level.size.magnitude)) _dioPivot = pivotTarget;
                else _dioPivot = Vector3.Lerp(_dioPivot, pivotTarget, 1f - Mathf.Exp(-8f * dt));
                _dioRot = _dioRotSet ? Quaternion.Slerp(_dioRot, target, 1f - Mathf.Exp(-6f * dt)) : target;
                _dioRotSet = true;
                Quaternion rot = Plugin.DioramaSteering.Value ? _dioRot : _dioRot * Quaternion.Euler(0f, -_dioYaw, 0f);
                Vector3 table = new Vector3(0f, Plugin.DioramaHeight.Value, Plugin.DioramaDistance.Value);
                _root.transform.rotation = rot;
                _root.transform.localScale = Vector3.one * scale;
                _root.transform.position = _dioPivot - rot * (table * scale);
            }
            else if (_game3d != null)
            {
                _root.transform.position = _game3d.transform.position;
                _root.transform.rotation = _game3d.transform.rotation;   // follow-behind: the view turns with the game camera
                _root.transform.localScale = Vector3.one * scale;
            }
            if (_ui != null) _ui.Place(headPos, headRot);
            for (int i = 0; i < 2; i++)
            {
                var c = _eyes[i];
                c.transform.localPosition = headPos + headRot * _eyePos[i];
                c.transform.localRotation = headRot;
                bool useSky = _gameSky != null && _gameSky.isActiveAndEnabled;
                if (_game3d != null)
                {
                    c.cullingMask = _game3d.cullingMask & ~(1 << UiPanel.PanelLayer);
                    // With the backdrop drawn first, keep its colour like the game's own camera does (it clears depth only).
                    c.clearFlags = useSky ? CameraClearFlags.Depth
                        : _game3d.clearFlags == CameraClearFlags.Skybox ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                    Color bg = _game3d.backgroundColor; bg.a = 1f;
                    c.backgroundColor = bg;
                    c.farClipPlane = _game3d.farClipPlane;
                }
                // Clip planes are in world units: scale the near plane with the rig so close-up model detail isn't cut.
                c.nearClipPlane = 0.02f * scale;
                if (Plugin.Diorama.Value && _hasLevel) c.farClipPlane = Mathf.Max(c.farClipPlane, 100f * scale);
                c.projectionMatrix = Projection(_tan[i], c.nearClipPlane, c.farClipPlane);
                _uiEyes[i].nearClipPlane = 0.02f * scale;
                _uiEyes[i].farClipPlane = 100f * scale;
                _uiEyes[i].projectionMatrix = Projection(_tan[i], _uiEyes[i].nearClipPlane, _uiEyes[i].farClipPlane);

                var s = _skyEyes[i];
                if (s.enabled != useSky) s.enabled = useSky;
                if (useSky)
                {
                    // The backdrop is a miniature far away: it only turns with your view (position stays at the game's sky camera).
                    s.transform.position = _gameSky.transform.position;
                    s.transform.rotation = _root.transform.rotation * headRot;
                    s.cullingMask = _gameSky.cullingMask;
                    s.clearFlags = _gameSky.clearFlags;
                    Color sbg = _gameSky.backgroundColor; sbg.a = 1f;
                    s.backgroundColor = sbg;
                    s.nearClipPlane = _gameSky.nearClipPlane;
                    s.farClipPlane = _gameSky.farClipPlane;
                    s.projectionMatrix = Projection(_tan[i], s.nearClipPlane, s.farClipPlane);
                }
            }
            _frames++;
        }

        private IEnumerator EndOfFrameLoop()
        {
            int n = 0;
            while (true)
            {
                yield return new WaitForEndOfFrame();
                if (n++ == 0) Plugin.L.LogInfo("End-of-frame loop running");
                if (_ok && !s_quitting) GL.IssuePluginEvent(s_fnPtr, 1);   // after this frame's rendering is queued
            }
        }

        // Unity's render thread.
        private static void OnRenderEvent(int id)
        {
            if (s_quitting) return;
            try
            {
                var comp = OpenVR.Compositor;
                if (comp == null) return;
                for (int i = 0; i < 2; i++)
                {
                    var tex = new Texture_t { handle = s_tex[i], eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
                    var e = comp.Submit(i == 0 ? EVREye.Eye_Left : EVREye.Eye_Right, ref tex, ref s_bounds, EVRSubmitFlags.Submit_Default);
                    if (e != EVRCompositorError.None) { s_submitErrors++; s_lastErr = e; } else s_submits++;
                }
                comp.PostPresentHandoff();
                comp.WaitGetPoses(s_poseBuf, s_gameBuf);
            }
            catch (Exception) { s_submitErrors++; }
        }

        // Valve's convention (hellovr ComposeProjection): in a y-up projection, OpenVR's "bottom" is the upper edge.
        private static Matrix4x4 Projection(float[] tan, float n, float f)
        {
            float l = tan[0], r = tan[1], top = tan[3], bottom = tan[2];
            var m = new Matrix4x4();
            m[0, 0] = 2f / (r - l);
            m[0, 2] = (r + l) / (r - l);
            m[1, 1] = 2f / (top - bottom);
            m[1, 2] = (top + bottom) / (top - bottom);
            m[2, 2] = -(f + n) / (f - n);
            m[2, 3] = -2f * f * n / (f - n);
            m[3, 2] = -1f;
            return m;
        }

        // OpenVR is right-handed (-z forward); Unity is left-handed (+z forward).
        internal static void ToUnity(HmdMatrix34_t p, out Vector3 pos, out Quaternion rot)
        {
            var m = Matrix4x4.identity;
            m[0, 0] = p.m0; m[0, 1] = p.m1; m[0, 2] = -p.m2; m[0, 3] = p.m3;
            m[1, 0] = p.m4; m[1, 1] = p.m5; m[1, 2] = -p.m6; m[1, 3] = p.m7;
            m[2, 0] = -p.m8; m[2, 1] = -p.m9; m[2, 2] = p.m10; m[2, 3] = -p.m11;
            pos = new Vector3(m[0, 3], m[1, 3], m[2, 3]);
            rot = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
        }

        // Investigation: log every root canvas and camera when the set changes.
        private string _uiSig = "";
        private void LogUiChanges()
        {
            var sb = new System.Text.StringBuilder();
            var canvases = FindObjectsOfType<Canvas>();
            foreach (var cv in canvases)
            {
                if (cv == null || !cv.isRootCanvas) continue;
                sb.AppendFormat("\n   canvas '{0}' mode={1} cam={2} layer={3} order={4} active={5} scale={6}", cv.name, cv.renderMode,
                    cv.worldCamera != null ? cv.worldCamera.name : "-", LayerMask.LayerToName(cv.gameObject.layer), cv.sortingOrder,
                    cv.gameObject.activeInHierarchy && cv.enabled, cv.transform.lossyScale.x.ToString("0.###"));
            }
            foreach (var c in Camera.allCameras)
            {
                if (c.name.StartsWith("RQVR_")) continue;
                sb.AppendFormat("\n   camera '{0}' depth={1} mask=0x{2:X} clear={3} rt={4} ortho={5}", c.name, c.depth, c.cullingMask, c.clearFlags,
                    c.targetTexture != null ? c.targetTexture.name : "-", c.orthographic);
            }
            var used = new int[32];
            foreach (var r in FindObjectsOfType<Renderer>()) if (!r.name.StartsWith("RQVR_")) used[r.gameObject.layer]++;
            sb.Append("\n   layers in use:");
            for (int i = 0; i < 32; i++) if (used[i] > 0 || !string.IsNullOrEmpty(LayerMask.LayerToName(i))) sb.AppendFormat(" {0}'{1}'={2}", i, LayerMask.LayerToName(i), used[i]);
            foreach (var u in FindObjectsOfType<UI.ThreeDimensional.UIObject3D>())
                sb.AppendFormat("\n   UIObject3D '{0}' target={1} layer={2} cam={3} camMask=0x{4:X} pos={5}", u.name,
                    u._target != null ? u._target.name : "-", u._target != null ? u._target.gameObject.layer : -1,
                    u._targetCamera != null ? u._targetCamera.name : "-", u._targetCamera != null ? u._targetCamera.cullingMask : 0,
                    u._target != null ? u._target.position.ToString() : "-");
            if (_game3d != null)
                foreach (var r in _game3d.GetComponentsInChildren<Renderer>(true))
                    sb.AppendFormat("\n   cam child '{0}' layer={1}({2}) active={3} local={4}", r.name, r.gameObject.layer,
                        LayerMask.LayerToName(r.gameObject.layer), r.gameObject.activeInHierarchy, r.transform.localPosition);
            string s = sb.ToString();
            if (s == _uiSig) return;
            _uiSig = s;
            Plugin.L.LogInfo("UI in scene '" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "':" + s);
        }

        // F12: SteamVR stereo screenshot (exactly what was submitted) + the desktop window, into BepInEx\rqvr_shots.
        private int _shot;
        private void Screenshot()
        {
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "rqvr_shots");
                System.IO.Directory.CreateDirectory(dir);
                string b = System.IO.Path.Combine(dir, (_shot++).ToString("000"));
                uint handle = 0;
                var e = OpenVR.Screenshots != null ? OpenVR.Screenshots.TakeStereoScreenshot(ref handle, b + "_vr_preview", b + "_vr") : EVRScreenshotError.RequestFailed;
                ScreenCapture.CaptureScreenshot(b + "_desktop.png");
                Plugin.L.LogInfo("Screenshot " + b + ": " + e);
            }
            catch (Exception ex) { Plugin.L.LogError("Screenshot failed: " + ex.Message); }
        }

        private void OnApplicationQuit()
        {
            s_quitting = true;
            _ok = false;
        }
    }
}
