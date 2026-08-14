using UnityEngine;
using UnityEngine.InputSystem;

namespace PointCloud.App.Input
{
    /// <summary>
    /// Every viewport binding, in one place, expressed semantically.
    ///
    /// Reads Mouse/Keyboard.current directly rather than going through an .inputactions
    /// asset. The bindings here are fixed CloudCompare/MeshLab conventions that this
    /// audience already has muscle memory for, so the rebinding machinery an action asset
    /// buys would be unused — and a hand-authored asset plus generated wrapper is a lot of
    /// GUID-bearing surface for no benefit. Swapping this class for an action-asset
    /// implementation later touches nothing else, because callers only see the properties
    /// below.
    ///
    /// The project is on the new Input System exclusively (activeInputHandler = 1), so the
    /// legacy UnityEngine.Input API would throw at runtime.
    /// </summary>
    public sealed class ViewportInput
    {
        /// <summary>
        /// True while the pointer is over a UI panel and the viewport should ignore it.
        /// Latched by the owner on pointer-down and held for the whole drag — otherwise a
        /// drag that starts in the viewport stutters the moment it crosses a panel.
        /// </summary>
        public bool PointerCapturedByUi { get; set; }

        public bool FlyMode { get; set; }

        static Mouse Mouse => Mouse.current;
        static Keyboard Keyboard => Keyboard.current;

        public bool HasMouse => Mouse != null;

        public Vector2 PointerPosition => Mouse != null ? Mouse.position.ReadValue() : Vector2.zero;

        public Vector2 PointerDelta => Mouse != null ? Mouse.delta.ReadValue() : Vector2.zero;

        public bool LeftPressedThisFrame  => Mouse != null && Mouse.leftButton.wasPressedThisFrame;
        public bool LeftReleasedThisFrame => Mouse != null && Mouse.leftButton.wasReleasedThisFrame;
        public bool LeftHeld              => Mouse != null && Mouse.leftButton.isPressed;
        public bool MiddleHeld            => Mouse != null && Mouse.middleButton.isPressed;
        public bool RightHeld             => Mouse != null && Mouse.rightButton.isPressed;

        public bool AnyButtonPressedThisFrame =>
            Mouse != null && (Mouse.leftButton.wasPressedThisFrame ||
                              Mouse.middleButton.wasPressedThisFrame ||
                              Mouse.rightButton.wasPressedThisFrame);

        public bool ShiftHeld => Keyboard != null &&
                                 (Keyboard.leftShiftKey.isPressed || Keyboard.rightShiftKey.isPressed);

        public bool AltHeld => Keyboard != null &&
                               (Keyboard.leftAltKey.isPressed || Keyboard.rightAltKey.isPressed);

        /// <summary>Scroll in notches. Mouse wheels report 120 per detent on Windows.</summary>
        public float ScrollDelta
        {
            get
            {
                if (Mouse == null) return 0f;
                float raw = Mouse.scroll.ReadValue().y;
                return Mathf.Abs(raw) >= 1f ? raw / 120f : raw;
            }
        }

        // --- Semantic gestures -------------------------------------------------------
        // Orbit is plain LMB (or Alt+LMB in fly mode, so WASD-flying users can still orbit).
        // Pan is MMB or Shift+LMB. Dolly is RMB drag. This is the CloudCompare arrangement.

        public bool OrbitActive => Usable && (FlyMode ? (AltHeld && LeftHeld) : (LeftHeld && !ShiftHeld));

        public bool PanActive => Usable && (MiddleHeld || (LeftHeld && ShiftHeld && !FlyMode));

        public bool DollyActive => Usable && RightHeld && !FlyMode;

        bool Usable => !PointerCapturedByUi && Mouse != null;

        /// <summary>WASD + QE while in fly mode. Zero otherwise.</summary>
        public Vector3 FlyVector
        {
            get
            {
                if (!FlyMode || Keyboard == null) return Vector3.zero;

                var move = Vector3.zero;
                if (Keyboard.wKey.isPressed) move.z += 1f;
                if (Keyboard.sKey.isPressed) move.z -= 1f;
                if (Keyboard.dKey.isPressed) move.x += 1f;
                if (Keyboard.aKey.isPressed) move.x -= 1f;
                if (Keyboard.eKey.isPressed) move.y += 1f;
                if (Keyboard.qKey.isPressed) move.y -= 1f;
                return move.sqrMagnitude > 1f ? move.normalized : move;
            }
        }

        public bool BoostHeld => ShiftHeld;

        public bool FocusSelectedPressed => Keyboard != null && Keyboard.fKey.wasPressedThisFrame;
        public bool FocusAllPressed      => Keyboard != null && Keyboard.aKey.wasPressedThisFrame && !FlyMode;
        public bool ToggleFlyModePressed => Keyboard != null && Keyboard.backquoteKey.wasPressedThisFrame;

        /// <summary>Number keys 1-9, mapped to render modes. Returns -1 when nothing was pressed.</summary>
        public int RenderModeHotkey
        {
            get
            {
                if (Keyboard == null) return -1;
                if (Keyboard.digit1Key.wasPressedThisFrame) return 0;
                if (Keyboard.digit2Key.wasPressedThisFrame) return 1;
                if (Keyboard.digit3Key.wasPressedThisFrame) return 2;
                if (Keyboard.digit4Key.wasPressedThisFrame) return 3;
                if (Keyboard.digit5Key.wasPressedThisFrame) return 4;
                if (Keyboard.digit6Key.wasPressedThisFrame) return 5;
                if (Keyboard.digit7Key.wasPressedThisFrame) return 6;
                if (Keyboard.digit8Key.wasPressedThisFrame) return 7;
                if (Keyboard.digit9Key.wasPressedThisFrame) return 8;
                return -1;
            }
        }
    }
}
