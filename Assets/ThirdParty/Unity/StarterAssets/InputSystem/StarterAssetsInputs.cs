using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StarterAssets
{
	public class StarterAssetsInputs : MonoBehaviour
	{
		[Header("Character Input Values")]
		public Vector2 move;
		public Vector2 look;
		public bool jump;
		public bool sprint;

		[Header("Movement Settings")]
		public bool analogMovement;

		[Header("Mouse Cursor Settings")]
		public bool cursorLocked = false;
		public bool cursorInputForLook = true;

	#if ENABLE_INPUT_SYSTEM
			public void OnMove(InputValue value)
			{
				MoveInput(value.Get<Vector2>());
			}

			// The runtime PlayerArmature prefab is wired through PlayerInput UnityEvents
			// to call Input* methods with CallbackContext payloads.
			public void InputMove(InputAction.CallbackContext context)
			{
				MoveInput(context.ReadValue<Vector2>());
			}

			public void OnLook(InputValue value)
			{
				// MMO-style: orbit camera while LMB or RMB is held.
				if(cursorInputForLook && IsOrbitLookActive())
			{
				LookInput(value.Get<Vector2>());
			}
			else
				{
					LookInput(Vector2.zero);
				}
			}

			public void InputLook(InputAction.CallbackContext context)
			{
				if (cursorInputForLook && IsOrbitLookActive())
				{
					LookInput(context.ReadValue<Vector2>());
				}
				else
				{
					LookInput(Vector2.zero);
				}
			}

			public void OnJump(InputValue value)
			{
				JumpInput(value.isPressed);
			}

			public void InputJump(InputAction.CallbackContext context)
			{
				JumpInput(context.ReadValueAsButton());
			}

			public void OnSprint(InputValue value)
			{
				SprintInput(value.isPressed);
			}

			public void InputSprint(InputAction.CallbackContext context)
			{
				SprintInput(context.ReadValueAsButton());
			}
	#endif


		public void MoveInput(Vector2 newMoveDirection)
		{
			move = newMoveDirection;
		} 

		public void LookInput(Vector2 newLookDirection)
		{
			look = newLookDirection;
		}

		public void JumpInput(bool newJumpState)
		{
			jump = newJumpState;
		}

		public void SprintInput(bool newSprintState)
		{
			sprint = newSprintState;
		}

		/// <summary>True when RMB is held (character should face camera direction).</summary>
		public bool rightMouseHeld { get; private set; }

		private void Update()
		{
			rightMouseHeld = IsMouseButtonHeld(1);
		}

		private void OnApplicationFocus(bool hasFocus)
		{
			if (hasFocus)
			{
				Cursor.lockState = CursorLockMode.None;
				Cursor.visible = true;
			}
		}

		private static bool IsOrbitLookActive()
		{
			return IsMouseButtonHeld(0) || IsMouseButtonHeld(1);
		}

		private static bool IsMouseButtonHeld(int button)
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
			return Input.GetMouseButton(button);
		}
	}
	
}
