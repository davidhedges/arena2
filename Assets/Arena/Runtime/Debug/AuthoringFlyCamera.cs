#nullable enable
using UnityEngine;

namespace Arena.Debugging
{
    // Free-fly inspection camera for the Scenes/Authoring demo scenes: hold
    // the right mouse button to look around, WASD to move, Q/E to descend or
    // rise, Shift for a speed boost, scroll wheel to change the base speed.
    // Uses the legacy Input API (the project runs both input backends),
    // fully qualified because Arena.Input shadows UnityEngine.Input here.
    [DisallowMultipleComponent]
    public sealed class AuthoringFlyCamera : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 4f;
        [SerializeField] private float fastMultiplier = 4f;
        [SerializeField] private float lookSensitivity = 2.5f;

        private float _yaw;
        private float _pitch;

        private void OnEnable()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        private void OnDisable()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Update()
        {
            bool looking = UnityEngine.Input.GetMouseButton(1);
            Cursor.lockState = looking ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !looking;
            if (!looking)
                return;

            float scroll = UnityEngine.Input.GetAxis("Mouse ScrollWheel");
            if (scroll != 0f)
                moveSpeed = Mathf.Clamp(moveSpeed * Mathf.Pow(1.4f, scroll * 10f), 0.25f, 100f);

            _yaw += UnityEngine.Input.GetAxisRaw("Mouse X") * lookSensitivity;
            _pitch = Mathf.Clamp(_pitch - UnityEngine.Input.GetAxisRaw("Mouse Y") * lookSensitivity, -89f, 89f);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            Vector3 move = Vector3.zero;
            if (UnityEngine.Input.GetKey(KeyCode.W)) move += transform.forward;
            if (UnityEngine.Input.GetKey(KeyCode.S)) move -= transform.forward;
            if (UnityEngine.Input.GetKey(KeyCode.D)) move += transform.right;
            if (UnityEngine.Input.GetKey(KeyCode.A)) move -= transform.right;
            if (UnityEngine.Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (UnityEngine.Input.GetKey(KeyCode.Q)) move -= Vector3.up;
            if (move == Vector3.zero)
                return;

            bool fast = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            float speed = moveSpeed * (fast ? fastMultiplier : 1f);
            transform.position += move.normalized * (speed * Time.unscaledDeltaTime);
        }
    }
}
