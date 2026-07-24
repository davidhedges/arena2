#nullable enable
using System;
using Arena.Presentation.Dice;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Debugging
{
    /// <summary>
    /// Editor/development-only local presentation harness. Forced values enter
    /// the visual presenter directly and never touch networking or game state.
    /// </summary>
    [DefaultExecutionOrder(90)]
    [DisallowMultipleComponent]
    public sealed class DicePresentationDebugPanel : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const string DiceOverlayLabSceneName = "DiceOverlayLab";
        private const KeyCode ToggleKey = KeyCode.Minus;

        private static readonly Rect[] PreviewRegions =
        {
            new(0f, 0f, 1f, 1f),
            new(0.08f, 0.2f, 0.84f, 0.6f),
            new(0.32f, 0.05f, 0.36f, 0.9f),
            new(0.26f, 0.27f, 0.48f, 0.46f)
        };

        private static readonly string[] PreviewRegionNames =
        {
            "Full", "Landscape", "Portrait", "Compact"
        };

        [SerializeField] private DiceOverlayPresenter? presenter;
        [SerializeField] private bool visible;

        private string _requestId = "local-d20-review";
        private string _resultText = "20";
        private int _profileIndex = -1;
        private int _regionIndex;
        private bool _sequenceActive;
        private float _heldSince = -1f;
        private int _sequenceSerial;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;
            if (FindAnyObjectByType<DicePresentationDebugPanel>() != null)
                return;

            GameObject host = new("DicePresentationDebugPanel");
            DontDestroyOnLoad(host);
            DicePresentationDebugPanel panel = host.AddComponent<DicePresentationDebugPanel>();
            panel.visible = false;
        }

        public void SetAuthoringData(DiceOverlayPresenter authoredPresenter, bool startVisible)
        {
            presenter = authoredPresenter;
            visible = startVisible;
        }

        private void Awake()
        {
            if (IsDiceOverlayLabActive())
                visible = true;
            ResolvePresenter();
        }

        private void Update()
        {
            if (IsDiceOverlayLabActive())
            {
                visible = true;
            }
            else if (UnityEngine.Input.GetKeyDown(ToggleKey) ||
                     UnityEngine.Input.GetKeyDown(KeyCode.KeypadMinus))
            {
                visible = !visible;
            }

            ResolvePresenter();
            if (!_sequenceActive || presenter == null)
                return;

            if (presenter.State != DicePresentationState.Held)
            {
                _heldSince = -1f;
                return;
            }

            if (_heldSince < 0f)
            {
                _heldSince = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime - _heldSince < 0.7f)
                return;

            int current = ParseResult();
            if (current >= 20)
            {
                _sequenceActive = false;
                _heldSince = -1f;
                return;
            }

            _resultText = (current + 1).ToString();
            _heldSince = -1f;
            ShowCurrent($"local-sequence-{_sequenceSerial}-{_resultText}");
        }

        private void OnGUI()
        {
            if (!visible)
                return;

            const float width = 370f;
            GUILayout.BeginArea(new Rect(20f, 20f, width, 440f), GUI.skin.box);
            GUILayout.Label("D20 OVERLAY LAB");
            GUILayout.Label("LOCAL PREVIEW + AUTHORITATIVE SERVER ROLL");
            GUILayout.Space(8f);

            GUILayout.Label($"State: {(presenter != null ? presenter.State.ToString() : "Presenter unavailable")}");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Result", GUILayout.Width(64f));
            _resultText = GUILayout.TextField(_resultText, GUILayout.Width(54f));
            if (GUILayout.Button("−", GUILayout.Width(34f)))
                SetResult(ParseResult() - 1);
            if (GUILayout.Button("+", GUILayout.Width(34f)))
                SetResult(ParseResult() + 1);
            if (GUILayout.Button("Random", GUILayout.Width(72f)))
                _resultText = UnityEngine.Random.Range(1, 21).ToString();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Request", GUILayout.Width(64f));
            _requestId = GUILayout.TextField(_requestId);
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label("Motion path");
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_profileIndex < 0, "Auto", GUI.skin.button))
                _profileIndex = -1;
            if (presenter != null)
            {
                for (int i = 0; i < presenter.MotionProfiles.Count; i++)
                {
                    DiceMotionProfile profile = presenter.MotionProfiles[i];
                    if (GUILayout.Toggle(_profileIndex == i, profile.DisplayName, GUI.skin.button))
                        _profileIndex = i;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label("Presentation region");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < PreviewRegionNames.Length; i++)
            {
                if (GUILayout.Toggle(_regionIndex == i, PreviewRegionNames[i], GUI.skin.button))
                {
                    _regionIndex = i;
                    presenter?.SetPresentationRegion(PreviewRegions[i]);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Play / Replay"))
                ShowCurrent(_requestId);
            if (GUILayout.Button("Server Roll"))
            {
                _sequenceActive = false;
                DiceRollNetworkBridge.RequestPreview(_requestId, 20);
            }
            if (GUILayout.Button("Skip"))
                presenter?.SkipToResult();
            if (GUILayout.Button("Dismiss"))
            {
                _sequenceActive = false;
                DiceRollNetworkBridge.DismissActiveRoll();
            }
            GUILayout.EndHorizontal();

            if (GUILayout.Button(_sequenceActive ? "Stop 1–20 sequence" : "Run results 1–20"))
            {
                if (_sequenceActive)
                {
                    _sequenceActive = false;
                }
                else
                {
                    _sequenceActive = true;
                    _sequenceSerial++;
                    _resultText = "1";
                    _heldSince = -1f;
                    ShowCurrent($"local-sequence-{_sequenceSerial}-1");
                }
            }

            GUILayout.Space(10f);
            GUILayout.Label("Click/tap the moving die to skip.");
            GUILayout.Label("Held results ignore input and remain until Dismiss.");
            GUILayout.Label(
                IsDiceOverlayLabActive()
                    ? "Dice lab controls remain visible in this scene."
                    : "- or keypad - toggles this panel in runtime scenes.");
            GUILayout.Space(6f);
            GUILayout.Label($"Server: {DiceRollNetworkBridge.Status}");
            GUILayout.EndArea();
        }

        private static bool IsDiceOverlayLabActive()
        {
            Scene scene = SceneManager.GetActiveScene();
            return scene.IsValid() &&
                   scene.isLoaded &&
                   string.Equals(scene.name, DiceOverlayLabSceneName, StringComparison.Ordinal);
        }

        private void ResolvePresenter()
        {
            if (presenter == null)
                presenter = DiceOverlayPresenter.Instance ?? FindAnyObjectByType<DiceOverlayPresenter>();
        }

        private void ShowCurrent(string requestId)
        {
            ResolvePresenter();
            if (presenter == null)
                return;

            presenter.Dismiss();
            int result = ParseResult();
            DiceMotionProfile? profile = _profileIndex >= 0 &&
                                         _profileIndex < presenter.MotionProfiles.Count
                ? presenter.MotionProfiles[_profileIndex]
                : null;
            presenter.Show(
                new ResolvedDiceRoll(requestId, "d20", result),
                profile,
                PreviewRegions[_regionIndex]);
        }

        private int ParseResult()
        {
            return int.TryParse(_resultText, out int value)
                ? Mathf.Clamp(value, 1, 20)
                : 20;
        }

        private void SetResult(int value)
        {
            _resultText = (1 + Mod(value - 1, 20)).ToString();
        }

        private static int Mod(int value, int modulus)
        {
            int remainder = value % modulus;
            return remainder < 0 ? remainder + modulus : remainder;
        }
#endif
    }
}
