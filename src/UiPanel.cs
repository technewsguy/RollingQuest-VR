// Menus and HUD in VR: the game's screen-space canvases are redirected to a hidden capture camera that draws them
// into a texture, shown on a floating screen in front of you. Only the VR eyes see the screen: it sits on layer 31,
// which the game never uses, and the mod removes that layer from every game camera. (Layer 3 looked free because the
// game's cameras skip it, but the hourglass HUD model is drawn there, so the VR eyes showed a stray hourglass.)
using System;
using UnityEngine;

namespace RollingQuestVR
{
    internal class UiPanel
    {
        internal const int PanelLayer = 31;

        // Keeps the screen's layer out of the game's own cameras (desktop view, 3D-UI cameras, ...).
        public static void KeepOffGameCameras()
        {
            int bit = 1 << PanelLayer;
            foreach (var c in Camera.allCameras)
                if (c != null && !c.name.StartsWith("RQVR_") && (c.cullingMask & bit) != 0) c.cullingMask &= ~bit;
        }
        private const int TexW = 1920, TexH = 1080;

        private RenderTexture _tex;
        private Camera _cam;
        private GameObject _panel;
        private Material _mat;
        private bool _placed;
        private Vector3 _anchor;
        private float _yaw, _targetYaw;
        private readonly System.Collections.Generic.HashSet<int> _converted = new System.Collections.Generic.HashSet<int>();

        public UiPanel(Transform root)
        {
            _tex = new RenderTexture(TexW, TexH, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _tex.name = "RQVR_UI";
            _tex.Create();

            // Capture camera, parked far from the level so it only sees the canvases we put in front of it.
            var go = new GameObject("RQVR_UICam");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.transform.position = new Vector3(0f, -5000f, 0f);
            _cam = go.AddComponent<Camera>();
            _cam.targetTexture = _tex;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cam.cullingMask = ~0;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 50f;
            _cam.depth = 30f;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;

            Shader shader = Shader.Find("UI/Default");
            Plugin.L.LogInfo("UI panel shader: " + (shader != null ? shader.name : "<none>"));
            _mat = new Material(shader);
            _mat.mainTexture = _tex;
            _mat.SetFloat("unity_GUIZTestMode", (float)UnityEngine.Rendering.CompareFunction.Always);   // always on top
            _mat.renderQueue = 4001;

            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            _panel = new GameObject("RQVR_Panel");
            _panel.layer = PanelLayer;
            _panel.transform.SetParent(root, false);
            _panel.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = _panel.AddComponent<MeshRenderer>();
            r.sharedMaterial = _mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;

            if (Plugin.DesktopUi.Value) CreateDesktopMirror();
        }

        private const string DesktopCanvasName = "RQVR_DesktopUI";

        // The desktop window still shows the game's own camera view; this puts the menus/HUD back on top of it, as a
        // full-window image of the same texture the VR screen shows.
        private void CreateDesktopMirror()
        {
            var go = new GameObject(DesktopCanvasName);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var cv = go.AddComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 32767;
            var img = new GameObject("RQVR_DesktopUI_Image");
            img.transform.SetParent(go.transform, false);
            var raw = img.AddComponent<UnityEngine.UI.RawImage>();
            raw.texture = _tex;
            raw.raycastTarget = false;
            var rt = img.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            Plugin.L.LogInfo("Desktop window shows the menus/HUD too.");
        }

        // Every half second: move any new screen-overlay canvas onto the capture camera.
        public void Scan()
        {
            foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (cv == null || !cv.isRootCanvas || cv.renderMode != RenderMode.ScreenSpaceOverlay || cv.name == DesktopCanvasName) continue;
                cv.renderMode = RenderMode.ScreenSpaceCamera;
                cv.worldCamera = _cam;
                cv.planeDistance = 1f;
                if (_converted.Add(cv.GetInstanceID())) Plugin.L.LogInfo("UI canvas '" + cv.name + "' -> VR screen");
            }
        }

        // The screen floats in front of you at eye level (in the view rig's space, so it turns with the game camera)
        // and turns to face you again when you look well away from it.
        public void Place(Vector3 headPos, Quaternion headRot)
        {
            float headYaw = headRot.eulerAngles.y;
            float dt = Time.unscaledDeltaTime;
            if (!_placed) { _anchor = headPos; _yaw = _targetYaw = headYaw; _placed = true; }
            if (Mathf.Abs(Mathf.DeltaAngle(_targetYaw, headYaw)) > Plugin.UiFollowAngle.Value) _targetYaw = headYaw;
            _yaw = Mathf.LerpAngle(_yaw, _targetYaw, 1f - Mathf.Exp(-4f * dt));
            _anchor = Vector3.Lerp(_anchor, headPos, 1f - Mathf.Exp(-2f * dt));
            float w = Mathf.Max(0.1f, Plugin.UiWidth.Value);
            Quaternion yaw = Quaternion.Euler(0f, _yaw, 0f);
            _panel.transform.localPosition = _anchor + yaw * new Vector3(0f, Plugin.UiHeight.Value, Mathf.Max(0.2f, Plugin.UiDistance.Value));
            _panel.transform.localRotation = yaw;
            _panel.transform.localScale = new Vector3(w, w * TexH / TexW, 1f);
        }

        public void Recenter() { _placed = false; }
    }
}
