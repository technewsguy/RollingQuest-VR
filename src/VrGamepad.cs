// Index (and other SteamVR) controllers as a gamepad. The game only understands gamepads and the keyboard, so the mod
// reads the motion controllers through SteamVR Input (input\actions.json, rebindable in SteamVR's controller settings)
// and feeds them to the game as a virtual gamepad named "VR Controllers". Menus, gameplay and the diorama controls then
// work exactly as with a real gamepad.
//
//   Left thumbstick  -> left stick (roll / turn / menus)     Right thumbstick -> right stick (diorama turn & size)
//   Right A          -> A / Cross (jump, accept)             Right B          -> B / Circle (back)
//   Left A           -> X / Square (action)                  Left B           -> Start / Options (pause)
//   Right trigger    -> R2 (look up)                         Left trigger     -> R1 (look down)
//   Left stick click -> Share (switch follow / diorama)      Right stick click-> recenter the view
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Valve.VR;

namespace RollingQuestVR
{
    internal static class VrGamepad
    {
        // GamepadState button bits
        private const int BtnNorth = 4, BtnEast = 5, BtnSouth = 6, BtnWest = 7, BtnLeftStick = 8, BtnRightStick = 9,
                          BtnLeftShoulder = 10, BtnRightShoulder = 11, BtnStart = 12, BtnSelect = 13;
        private const int EventSize = 24 + 28;   // StateEvent header (InputEvent 20 + format 4) + GamepadState (28)

        private static bool _ok;
        private static ulong _set;
        private static ulong _stickL, _stickR, _clickL, _clickR, _trigL, _trigR, _aL, _aR, _bL, _bR;
        private static VRActiveActionSet_t[] _active;
        private static InputDevice _pad;
        private static IntPtr _buf;
        private static bool _wasActive, _recenterHeld;
        private static readonly uint DigitalSize = (uint)Marshal.SizeOf(typeof(InputDigitalActionData_t));
        private static readonly uint AnalogSize = (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t));
        private static readonly uint ActiveSetSize = (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t));

        internal static bool RecenterRequested;

        public static void Init(string manifestPath)
        {
            try
            {
                var input = OpenVR.Input;
                if (input == null || !File.Exists(manifestPath)) { Plugin.L.LogWarning("VR controllers unavailable (no " + manifestPath + ")."); return; }
                var e = input.SetActionManifestPath(manifestPath);
                if (e != EVRInputError.None) { Plugin.L.LogWarning("SteamVR Input: SetActionManifestPath failed: " + e); return; }
                input.GetActionSetHandle("/actions/rqvr", ref _set);
                input.GetActionHandle("/actions/rqvr/in/stick_l", ref _stickL);
                input.GetActionHandle("/actions/rqvr/in/stick_r", ref _stickR);
                input.GetActionHandle("/actions/rqvr/in/stickclick_l", ref _clickL);
                input.GetActionHandle("/actions/rqvr/in/stickclick_r", ref _clickR);
                input.GetActionHandle("/actions/rqvr/in/trigger_l", ref _trigL);
                input.GetActionHandle("/actions/rqvr/in/trigger_r", ref _trigR);
                input.GetActionHandle("/actions/rqvr/in/a_l", ref _aL);
                input.GetActionHandle("/actions/rqvr/in/a_r", ref _aR);
                input.GetActionHandle("/actions/rqvr/in/b_l", ref _bL);
                input.GetActionHandle("/actions/rqvr/in/b_r", ref _bR);
                _active = new[] { new VRActiveActionSet_t { ulActionSet = _set } };
                _buf = Marshal.AllocHGlobal(EventSize);
                _ok = true;
                Plugin.L.LogInfo("VR controllers: SteamVR Input ready (" + manifestPath + ").");
            }
            catch (Exception ex) { Plugin.L.LogWarning("VR controllers unavailable: " + ex.Message); }
        }

        private static bool Digital(ulong h, out bool active)
        {
            var d = new InputDigitalActionData_t();
            bool ok = OpenVR.Input.GetDigitalActionData(h, ref d, DigitalSize, 0) == EVRInputError.None;
            active = ok && d.bActive;
            return active && d.bState;
        }

        private static Vector2 Analog(ulong h, out bool active)
        {
            var d = new InputAnalogActionData_t();
            bool ok = OpenVR.Input.GetAnalogActionData(h, ref d, AnalogSize, 0) == EVRInputError.None;
            active = ok && d.bActive;
            return active ? new Vector2(d.x, d.y) : Vector2.zero;
        }

        public static void Update()
        {
            if (!_ok) return;
            try
            {
                OpenVR.Input.UpdateActionState(_active, ActiveSetSize);
                Vector2 sl = Analog(_stickL, out bool actL), sr = Analog(_stickR, out bool actR);
                float tl = Analog(_trigL, out _).x, tr = Analog(_trigR, out _).x;
                bool aL = Digital(_aL, out _), aR = Digital(_aR, out _), bL = Digital(_bL, out _), bR = Digital(_bR, out _);
                bool cL = Digital(_clickL, out _), cR = Digital(_clickR, out _);
                bool active = actL || actR;   // a VR controller is on and bound

                if (cR && !_recenterHeld) RecenterRequested = true;
                _recenterHeld = cR;

                if (active && _pad == null)
                {
                    _pad = InputSystem.AddDevice("Gamepad", "VR Controllers");
                    Plugin.L.LogInfo("VR controllers connected as a gamepad (device " + _pad.deviceId + ").");
                }
                if (_pad == null) return;
                if (!active && !_wasActive) return;   // controllers off: leave the pad idle (released)
                _wasActive = active;

                uint buttons = 0;
                if (aR) buttons |= 1u << BtnSouth;
                if (bR) buttons |= 1u << BtnEast;
                if (aL) buttons |= 1u << BtnWest;
                if (bL) buttons |= 1u << BtnStart;
                if (cL) buttons |= 1u << BtnSelect;
                if (tl > 0.5f) buttons |= 1u << BtnRightShoulder;   // left trigger = R1 (look down)
                QueueState(buttons, sl, sr, 0f, tr);
            }
            catch (Exception ex) { Plugin.L.LogWarning("VR controllers: " + ex.Message); _ok = false; }
        }

        // Writes a GamepadState event by hand (header + 28-byte state) and queues it for the game's input system.
        private static unsafe void QueueState(uint buttons, Vector2 left, Vector2 right, float lt, float rt)
        {
            byte* p = (byte*)_buf;
            for (int i = 0; i < EventSize; i++) p[i] = 0;
            // InputEvent: type 0, size 4, device 6, time 8, event id 16; then the state format at 20 and the state at 24.
            // FourCCs are stored as (c0 << 24 | c1 << 16 | c2 << 8 | c3).
            *(int*)(p + 0) = 0x53544154;                      // 'STAT'
            *(ushort*)(p + 4) = EventSize;
            *(ushort*)(p + 6) = (ushort)_pad.deviceId;
            *(double*)(p + 8) = InputState.currentTime;
            *(int*)(p + 16) = 0;
            *(int*)(p + 20) = 0x47504144;                     // 'GPAD'
            *(uint*)(p + 24) = buttons;
            *(float*)(p + 28) = left.x; *(float*)(p + 32) = left.y;
            *(float*)(p + 36) = right.x; *(float*)(p + 40) = right.y;
            *(float*)(p + 44) = lt; *(float*)(p + 48) = rt;
            InputSystem.QueueEvent(new InputEventPtr((InputEvent*)p));
        }
    }
}
