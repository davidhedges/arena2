using UnityEngine;
using UnityEngine.InputSystem;

namespace DungeonLab
{
    public sealed class FlyCameraController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 8f;
        [SerializeField] private float fastMoveMultiplier = 3f;
        [SerializeField] private float lookSensitivity = 0.12f;
        [SerializeField] private bool requireRightMouseButton;

        private float yaw;
        private float pitch;

        public void Configure(float moveSpeed, float fastMoveMultiplier, float lookSensitivity, bool requireRightMouseButton)
        {
            this.moveSpeed = moveSpeed;
            this.fastMoveMultiplier = fastMoveMultiplier;
            this.lookSensitivity = lookSensitivity;
            this.requireRightMouseButton = requireRightMouseButton;
        }

        private void Awake()
        {
            Vector3 euler = transform.eulerAngles;
            yaw = euler.y;
            pitch = NormalizePitch(euler.x);
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;

            if (keyboard == null)
            {
                return;
            }

            bool looking = !requireRightMouseButton || (mouse != null && mouse.rightButton.isPressed);
            Cursor.lockState = looking ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !looking;

            if (looking && mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                yaw += delta.x * lookSensitivity;
                pitch = Mathf.Clamp(pitch - delta.y * lookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }

            Vector3 localMove = Vector3.zero;

            if (keyboard.wKey.isPressed) localMove += Vector3.forward;
            if (keyboard.sKey.isPressed) localMove += Vector3.back;
            if (keyboard.dKey.isPressed) localMove += Vector3.right;
            if (keyboard.aKey.isPressed) localMove += Vector3.left;
            if (keyboard.eKey.isPressed || keyboard.spaceKey.isPressed) localMove += Vector3.up;
            if (keyboard.qKey.isPressed || keyboard.leftCtrlKey.isPressed) localMove += Vector3.down;

            if (localMove.sqrMagnitude <= 0f)
            {
                return;
            }

            float speed = moveSpeed;
            if (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed)
            {
                speed *= fastMoveMultiplier;
            }

            transform.position += transform.TransformDirection(localMove.normalized) * (speed * Time.deltaTime);
        }

        private static float NormalizePitch(float value)
        {
            return value > 180f ? value - 360f : value;
        }

        private void OnDisable()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
