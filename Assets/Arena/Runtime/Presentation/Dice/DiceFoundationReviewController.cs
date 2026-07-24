#nullable enable
using UnityEngine;

namespace Arena.Presentation.Dice
{
    [DisallowMultipleComponent]
    public sealed class DiceFoundationReviewController : MonoBehaviour
    {
        [SerializeField] private DiceDefinition? definition;
        [SerializeField] private Transform? diceTransform;
        [SerializeField] private Camera? inspectionCamera;
        [SerializeField, Min(1)] private int displayedResult = 20;
        [SerializeField] private bool autoCycle;
        [SerializeField, Min(0.25f)] private float secondsPerResult = 1.25f;
        [SerializeField, Min(1f)] private float turntableDegreesPerSecond = 28f;

        private bool _turntable;
        private float _cycleClock;

        public void SetAuthoringData(
            DiceDefinition authoredDefinition,
            Transform authoredDice,
            Camera authoredCamera,
            int initialResult)
        {
            definition = authoredDefinition;
            diceTransform = authoredDice;
            inspectionCamera = authoredCamera;
            displayedResult = initialResult;
            ApplyDisplayedResult();
        }

        private void OnEnable()
        {
            ApplyDisplayedResult();
        }

        private void Update()
        {
            if (definition == null || diceTransform == null || inspectionCamera == null)
                return;

            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow))
                StepResult(-1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow))
                StepResult(1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Space))
                _turntable = !_turntable;
            if (UnityEngine.Input.GetKeyDown(KeyCode.A))
            {
                autoCycle = !autoCycle;
                _cycleClock = 0f;
                _turntable = false;
                ApplyDisplayedResult();
            }

            if (_turntable)
            {
                diceTransform.rotation =
                    Quaternion.AngleAxis(turntableDegreesPerSecond * Time.unscaledDeltaTime, Vector3.up) *
                    diceTransform.rotation;
            }

            if (!autoCycle)
                return;

            _cycleClock += Time.unscaledDeltaTime;
            if (_cycleClock < secondsPerResult)
                return;

            _cycleClock = 0f;
            StepResult(1);
        }

        private void StepResult(int delta)
        {
            if (definition == null)
                return;

            displayedResult = 1 + Mod(displayedResult - 1 + delta, definition.Sides);
            _turntable = false;
            ApplyDisplayedResult();
        }

        private void ApplyDisplayedResult()
        {
            if (definition == null || diceTransform == null || inspectionCamera == null)
                return;
            if (!definition.TryGetFace(displayedResult, out DiceFace face))
                return;

            Vector3 towardCamera = inspectionCamera.transform.position - diceTransform.position;
            diceTransform.rotation = DicePoseSolver.FaceTowardCamera(
                face,
                towardCamera,
                inspectionCamera.transform.up);
        }

        private void OnGUI()
        {
            if (definition == null)
                return;

            const float width = 460f;
            Rect panel = new Rect(24f, 24f, width, 104f);
            GUI.Box(panel, GUIContent.none);
            GUI.Label(
                new Rect(panel.x + 16f, panel.y + 12f, width - 32f, 26f),
                $"D20 FOUNDATION REVIEW  •  RESULT {displayedResult}");
            GUI.Label(
                new Rect(panel.x + 16f, panel.y + 40f, width - 32f, 22f),
                "←/→ result   Space turntable   A auto-cycle");
            GUI.Label(
                new Rect(panel.x + 16f, panel.y + 65f, width - 32f, 22f),
                autoCycle ? "Auto-cycle: ON" : _turntable ? "Turntable: ON" : "Inspection pose: LOCKED");
        }

        private static int Mod(int value, int modulus)
        {
            int remainder = value % modulus;
            return remainder < 0 ? remainder + modulus : remainder;
        }
    }
}
