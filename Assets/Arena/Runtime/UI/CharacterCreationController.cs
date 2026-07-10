#nullable enable

using Arena.Network;
using Arena.Presentation;
using Arena.Presentation.Appearance;
using System.Linq;
using SpacetimeDB;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Arena.UI
{
    public sealed class CharacterCreationController : MonoBehaviour
    {
        private const string HubSceneName = "Hub";

        private CharacterAvatarAssembler? _previewAssembler;
        private Button? _mainHandSummary;
        private Button? _offHandSummary;
        private Button? _spellSlotSummary;
        private Button? _createButton;
        private TMP_Text? _raceSexText;
        private TMP_Text? _gearText;
        private TMP_Text? _statusText;
        private Transform? _previewAnchor;
        private GameObject? _previewDragSurface;
        private DbConnection? _subscribedConnection;
        private bool _previewApplied;
        private bool _pendingCreate;

        private void OnEnable()
        {
            Resolve();
            Wire();
            TrySubscribeToReducerErrors();
            SelectCharacterBaseline();
        }

        private void OnDisable()
        {
            UnsubscribeFromReducerErrors();
        }

        private void Update()
        {
            TrySubscribeToReducerErrors();
            if (_pendingCreate)
                LoadHubWhenCreationCompletes();
        }

        private void Resolve()
        {
            Transform root = transform;
            _previewAssembler = root.Find("StageRoot/PreviewAnchor")?.GetComponent<CharacterAvatarAssembler>();
            _previewAnchor = root.Find("StageRoot/PreviewAnchor");
            _mainHandSummary = root.Find("CharacterCreationCanvas/LeftPanel/GearSummary/MainHandSummary")?.GetComponent<Button>();
            _offHandSummary = root.Find("CharacterCreationCanvas/LeftPanel/GearSummary/OffHandSummary")?.GetComponent<Button>();
            _spellSlotSummary = root.Find("CharacterCreationCanvas/LeftPanel/GearSummary/SpellSlotSummary")?.GetComponent<Button>();
            _createButton = root.Find("CharacterCreationCanvas/BottomBar/CreateButton")?.GetComponent<Button>();
            _raceSexText = root.Find("CharacterCreationCanvas/LeftPanel/RaceSexValue")?.GetComponent<TMP_Text>();
            _gearText = root.Find("CharacterCreationCanvas/LeftPanel/GearValue")?.GetComponent<TMP_Text>();
            _statusText = root.Find("CharacterCreationCanvas/BottomBar/StatusText")?.GetComponent<TMP_Text>();
            EnsurePreviewDragSurface(root);
        }

        private void Wire()
        {
            if (_mainHandSummary != null)
            {
                _mainHandSummary.onClick.RemoveAllListeners();
                _mainHandSummary.interactable = false;
            }

            if (_offHandSummary != null)
            {
                _offHandSummary.onClick.RemoveAllListeners();
                _offHandSummary.interactable = false;
            }

            if (_spellSlotSummary != null)
            {
                _spellSlotSummary.onClick.RemoveAllListeners();
                _spellSlotSummary.interactable = false;
            }

            if (_createButton != null)
            {
                _createButton.onClick.RemoveAllListeners();
                _createButton.onClick.AddListener(CreateCharacter);
                StyleCreateButton(_createButton);
            }
        }

        private static void StyleCreateButton(Button button)
        {
            Image? image = button.GetComponent<Image>();
            if (image != null)
                image.color = ArenaUiTheme.Accent;

            // Fill lives on the Image; the ColorBlock only applies white-based
            // multipliers so the accent isn't tinted twice.
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.selectedColor = colors.normalColor;
            colors.highlightedColor = new Color(1.14f, 1.14f, 1.14f, 1f);
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            button.colors = colors;

            foreach (TMP_Text label in button.GetComponentsInChildren<TMP_Text>(true))
                label.color = ArenaUiTheme.OnAccent;
        }

        private void SelectCharacterBaseline()
        {
            if (_raceSexText != null)
                _raceSexText.text = "HUMAN / MALE";
            if (_gearText != null)
                _gearText.text = "SWORD & SHIELD";

            SetButtonSelected(_mainHandSummary, false);
            SetButtonSelected(_offHandSummary, false);
            SetButtonSelected(_spellSlotSummary, false);
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (_previewAssembler == null || _previewApplied)
            {
                return;
            }

            if (_previewAssembler.TryApplyStarterDefault(out _, out string error))
            {
                _previewApplied = true;
                SetStatus(string.Empty);
            }
            else
            {
                SetStatus(error);
            }
        }

        private void EnsurePreviewDragSurface(Transform root)
        {
            if (_previewAnchor == null)
                return;

            Transform? canvas = root.Find("CharacterCreationCanvas");
            if (canvas == null)
                return;

            Transform existing = canvas.Find("PreviewDragSurface");
            RectTransform dragRect;
            if (existing != null)
            {
                _previewDragSurface = existing.gameObject;
                dragRect = (RectTransform)existing;
            }
            else
            {
                _previewDragSurface = new GameObject("PreviewDragSurface", typeof(RectTransform));
                _previewDragSurface.transform.SetParent(canvas, false);
                dragRect = (RectTransform)_previewDragSurface.transform;
                Image image = _previewDragSurface.AddComponent<Image>();
                image.color = new Color(0f, 0f, 0f, 0f);
                image.raycastTarget = true;
            }

            dragRect.anchorMin = new Vector2(0.28f, 0.14f);
            dragRect.anchorMax = new Vector2(1f, 1f);
            dragRect.offsetMin = Vector2.zero;
            dragRect.offsetMax = Vector2.zero;
            dragRect.SetAsFirstSibling();

            HubShowcaseRotator rotator =
                _previewDragSurface.GetComponent<HubShowcaseRotator>() ??
                _previewDragSurface.AddComponent<HubShowcaseRotator>();
            rotator.Configure(_previewAnchor, 0.4f);
        }

        private void CreateCharacter()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                SetStatus("Not connected.");
                return;
            }

            _pendingCreate = true;
            SetStatus("Creating character...");
            if (_createButton != null)
                _createButton.interactable = false;

            string outfitId = ResolveDefaultOutfitId();
            CharacterAppearanceSelection selection = CharacterAppearanceSelection.DefaultHumanMale(outfitId);
            conn.Reducers.CreateOrUpdateCharacter(
                selection.raceId,
                selection.sexId,
                selection.bodyId,
                selection.headId,
                selection.faceId,
                selection.hairId,
                selection.eyesId,
                selection.outfitId);
        }

        private void LoadHubWhenCreationCompletes()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            Identity? identity = conn?.Identity;
            if (conn == null || !identity.HasValue)
                return;

            CharacterAppearance? appearance = conn.Db.CharacterAppearance.Owner.Filter(identity.Value).FirstOrDefault();
            if (appearance == null || !appearance.CreationComplete)
                return;

            SceneManager.LoadScene(HubSceneName);
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

        private void OnUnhandledReducerError(ReducerEventContext ctx, System.Exception error)
        {
            if (ctx.Event.Reducer is not Reducer.CreateOrUpdateCharacter)
                return;

            _pendingCreate = false;
            if (_createButton != null)
                _createButton.interactable = true;
            SetStatus(error.Message);
            Debug.LogWarning($"[{nameof(CharacterCreationController)}] Character creation rejected: {error.Message}");
        }

        private static string ResolveDefaultOutfitId()
        {
            return CharacterAppearanceIds.DefaultOutfitId;
        }

        private static void SetButtonSelected(Button? button, bool selected)
        {
            if (button == null)
                return;

            ColorBlock colors = button.colors;
            colors.normalColor = selected ? ArenaUiTheme.Accent : ArenaUiTheme.RowAlt;
            colors.selectedColor = colors.normalColor;
            colors.highlightedColor = selected ? ArenaUiTheme.AccentHot : ArenaUiTheme.CellFilled;
            button.colors = colors;
        }

        private void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = message;
        }
    }
}
