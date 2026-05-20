using System.Collections;
using System.Collections.Generic;
using NHance.Assets.Scripts;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NHance.Assets
{
    [RequireComponent(typeof(Camera))]
    public class NHCamera : MonoBehaviour
    {
        [SerializeField] private Transform focus = default;
        [SerializeField] private bool TryToFindCharacter = true;
        [SerializeField, Range(1f, 20f)] private float distance = 5f;
        [SerializeField, Range(1f, 20f)] private float minDistance = 1;
        [SerializeField, Range(1f, 20f)] private float maxDistance = 1;
        [SerializeField] private Vector3 focusOffset = Vector3.zero;

        [SerializeField] private float rotationSpeed = 90f;
        [SerializeField, Range(-89f, 89f)] private float minVerticalAngle = -45f, maxVerticalAngle = 45f;
        [SerializeField] private LayerMask obstructionMask = -1;
        [SerializeField] private float horizontalSmoothTime = 0.1f;
        [SerializeField] private float verticalSmoothTime = 0.2f;
        [SerializeField] private float flySpeed = 1f;

        public bool ControlsFoldout = true;

        private Transform _transform;
        private Camera regularCamera;
        private Vector3 focusPoint;
        private Vector2 orbitAngles = new Vector2(22f, 0f);
        private Vector3 lastFocusOffset;
        private float targetDistance;
        private float zoomVelocity;
        private float currentXVelocity;
        private float currentYVelocity;
        private float currentZVelocity;

        private Vector3 CameraHalfExtends
        {
            get
            {
                Vector3 halfExtends;
                halfExtends.y =
                    regularCamera.nearClipPlane *
                    Mathf.Tan(0.5f * Mathf.Deg2Rad * regularCamera.fieldOfView);
                halfExtends.x = halfExtends.y * regularCamera.aspect;
                halfExtends.z = 0f;
                return halfExtends;
            }
        }

        void OnValidate()
        {
            if (maxVerticalAngle < minVerticalAngle)
            {
                maxVerticalAngle = minVerticalAngle;
            }
        }

        void Awake()
        {
            if(focus == null && TryToFindCharacter)
                focus = FindObjectOfType<NHCharacterController>().gameObject.transform;
            _transform = transform;
            regularCamera = GetComponent<Camera>();
            focusPoint = (focus != null ? focus.position : Vector3.zero) + focusOffset;
            _transform.localRotation = Quaternion.Euler(orbitAngles);
            targetDistance = distance;
            lastFocusOffset = focusOffset;
        }

		void Update()
		{
			
			UpdateFocusPoint();
			Quaternion lookRotation = _transform.localRotation;

			var mouse = Mouse.current;
			var keyboard = Keyboard.current;

			bool lmb = mouse != null && mouse.leftButton.isPressed;
			Cursor.visible = !lmb;

			if (ManualRotation())
			{
				ConstrainAngles();
				lookRotation = Quaternion.Euler(orbitAngles);
			}

			FocusOffsetMove();

			if (mouse != null)
			{
				float scrollY = mouse.scroll.ReadValue().y; // обычно шаг = 120
				if (Mathf.Abs(scrollY) > 0.01f)
					targetDistance = Mathf.Clamp(targetDistance - (scrollY / 120f) / 5f, minDistance, maxDistance);
			}


			distance = Mathf.SmoothDamp(distance, targetDistance, ref zoomVelocity, 0.3f);

            Vector3 lookDirection = lookRotation * Vector3.forward;
            Vector3 lookPosition = focusPoint - lookDirection * distance;

            Vector3 rectOffset = lookDirection * regularCamera.nearClipPlane;
            Vector3 rectPosition = lookPosition + rectOffset;
            Vector3 castFrom = focusPoint;
            Vector3 castLine = rectPosition - castFrom;
            float castDistance = castLine.magnitude;
            Vector3 castDirection = castLine / castDistance;

            if (Physics.BoxCast(
                castFrom, CameraHalfExtends, castDirection, out RaycastHit hit,
                lookRotation, castDistance, obstructionMask
            ))
            {
                rectPosition = castFrom + castDirection * hit.distance;
                lookPosition = rectPosition - rectOffset;
            }
            
            _transform.position = lookPosition;
            _transform.rotation = lookRotation;
        }

        void UpdateFocusPoint()
        {
            var position = (focus != null ? focus.position : Vector3.zero) + focusOffset;
            var x = Mathf.SmoothDamp(focusPoint.x, position.x, ref currentXVelocity,
                horizontalSmoothTime);
            var y = Mathf.SmoothDamp(focusPoint.y, position.y, ref currentYVelocity,
                verticalSmoothTime);
            var z = Mathf.SmoothDamp(focusPoint.z, position.z, ref currentZVelocity,
                horizontalSmoothTime);
            focusPoint = new Vector3(x, y, z);
        }

        bool ManualRotation()
        {
			var mouse = Mouse.current;
				if (mouse == null || !mouse.leftButton.isPressed) return false;

				Vector2 d = mouse.delta.ReadValue(); // пиксели за кадр
				Vector2 input = new Vector2(-d.y, d.x);

				const float e = 0.001f;
				if (input.x < -e || input.x > e || input.y < -e || input.y > e)
				{
					orbitAngles += rotationSpeed * Time.unscaledDeltaTime * input;
					return true;
				}
				return false;
        }

        void FocusOffsetMove()
		{
			var mouse = Mouse.current;
			var keyboard = Keyboard.current;

			bool rmb = mouse != null && mouse.rightButton.isPressed;
			if (!(rmb || focus == null))
			{
				focusOffset = lastFocusOffset;
				return;
			}

			float vertical = 0f;
			float horizontal = 0f;

			if (keyboard != null)
			{
				// Vertical (W/S)
				if (keyboard.wKey.isPressed) vertical += 1f;
				if (keyboard.sKey.isPressed) vertical -= 1f;

				// Horizontal (A/D)
				if (keyboard.dKey.isPressed) horizontal += 1f;
				if (keyboard.aKey.isPressed) horizontal -= 1f;
			}

			var up = vertical * _transform.forward * Time.deltaTime * flySpeed;
			var right = horizontal * _transform.right * Time.deltaTime * flySpeed;

			float qe = 0f;
			if (keyboard != null)
			{
				if (keyboard.qKey.isPressed) qe -= 1f;
				if (keyboard.eKey.isPressed) qe += 1f;
			}

			var forward = qe * _transform.up * Time.deltaTime * flySpeed;
			focusOffset += up + right + forward;
		}

        void ConstrainAngles()
        {
            orbitAngles.x =
                Mathf.Clamp(orbitAngles.x, minVerticalAngle, maxVerticalAngle);

            if (orbitAngles.y < 0f)
            {
                orbitAngles.y += 360f;
            }
            else if (orbitAngles.y >= 360f)
            {
                orbitAngles.y -= 360f;
            }
        }
    }
}