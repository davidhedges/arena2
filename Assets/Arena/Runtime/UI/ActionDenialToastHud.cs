#nullable enable

using Arena.Simulation;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>
    /// Denial toast just above the action bar (feel audit F1 step 4): renders
    /// the server's machine-readable rejection reason as short honest text
    /// whenever a predicted action is rolled back. Presentation only — no
    /// sound, no shake, no client-side validation, and gameplay reads nothing
    /// from it. Show/rate-limit/expiry bookkeeping lives in
    /// <see cref="ActionDenialToastModel"/> so it stays editor-testable.
    /// </summary>
    internal sealed class ActionDenialToastHud : MonoBehaviour
    {
        private const float FadeTailFraction = 0.25f;

        private static readonly Color TextColor = new(1f, 0.55f, 0.45f);

        private readonly ActionDenialToastModel _model = new();
        private GameObject _toastRoot = null!;
        private CanvasGroup _group = null!;
        private Text _label = null!;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<ActionDenialToastHud>() != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("ActionDenialToast");
            DontDestroyOnLoad(go);
            go.AddComponent<ActionDenialToastHud>();
        }

        private void Awake()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 12;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            // Just above the discipline row that tops the action bars
            // (BuildActionBars: bars at y=10, discipline box 12 px above them).
            float toastY =
                10f + ActionBarLayout.GridSize.y + 12f
                + ActionBarLayout.SlotSize + 16f
                + 12f;

            _toastRoot = new GameObject("Toast");
            _toastRoot.transform.SetParent(transform, false);
            var rt = _toastRoot.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(460f, 30f);
            rt.anchoredPosition = new Vector2(0f, toastY);

            _group = _toastRoot.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;

            var labelGo = new GameObject("Text");
            labelGo.transform.SetParent(_toastRoot.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.sizeDelta = Vector2.zero;
            labelRt.anchoredPosition = Vector2.zero;

            _label = labelGo.AddComponent<Text>();
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _label.fontSize = 20;
            _label.fontStyle = FontStyle.Bold;
            _label.alignment = TextAnchor.MiddleCenter;
            _label.color = TextColor;
            _label.raycastTarget = false;

            var shadow = labelGo.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            shadow.effectDistance = new Vector2(1f, -1f);

            _toastRoot.SetActive(false);
        }

        private void OnEnable()
        {
            LocalCombatState.PredictionRejected += OnPredictionRejected;
            LocalInteractionState.InteractionDenied += OnInteractionDenied;
        }

        private void OnDisable()
        {
            LocalCombatState.PredictionRejected -= OnPredictionRejected;
            LocalInteractionState.InteractionDenied -= OnInteractionDenied;
        }

        private void OnPredictionRejected(string actionKind, string pressedActionId, ActionRejectReason reason)
        {
            _ = actionKind;
            _ = pressedActionId;
            _model.TryShow(ActionDenialText.For(reason), Time.unscaledTimeAsDouble);
        }

        private void OnInteractionDenied(string reason)
        {
            _model.TryShow(reason, Time.unscaledTimeAsDouble);
        }

        private void Update()
        {
            string? text = _model.Tick(Time.unscaledTimeAsDouble);
            bool visible = text != null;
            if (_toastRoot.activeSelf != visible)
                _toastRoot.SetActive(visible);
            if (!visible)
                return;

            _label.text = text;
            float remaining = _model.RemainingFraction(Time.unscaledTimeAsDouble);
            _group.alpha = Mathf.Clamp01(remaining / FadeTailFraction);
        }
    }
}
