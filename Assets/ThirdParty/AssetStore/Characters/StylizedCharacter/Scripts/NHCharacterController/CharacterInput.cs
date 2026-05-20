using UnityEngine;
using UnityEngine.InputSystem;

namespace NHance.Assets.Scripts
{
    public class CharacterInput
    {
        private CharacterSettings _settings;
        public CharacterInput(CharacterSettings settings)
        {
            _settings = settings;
        }

        public void Update()
        {
            if (!_settings.IsEnabled)
            {
                _settings.IsWalking = false;
                _settings.IsSprinting = false;
                _settings.MovementAxis = Vector2.zero;
                return;
            }

            Sprint();
            Walk();
            Jump();
            Movement();
        }

        private void Walk()
        {
            var kb = Keyboard.current;
            _settings.IsWalking = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
        }

        private void Sprint()
        {
            var kb = Keyboard.current;
            _settings.IsSprinting = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
        }

        private void Jump()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.spaceKey.wasPressedThisFrame)
                _settings.LastJumpButtonTime = Time.time;
        }

                private void Movement()
        {
            var mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                _settings.MovementAxis = Vector2.zero;
                return;
            }

            var kb = Keyboard.current;
            if (kb == null)
            {
                _settings.MovementAxis = Vector2.zero;
                return;
            }

            float vertical = 0f;
            if (kb.wKey.isPressed) vertical += 1f;
            if (kb.sKey.isPressed) vertical -= 1f;

            float horizontal = 0f;
            if (kb.dKey.isPressed) horizontal += 1f;
            if (kb.aKey.isPressed) horizontal -= 1f;

            Vector2 playerInput = new Vector2(vertical, horizontal);
            playerInput = Vector2.ClampMagnitude(playerInput, 1f);
            _settings.MovementAxis = playerInput;
        }
    }
}