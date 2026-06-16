#nullable enable
using System;
using Arena.Network;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>
    /// Playground-only target spawner for testing targeting rules and party frames.
    /// This is intentionally separate from normal HUD, party, and match UI.
    /// </summary>
    public sealed class PlaygroundTargetsPanel : MonoBehaviour
    {
        private const float Pad = 10f;
        private const float ButtonTopOffset = 130f;
        private const float MenuTopOffset = 160f;
        private const string KindHostile = "HOSTILE";
        private const string KindNeutral = "NEUTRAL";
        private const string KindPartyMember = "PARTY_MEMBER";
        private const string KindMobHostile = "MOB_HOSTILE";
        private const string KindMobNeutral = "MOB_NEUTRAL";
        private const string KindMobFriendly = "MOB_FRIENDLY";

        private GameObject _menuRoot = null!;
        private Text _statusText = null!;
        private DbConnection? _subscribedConnection;
        private float _statusUntilTime;

        private void Awake()
        {
            Build();
        }

        private void Build()
        {
            var toggleButton = MakeHudButton(transform, "PlaygroundToggleButton", "PLAYGROUND",
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(122f, 24f), new Vector2(-Pad, -ButtonTopOffset),
                new Color(0.16f, 0.10f, 0.24f, 0.94f));
            toggleButton.onClick.AddListener(ToggleMenu);

            _menuRoot = Panel("PlaygroundTargetsMenu", transform,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(206f, 268f), new Vector2(-Pad, -MenuTopOffset));
            Img(_menuRoot, new Color(0.025f, 0.025f, 0.035f, 0.92f));
            var outline = _menuRoot.AddComponent<Outline>();
            outline.effectColor = new Color(0.38f, 0.22f, 0.52f, 0.95f);
            outline.effectDistance = new Vector2(1f, 1f);

            var title = Label(_menuRoot.transform, "Title",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(190f, 22f), new Vector2(0f, -8f),
                11, new Color(0.86f, 0.72f, 1f), TextAnchor.MiddleCenter);
            title.text = "PLAYGROUND TARGETS";
            title.fontStyle = FontStyle.Bold;

            BuildTargetButton("HostileButton", "PLAYER HOSTILE", 0, false, () => SpawnTarget(KindHostile));
            BuildTargetButton("NeutralButton", "PLAYER NEUTRAL", 1, false, () => SpawnTarget(KindNeutral));
            BuildTargetButton("PartyMemberButton", "PLAYER FRIENDLY", 2, false, () => SpawnTarget(KindPartyMember));
            BuildTargetButton("MobHostileButton", "KOBOLD HOSTILE", 3, false, () => SpawnTarget(KindMobHostile));
            BuildTargetButton("MobNeutralButton", "KOBOLD NEUTRAL", 4, false, () => SpawnTarget(KindMobNeutral));
            BuildTargetButton("MobFriendlyButton", "KOBOLD FRIENDLY", 5, false, () => SpawnTarget(KindMobFriendly));
            BuildTargetButton("ClearButton", "CLEAR", 6, true, ClearTargets);

            _statusText = Label(_menuRoot.transform, "Status",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(190f, 18f), new Vector2(0f, -238f),
                9, new Color(0.75f, 0.80f, 0.90f), TextAnchor.MiddleCenter);
            _statusText.text = string.Empty;
            _statusText.resizeTextForBestFit = true;
            _statusText.resizeTextMinSize = 6;
            _statusText.resizeTextMaxSize = 9;

            _menuRoot.SetActive(false);
        }

        private void BuildTargetButton(string name, string label, int row, bool destructive, UnityEngine.Events.UnityAction action)
        {
            var button = MakeHudButton(_menuRoot.transform, name, label,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(176f, 24f), new Vector2(0f, -36f - row * 28f),
                destructive
                    ? new Color(0.28f, 0.08f, 0.10f, 0.94f)
                    : new Color(0.12f, 0.13f, 0.18f, 0.94f));
            button.onClick.AddListener(action);
        }

        private void ToggleMenu()
        {
            _menuRoot.SetActive(!_menuRoot.activeSelf);
        }

        private void OnEnable()
        {
            TrySubscribeToReducerErrors();
        }

        private void Update()
        {
            TrySubscribeToReducerErrors();
            if (_statusText != null && _statusUntilTime > 0f && Time.unscaledTime >= _statusUntilTime)
            {
                _statusText.text = string.Empty;
                _statusUntilTime = 0f;
            }
        }

        private void OnDisable()
        {
            UnsubscribeFromReducerErrors();
        }

        private void OnDestroy()
        {
            UnsubscribeFromReducerErrors();
        }

        private void SpawnTarget(string kind)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                SetStatus("NO CONNECTION", true);
                return;
            }

            try
            {
                conn.Reducers.SpawnPlaygroundTarget(kind);
                SetStatus($"{LabelForKind(kind)} SENT", false);
            }
            catch (Exception error)
            {
                SetStatus("SPAWN FAILED", true);
                Debug.LogWarning($"[{nameof(PlaygroundTargetsPanel)}] Spawn request failed locally: {error.Message}");
            }
        }

        private void ClearTargets()
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                SetStatus("NO CONNECTION", true);
                return;
            }

            try
            {
                conn.Reducers.DespawnAllPlaygroundTargets();
                SetStatus("CLEAR SENT", false);
            }
            catch (Exception error)
            {
                SetStatus("CLEAR FAILED", true);
                Debug.LogWarning($"[{nameof(PlaygroundTargetsPanel)}] Clear request failed locally: {error.Message}");
            }
        }

        private void TrySubscribeToReducerErrors()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || ReferenceEquals(_subscribedConnection, conn))
                return;

            UnsubscribeFromReducerErrors();
            _subscribedConnection = conn;
            _subscribedConnection.OnUnhandledReducerError += OnUnhandledReducerError;
        }

        private void UnsubscribeFromReducerErrors()
        {
            if (_subscribedConnection == null)
                return;

            _subscribedConnection.OnUnhandledReducerError -= OnUnhandledReducerError;
            _subscribedConnection = null;
        }

        private void OnUnhandledReducerError(ReducerEventContext ctx, Exception error)
        {
            if (ctx.Event.Reducer is not Reducer.SpawnPlaygroundTarget
                and not Reducer.DespawnAllPlaygroundTargets
                and not Reducer.DespawnPlaygroundTarget)
            {
                return;
            }

            SetStatus(error.Message, true);
            Debug.LogWarning($"[{nameof(PlaygroundTargetsPanel)}] Playground reducer rejected: {error.Message}");
        }

        private void SetStatus(string message, bool error)
        {
            if (_statusText == null)
                return;

            _statusText.color = error
                ? new Color(1f, 0.45f, 0.36f)
                : new Color(0.65f, 0.95f, 0.75f);
            _statusText.text = message;
            _statusUntilTime = Time.unscaledTime + 4f;
        }

        private static string LabelForKind(string kind)
        {
            return kind switch
            {
                KindHostile => "PLAYER HOSTILE",
                KindNeutral => "PLAYER NEUTRAL",
                KindPartyMember => "PLAYER FRIENDLY",
                KindMobHostile => "KOBOLD HOSTILE",
                KindMobNeutral => "KOBOLD NEUTRAL",
                KindMobFriendly => "KOBOLD FRIENDLY",
                _ => "SPAWN",
            };
        }

        private static GameObject Panel(
            string name,
            Transform parent,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 size,
            Vector2 offset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = offset;
            return go;
        }

        private static void Img(GameObject go, Color color)
        {
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        private static Text Label(
            Transform parent,
            string name,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 size,
            Vector2 offset,
            int fontSize,
            Color color,
            TextAnchor alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = offset;

            var txt = go.AddComponent<Text>();
            txt.font = Font();
            txt.fontSize = fontSize;
            txt.alignment = alignment;
            txt.color = color;
            txt.raycastTarget = false;

            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return txt;
        }

        private static Button MakeHudButton(
            Transform parent,
            string name,
            string text,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 size,
            Vector2 offset,
            Color fill)
        {
            var go = Panel(name, parent, anchor, pivot, size, offset);
            var image = go.AddComponent<Image>();
            image.color = fill;
            image.raycastTarget = true;

            var button = go.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.disabledColor = new Color(0.35f, 0.35f, 0.35f, 0.55f);
            button.colors = colors;

            var label = Label(go.transform, "Label",
                Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero,
                10, Color.white, TextAnchor.MiddleCenter);
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.sizeDelta = Vector2.zero;
            labelRect.anchoredPosition = Vector2.zero;
            label.fontStyle = FontStyle.Bold;
            label.text = text;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 7;
            label.resizeTextMaxSize = 10;

            return button;
        }

        private static Font Font() =>
            Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }
}
