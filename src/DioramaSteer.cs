// Diorama steering: in the game you turn the ball 90 degrees and roll forward, which makes sense from behind the ball
// but not when you look at the level from outside. In diorama mode the left stick / D-pad / arrow keys instead push the
// ball the way you want it to go on the model (up = away from you): the mod turns the ball to that direction on the
// surface it's on, then rolls it forward. Jump and everything else are unchanged.
using System;
using HarmonyLib;
using MoveControllers;
using UnityEngine;

namespace RollingQuestVR
{
    [HarmonyPatch]
    internal static class DioramaSteer
    {
        private static int _frame = -1;
        private static bool _fwd, _left, _right;

        internal static BallPlayerController Player;   // the controller of the ball you play (for the diorama's orientation)

        // Which way the ball faces and which way is up off its surface, in game-world space.
        internal static bool TryGetBallBasis(out Vector3 forward, out Vector3 up, out Vector3 position)
        {
            forward = Vector3.forward; up = Vector3.up; position = Vector3.zero;
            try
            {
                var pc = Player;
                if (pc == null || pc.WasCollected || !pc.HasAttachedBall) return false;
                var ball = pc.AttachedBall;
                if (ball == null || ball._moveController == null || ball._moveController._basisCenter == null) return false;
                var t = ball._moveController._basisCenter.transform;
                forward = t.forward; up = t.up; position = ball.transform.position;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool Active(BallPlayerController pc)
        {
            if (!Plugin.DioramaSteering.Value) return false;
            try { return VrRenderer.DioramaActive && VrRenderer.ViewEye != null && pc != null && pc.HasAttachedBall && !pc.IsPaused; }
            catch (Exception) { return false; }
        }

        // The game's own turn/forward inputs are ignored while steering this way.
        [HarmonyPrefix, HarmonyPatch(typeof(BallPlayerController), nameof(BallPlayerController.OnForward))]
        private static bool OnForward(BallPlayerController __instance) => !Active(__instance);
        [HarmonyPrefix, HarmonyPatch(typeof(BallPlayerController), nameof(BallPlayerController.OnTurnLeft))]
        private static bool OnTurnLeft(BallPlayerController __instance) => !Active(__instance);
        [HarmonyPrefix, HarmonyPatch(typeof(BallPlayerController), nameof(BallPlayerController.OnTurnRight))]
        private static bool OnTurnRight(BallPlayerController __instance) => !Active(__instance);

        [HarmonyPrefix, HarmonyPatch(typeof(BallPlayerController), nameof(BallPlayerController.Update))]
        private static void Update(BallPlayerController __instance) { Player = __instance; Apply(__instance); }
        [HarmonyPrefix, HarmonyPatch(typeof(BallPlayerController), nameof(BallPlayerController.FixedUpdate))]
        private static void FixedUpdate(BallPlayerController __instance) => Apply(__instance);

        private static void Apply(BallPlayerController pc)
        {
            if (!Active(pc)) return;
            try
            {
                if (_frame != Time.frameCount) { _frame = Time.frameCount; Decide(pc); }
                pc._forwardAction = _fwd;
                pc._turnLeftAction = _left;
                pc._turnRightAction = _right;
            }
            catch (Exception e) { Plugin.L.LogWarning("Diorama steering: " + e.Message); }
        }

        private static Vector2 ReadStick()
        {
            Vector2 s = Vector2.zero;
            try
            {
                var pad = UnityEngine.InputSystem.Gamepad.current;
                if (pad != null)
                {
                    s = pad.leftStick.ReadValue();
                    Vector2 d = pad.dpad.ReadValue();
                    if (d.sqrMagnitude > s.sqrMagnitude) s = d;
                }
            }
            catch (Exception) { }
            Vector2 k = new Vector2((Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.LeftArrow) ? 1 : 0),
                                    (Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.DownArrow) ? 1 : 0));
            if (k.sqrMagnitude > s.sqrMagnitude) s = k;
            return s;
        }

        private static void Decide(BallPlayerController pc)
        {
            _fwd = _left = _right = false;
            var ball = pc.AttachedBall;
            if (ball == null) return;
            NormalMoveController mc = ball._moveController;
            if (mc == null) return;

            // Which way the ball faces, and which way is "up" off the surface it's rolling on.
            Transform basis = mc._basisCenter != null ? mc._basisCenter.transform : null;
            Vector3 up = basis != null ? basis.up : Vector3.up;
            if (basis == null) return;
            Vector3 heading = Vector3.ProjectOnPlane(basis.forward, up).normalized;

            Vector2 stick = ReadStick();
            if (stick.sqrMagnitude < 0.25f || heading.sqrMagnitude < 0.5f) return;

            // The stick in the model's on-table directions (grid-aligned): up = away from you, right = your right.
            if (!VrRenderer.DisplaySet) return;
            Vector3 away = VrRenderer.DisplayForward, right = Vector3.Cross(VrRenderer.DisplayUp, away);
            Vector3 want = Mathf.Abs(stick.y) >= Mathf.Abs(stick.x) ? away * Mathf.Sign(stick.y) : right * Mathf.Sign(stick.x);
            want = Vector3.ProjectOnPlane(want, up);
            // On a wall facing you (or away), "away" runs into the wall: there, up/down on the stick means up/down the
            // wall, and on side walls left/right on the stick follows the wall as you see it.
            if (want.sqrMagnitude < 0.25f)
            {
                Transform eye = VrRenderer.ViewEye;
                want = Vector3.ProjectOnPlane(Mathf.Abs(stick.y) >= Mathf.Abs(stick.x) ? VrRenderer.DisplayUp * Mathf.Sign(stick.y) : eye.right * Mathf.Sign(stick.x), up);
                if (want.sqrMagnitude < 0.04f && eye != null) want = Vector3.ProjectOnPlane(eye.up * stick.y + eye.right * stick.x, up);
            }
            if (want.sqrMagnitude < 1e-4f) return;

            float a = Vector3.SignedAngle(heading, want, up);   // > 0: to the ball's right
            if (Mathf.Abs(a) <= 45f) _fwd = true;
            else if (a > 0f) _right = true;
            else _left = true;   // includes "behind": two turns
        }
    }
}
