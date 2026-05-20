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
        private const string DefaultClassId = "WARRIOR";
        private const string HubSceneName = "Hub";

        private CharacterAvatarAssembler? _previewAssembler;
        private Button? _warriorButton;
        private Button? _paladinButton;
        private Button? _archerButton;
        private Button? _createButton;
        private TMP_Text? _raceSexText;
        private TMP_Text? _classText;
        private TMP_Text? _statusText;
        private Transform? _previewAnchor;
        private GameObject? _previewDragSurface;
        private DbConnection? _subscribedConnection;
        private string _selectedClassId = DefaultClassId;
        private string _lastPreviewClassId = string.Empty;
        private bool _pendingCreate;

        private void OnEnable()
        {
            Resolve();
            Wire();
            TrySubscribeToReducerErrors();
            SelectClass(_selectedClassId);
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
            _warriorButton = root.Find("CharacterCreationCanvas/LeftPanel/ClassButtons/WarriorButton")?.GetComponent<Button>();
            _paladinButton = root.Find("CharacterCreationCanvas/LeftPanel/ClassButtons/PaladinButton")?.GetComponent<Button>();
            _archerButton = root.Find("CharacterCreationCanvas/LeftPanel/ClassButtons/ArcherButton")?.GetComponent<Button>();
            _createButton = root.Find("CharacterCreationCanvas/BottomBar/CreateButton")?.GetComponent<Button>();
            _raceSexText = root.Find("CharacterCreationCanvas/LeftPanel/RaceSexValue")?.GetComponent<TMP_Text>();
            _classText = root.Find("CharacterCreationCanvas/LeftPanel/ClassValue")?.GetComponent<TMP_Text>();
            _statusText = root.Find("CharacterCreationCanvas/BottomBar/StatusText")?.GetComponent<TMP_Text>();
            EnsurePreviewDragSurface(root);
        }

        private void Wire()
        {
            if (_warriorButton != null)
            {
                _warriorButton.onClick.RemoveAllListeners();
                _warriorButton.onClick.AddListener(() => SelectClass("WARRIOR"));
            }

            if (_paladinButton != null)
            {
                _paladinButton.onClick.RemoveAllListeners();
                _paladinButton.onClick.AddListener(() => SelectClass("PALADIN"));
            }

            if (_archerButton != null)
            {
                _archerButton.onClick.RemoveAllListeners();
                _archerButton.onClick.AddListener(() => SelectClass("RANGER"));
            }

            if (_createButton != null)
            {
                _createButton.onClick.RemoveAllListeners();
                _createButton.onClick.AddListener(CreateCharacter);
            }
        }

        private void SelectClass(string classId)
        {
            _selectedClassId = string.IsNullOrWhiteSpace(classId)
                ? DefaultClassId
                : classId.Trim().ToUpperInvariant();

            if (_raceSexText != null)
                _raceSexText.text = "HUMAN / MALE";
            if (_classText != null)
                _classText.text = _selectedClassId;

            SetButtonSelected(_warriorButton, _selectedClassId == "WARRIOR");
            SetButtonSelected(_paladinButton, _selectedClassId == "PALADIN");
            SetButtonSelected(_archerButton, _selectedClassId == "RANGER");
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (_previewAssembler == null ||
                string.Equals(_lastPreviewClassId, _selectedClassId, System.StringComparison.Ordinal))
            {
                return;
            }

            if (_previewAssembler.TryApplyClassDefault(_selectedClassId, out _, out string error))
            {
                _lastPreviewClassId = _selectedClassId;
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

            string outfitId = ResolveDefaultOutfitId(_selectedClassId);
            CharacterAppearanceSelection selection = CharacterAppearanceSelection.DefaultHumanMale(outfitId);
            conn.Reducers.CreateOrUpdateCharacter(
                _selectedClassId,
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

        private static string ResolveDefaultOutfitId(string classId)
        {
            if (!CharacterAppearanceCatalogSet.TryLoadDefault(out CharacterAppearanceCatalogSet catalogs, out _))
                return CharacterAppearanceIds.DefaultOutfitId;

            return catalogs.ClassOutfitCatalog.TryGetDefaultOutfitId(
                classId,
                CharacterAppearanceIds.DefaultRaceId,
                CharacterAppearanceIds.DefaultSexId,
                out string outfitId)
                ? outfitId
                : CharacterAppearanceIds.DefaultOutfitId;
        }

        private static void SetButtonSelected(Button? button, bool selected)
        {
            if (button == null)
                return;

            ColorBlock colors = button.colors;
            colors.normalColor = selected ? new Color(0.72f, 0.08f, 0.04f, 0.96f) : new Color(0.16f, 0.17f, 0.20f, 0.96f);
            colors.selectedColor = colors.normalColor;
            colors.highlightedColor = selected ? new Color(0.82f, 0.12f, 0.06f, 1f) : new Color(0.22f, 0.23f, 0.27f, 1f);
            button.colors = colors;
        }

        private void SetStatus(string message)
        {
            if (_statusText != null)
                _statusText.text = message;
        }
    }
}
