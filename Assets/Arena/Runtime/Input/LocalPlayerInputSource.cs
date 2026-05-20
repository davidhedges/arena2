#nullable enable
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace Arena.Input
{
    /// <summary>
    /// Samples the local player's input once per frame and exposes a unified
    /// frame state for movement, spells, targeting, and aim systems.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class LocalPlayerInputSource : MonoBehaviour
    {
        private const float MouseLookScale = 0.05f;

        private readonly HashSet<KeyCode> _pressedKeys = new();
        private readonly HashSet<KeyCode> _releasedKeys = new();

        public Vector2 Move { get; private set; }
        public Vector2 Look { get; private set; }
        public Vector2 ScrollDelta { get; private set; }
        public Vector2 MousePosition { get; private set; }
        public bool JumpPressed { get; private set; }
        public bool RightMouseHeld { get; private set; }
        public bool LeftMousePressed { get; private set; }
        public bool RightMousePressed { get; private set; }
        public bool RightMouseReleased { get; private set; }
        public bool CursorLocked { get; private set; }
        public bool TabPressed { get; private set; }
        public bool EscapePressed { get; private set; }
        private void Update()
        {
            RightMouseHeld = IsMouseButtonHeld(1);
            Move = ReadMove();
            Look = ReadLook();
            JumpPressed = WasKeyPressedThisFrame(KeyCode.Space);

            ScrollDelta = ReadScrollDelta();
            MousePosition = ReadMousePosition();
            LeftMousePressed = WasMouseButtonPressedThisFrame(0);
            RightMousePressed = WasMouseButtonPressedThisFrame(1);
            RightMouseReleased = WasMouseButtonReleasedThisFrame(1);
            CursorLocked = Cursor.lockState == CursorLockMode.Locked;
            TabPressed = WasKeyPressedThisFrame(KeyCode.Tab);
            EscapePressed = WasKeyPressedThisFrame(KeyCode.Escape);
            _pressedKeys.Clear();
            _releasedKeys.Clear();
        }

        private static Vector2 ReadMove()
        {
            Vector2 move = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
                    move.y += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
                    move.y -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                    move.x += 1f;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                    move.x -= 1f;
            }

            if (Gamepad.current != null)
                move += Gamepad.current.leftStick.ReadValue();
#else
            if (UnityEngine.Input.GetKey(KeyCode.W) || UnityEngine.Input.GetKey(KeyCode.UpArrow))
                move.y += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.S) || UnityEngine.Input.GetKey(KeyCode.DownArrow))
                move.y -= 1f;
            if (UnityEngine.Input.GetKey(KeyCode.D) || UnityEngine.Input.GetKey(KeyCode.RightArrow))
                move.x += 1f;
            if (UnityEngine.Input.GetKey(KeyCode.A) || UnityEngine.Input.GetKey(KeyCode.LeftArrow))
                move.x -= 1f;
#endif
            return Vector2.ClampMagnitude(move, 1f);
        }

        private static Vector2 ReadLook()
        {
            if (!IsMouseButtonHeld(0) && !IsMouseButtonHeld(1))
                return Vector2.zero;

#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
            {
                Vector2 delta = Mouse.current.delta.ReadValue();
                return new Vector2(delta.x, -delta.y) * MouseLookScale;
            }
#endif
            return new Vector2(UnityEngine.Input.GetAxisRaw("Mouse X"), -UnityEngine.Input.GetAxisRaw("Mouse Y")) * MouseLookScale;
        }

        public bool WasKeyPressedThisFrame(KeyCode key)
        {
            if (_pressedKeys.Contains(key))
                return true;
            if (!IsKeyPressedThisFrame(key))
                return false;

            _pressedKeys.Add(key);
            return true;
        }

        public bool WasKeyReleasedThisFrame(KeyCode key)
        {
            if (_releasedKeys.Contains(key))
                return true;
            if (!IsKeyReleasedThisFrame(key))
                return false;

            _releasedKeys.Add(key);
            return true;
        }

        public bool IsKeyHeldThisFrame(KeyCode key)
        {
            return IsKeyHeld(key);
        }

        static Vector2 ReadScrollDelta()
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
                return Mouse.current.scroll.ReadValue();
#endif
            return UnityEngine.Input.mouseScrollDelta;
        }

        static Vector2 ReadMousePosition()
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
                return Mouse.current.position.ReadValue();
#endif
            return UnityEngine.Input.mousePosition;
        }

        static bool WasMouseButtonPressedThisFrame(int button)
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
            {
                return button switch
                {
                    0 => Mouse.current.leftButton.wasPressedThisFrame,
                    1 => Mouse.current.rightButton.wasPressedThisFrame,
                    2 => Mouse.current.middleButton.wasPressedThisFrame,
                    _ => false,
                };
            }
#endif
            return UnityEngine.Input.GetMouseButtonDown(button);
        }

        static bool WasMouseButtonReleasedThisFrame(int button)
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
            {
                return button switch
                {
                    0 => Mouse.current.leftButton.wasReleasedThisFrame,
                    1 => Mouse.current.rightButton.wasReleasedThisFrame,
                    2 => Mouse.current.middleButton.wasReleasedThisFrame,
                    _ => false,
                };
            }
#endif
            return UnityEngine.Input.GetMouseButtonUp(button);
        }

        static bool IsKeyHeld(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            if (TryGetNewInputButton(key, out var button))
                return button.isPressed;
#endif
            return UnityEngine.Input.GetKey(key);
        }

        static bool IsMouseButtonHeld(int button)
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
            {
                return button switch
                {
                    0 => Mouse.current.leftButton.isPressed,
                    1 => Mouse.current.rightButton.isPressed,
                    2 => Mouse.current.middleButton.isPressed,
                    _ => false,
                };
            }
#endif
            return UnityEngine.Input.GetMouseButton(button);
        }

        static bool IsKeyPressedThisFrame(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            if (TryGetNewInputButton(key, out var button))
                return button.wasPressedThisFrame;
#endif
            return UnityEngine.Input.GetKeyDown(key);
        }

        static bool IsKeyReleasedThisFrame(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            if (TryGetNewInputButton(key, out var button))
                return button.wasReleasedThisFrame;
#endif
            return UnityEngine.Input.GetKeyUp(key);
        }

#if ENABLE_INPUT_SYSTEM
        static bool TryGetNewInputButton(KeyCode key, out ButtonControl button)
        {
            if (Mouse.current != null)
            {
                switch (key)
                {
                    case KeyCode.Mouse0:
                        button = Mouse.current.leftButton;
                        return true;
                    case KeyCode.Mouse1:
                        button = Mouse.current.rightButton;
                        return true;
                    case KeyCode.Mouse2:
                        button = Mouse.current.middleButton;
                        return true;
                }
            }

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                switch (key)
                {
                    case KeyCode.Tab:
                        button = keyboard.tabKey;
                        return true;
                    case KeyCode.Escape:
                        button = keyboard.escapeKey;
                        return true;
                    case KeyCode.BackQuote:
                        button = keyboard.backquoteKey;
                        return true;
                    case KeyCode.Alpha1:
                        button = keyboard.digit1Key;
                        return true;
                    case KeyCode.Alpha2:
                        button = keyboard.digit2Key;
                        return true;
                    case KeyCode.Alpha3:
                        button = keyboard.digit3Key;
                        return true;
                    case KeyCode.Alpha4:
                        button = keyboard.digit4Key;
                        return true;
                    case KeyCode.Alpha5:
                        button = keyboard.digit5Key;
                        return true;
                    case KeyCode.Alpha6:
                        button = keyboard.digit6Key;
                        return true;
                    case KeyCode.Alpha7:
                        button = keyboard.digit7Key;
                        return true;
                    case KeyCode.Alpha8:
                        button = keyboard.digit8Key;
                        return true;
                    case KeyCode.Alpha9:
                        button = keyboard.digit9Key;
                        return true;
                    case KeyCode.Alpha0:
                        button = keyboard.digit0Key;
                        return true;
                    case KeyCode.Q:
                        button = keyboard.qKey;
                        return true;
                    case KeyCode.E:
                        button = keyboard.eKey;
                        return true;
                    case KeyCode.R:
                        button = keyboard.rKey;
                        return true;
                    case KeyCode.F:
                        button = keyboard.fKey;
                        return true;
                    case KeyCode.C:
                        button = keyboard.cKey;
                        return true;
                    case KeyCode.V:
                        button = keyboard.vKey;
                        return true;
                    case KeyCode.X:
                        button = keyboard.xKey;
                        return true;
                    case KeyCode.Z:
                        button = keyboard.zKey;
                        return true;
                    case KeyCode.T:
                        button = keyboard.tKey;
                        return true;
                }
            }

            button = null!;
            return false;
        }
#endif
    }
}
