#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Arena.Entity;
using Arena.Simulation;
using Arena.Network;
using Arena.Combat;
using Arena.Input;
using Arena.Presentation;
using SpacetimeDB.Types;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;

namespace Arena.UI
{
    /// <summary>
    /// WoW-inspired HUD built procedurally via UGUI.
    /// Self-bootstraps via RuntimeInitializeOnLoadMethod — no scene YAML required.
    ///
    /// Layout:
    ///   Player frame   — top-left
    ///   Target frame   — top-left, right of player frame (with buff/debuff rows beneath)
    ///   Self buffs     — top-right
    ///   Cast bar       — above action bars, center
    ///   Action bars    — bottom-center, matching the shared action-bar grid
    ///   Respawn timer  — center screen (no overlay)
    ///
    /// INVARIANT: No network writes. Read-only presentation.
    /// </summary>
    public class HUDController : MonoBehaviour
    {
        private const string UnitFrameSpritePath = "UI/UnitFrame/UnitFrame";
        private const string TemporaryHitpointsStatusKind = "TEMPORARY_HITPOINTS";

        // --- Colors (WoW-inspired) ---
        private static readonly Color FrameBg       = Hex("#1a1a1a");
        private static readonly Color FrameBorder   = Hex("#333333");
        private static readonly Color HealthGreen   = new(0.2f, 0.75f, 0.3f);
        private static readonly Color ShieldGold    = new(1f, 0.85f, 0f, 0.8f);
        private static readonly Color ResourceBlue  = Hex("#3A6EA5");
        private static readonly Color SlotBg        = Hex("#1a1a1a");
        private static readonly Color SlotBorder    = Hex("#444444");
        private static readonly Color DisabledSlotBg = Hex("#2A2A2A");
        private static readonly Color TransparentSlotInput = new(1f, 1f, 1f, 0f);
        // Rejection denial flash (netcode design review S2): matches the
        // denial toast's red, presentation only.
        private static readonly Color RejectFlashColor = new(1f, 0.32f, 0.25f, 0.55f);
        private const float SlotRejectionFlashSeconds = 0.45f;
        // Advisory LOS gray-out (netcode design review S4): full-slot dim while
        // the selected target has no line of sight, presentation only.
        private static readonly Color LosBlockedOverlay = new(0.02f, 0.02f, 0.02f, 0.62f);
        private static readonly Color CdOverlay     = new(0f, 0f, 0f, 0.7f);
        private static readonly Color GcdOverlay    = new(0f, 0f, 0f, 0.4f);
        private static readonly Color CastGold      = Hex("#FFD100");
        private static readonly Color CastOrange    = Hex("#FF8800");
        private static readonly Color CastBg        = Hex("#222222");
        private static readonly Color BuffBg        = Hex("#224422");
        private static readonly Color DebuffBg      = Hex("#442222");
        private static readonly Color RelationSelfColor = Hex("#C8E6FF");
        private static readonly Color RelationPartyColor = Hex("#76D36F");
        private static readonly Color RelationNeutralColor = Hex("#E2D28B");
        private static readonly Color RelationHostileColor = Hex("#FF5555");

        // --- Layout ---
        // Unit-frame tuning guide:
        // - The source art is Assets/Arena/Resources/UI/UnitFrame/UnitFrame.png at 720x224.
        // - The runtime frame is rendered at 360x112, so 1 HUD unit here maps to 2 source pixels.
        // - Unit-frame child elements use bottom-left anchors/pivots; (0,0) is the lower-left
        //   corner of the full unit-frame rect, not the screen or source PNG.
        // - Fill shapes are listed clockwise as lower-left, lower-right, upper-right, upper-left.
        // - Tune only the non-mirrored player shapes below. Target geometry is derived by
        //   MirroredUnitFrameShape/MirroredUnitFramePos so both sides stay symmetrical.
        // - *Pos/*Size values tune text label rectangles. The actual colored fill geometry is
        //   controlled by UnitFrameHealthShape/UnitFrameResourceShape.
        private const float UnitFrameW = 360f;
        private const float UnitFrameH = 112f;

        // Text rectangles for the value labels centered over the shaped bars.
        // Keep these slightly inset from the polygon edges so labels do not sit on the bevels.
        private static readonly Vector2 UnitFrameHealthPos = new(108f, 55f);
        private static readonly Vector2 UnitFrameHealthSize = new(228f, 31f);
        private static readonly Vector2 UnitFrameResourcePos = new(108f, 25f);
        private static readonly Vector2 UnitFrameResourceSize = new(202f, 30f);
        private static readonly Vector2 UnitFrameStaminaPos = new(108f, 39f);
        private static readonly Vector2 UnitFrameManaPos = new(108f, 25f);
        private static readonly Vector2 UnitFrameSplitResourceSize = new(202f, 14f);

        // Colored fill polygons. The left edge starts after the portrait overlap; the right
        // points are intentionally shaved back from the frame tip so the color does not cover
        // the metal bevel.
        // Health vertical tuning:
        // - Top edge: change both 86f values below. Lower both to move the top edge down.
        // - Bottom edge: change both 55f values below. Raise both to move the bottom edge up.
        // - Height is top Y - bottom Y.
        private static readonly Vector2[] UnitFrameHealthShape =
        {
            // Lower-left: move X right to clear more of the portrait frame; move Y to tune the bottom edge.
            new(108f, 57f),
            // Lower-right: move X left/right to shorten/extend the bottom edge before the angled tip.
            new(321f, 57f),
            // Upper-right: move X left/right to tune the tip length; move Y to tune the top edge.
            new(347f, 84f),
            // Upper-left: keep X aligned with lower-left for a vertical left edge; move Y to tune the top edge.
            new(108f, 84f),
        };

        // Resource vertical tuning:
        // - Top edge: change both 55f values below. Lower both to move the top edge down.
        // - Bottom edge: change both 25f values below. Raise both to move the bottom edge up.
        // - Height is top Y - bottom Y.
        private static readonly Vector2[] UnitFrameResourceShape =
        {
            new(108f, 26f),
            new(292f, 26f),
            new(318f, 53f),
            new(108f, 53f),
        };
        private static readonly Vector2[] UnitFrameStaminaShape =
        {
            new(108f, 40f),
            new(304f, 40f),
            new(318f, 53f),
            new(108f, 53f),
        };
        private static readonly Vector2[] UnitFrameManaShape =
        {
            new(108f, 26f),
            new(292f, 26f),
            new(304f, 38f),
            new(108f, 38f),
        };
        private const float Pad    = 10f;
        private const float IconSz = 28f;
        private const float IconGap = 2f;
        private const int PartyFrameCount = 4;
        private const float PartyUnitFrameScale = 0.7f;
        private const float PartyUnitFrameW = UnitFrameW * PartyUnitFrameScale;
        private const float PartyUnitFrameH = UnitFrameH * PartyUnitFrameScale;
        private const float PartyFrameTopGap = 44f;
        private const float PartyFrameGap = 4f;
        private const float PartyMemberDebuffRowH = IconSz;
        private const float PartyMemberSlotH = PartyUnitFrameH + PartyMemberDebuffRowH + PartyFrameGap;
        private const float StatusIconRefreshInterval = 0.25f;
        private const float PartyRosterRefreshInterval = 0.50f;
        private const float PartyInviteRefreshInterval = 1.00f;
        private const float ActionBarStaticRefreshInterval = 0.25f;

        // --- Player Frame ---
        private Text  _playerName   = null!;
        private Image _playerHpFill = null!;
        private Image _playerTempHpFill = null!;
        private Text  _playerHpText = null!;
        private Image _playerPrimaryFill = null!;
        private Text  _playerPrimaryText = null!;
        private Image _playerManaFill = null!;
        private Text _playerManaText = null!;

        // --- Target Frame ---
        private GameObject _targetRoot  = null!;
        private Text       _targetName  = null!;
        private Image _targetTempHpFill = null!;
        private Image _targetHpFill = null!;
        private Text       _targetHpText = null!;
        private Image _targetPrimaryFill = null!;
        private Text       _targetPrimaryText = null!;
        private Text       _targetRelation = null!;
        private Button     _targetInviteButton = null!;
        private Transform  _tgtBuffRow  = null!;
        private Transform  _tgtDebuffRow = null!;
        private readonly List<GameObject> _tgtBuffIcons   = new();
        private readonly List<GameObject> _tgtDebuffIcons = new();
        private readonly Dictionary<GameObject, StatusIconView> _statusIconViews = new();

        // --- Party ---
        private GameObject _partyRoot = null!;
        private Button _partyLeaveButton = null!;
        private readonly List<PartyFrameView> _partyFrames = new();

        // --- Party Invites ---
        private GameObject _partyInviteRoot = null!;
        private Text _partyInviteText = null!;
        private Button _partyInviteAcceptButton = null!;
        private Button _partyInviteDeclineButton = null!;
        private ulong? _activeInviteId;

        // --- Self Buffs ---
        private Transform _selfBuffRow   = null!;
        private Transform _selfDebuffRow = null!;
        private readonly List<GameObject> _selfBuffIcons   = new();
        private readonly List<GameObject> _selfDebuffIcons = new();
        private GameObject? _selfCombatModeIcon;

        // --- Action Bars ---
        private readonly Image[] _abilityGridIcons = new Image[ActionBarLayout.CellCount];
        private readonly Image[] _abilityGridCd   = new Image[ActionBarLayout.CellCount];
        private readonly Text[]  _abilityGridText = new Text[ActionBarLayout.CellCount];
        private readonly Text[]  _abilityGridChargeText = new Text[ActionBarLayout.CellCount];
        private readonly TooltipTarget[] _abilityGridTooltips = new TooltipTarget[ActionBarLayout.CellCount];
        private readonly ActionBarClickTarget[] _abilityGridClicks = new ActionBarClickTarget[ActionBarLayout.CellCount];
        private readonly ActionBarSlotState[] _abilityGridStates = new ActionBarSlotState[ActionBarLayout.CellCount];
        private readonly Image[] _spellbookGridIcons = new Image[ActionBarLayout.Columns];
        private readonly Image[] _spellbookGridCd = new Image[ActionBarLayout.Columns];
        private readonly Text[] _spellbookGridText = new Text[ActionBarLayout.Columns];
        private readonly Text[] _spellbookGridChargeText = new Text[ActionBarLayout.Columns];
        private readonly TooltipTarget[] _spellbookGridTooltips = new TooltipTarget[ActionBarLayout.Columns];
        private readonly ActionBarClickTarget[] _spellbookGridClicks = new ActionBarClickTarget[ActionBarLayout.Columns];
        private readonly ActionBarSlotState[] _spellbookGridStates = new ActionBarSlotState[ActionBarLayout.Columns];
        private readonly Image[] _disciplineBarIcons = new Image[ActionBarSlotIds.DisciplineColumns];
        private readonly Image[] _disciplineBarCd = new Image[ActionBarSlotIds.DisciplineColumns];
        private readonly Text[] _disciplineBarText = new Text[ActionBarSlotIds.DisciplineColumns];
        private readonly Text[] _disciplineBarChargeText = new Text[ActionBarSlotIds.DisciplineColumns];
        private readonly TooltipTarget[] _disciplineBarTooltips = new TooltipTarget[ActionBarSlotIds.DisciplineColumns];
        private readonly ActionBarClickTarget[] _disciplineBarClicks = new ActionBarClickTarget[ActionBarSlotIds.DisciplineColumns];
        private readonly ActionBarSlotState[] _disciplineBarStates = new ActionBarSlotState[ActionBarSlotIds.DisciplineColumns];
        private readonly Image[] _abilityGridFlash = new Image[ActionBarLayout.CellCount];
        private readonly double[] _abilityGridFlashUntil = new double[ActionBarLayout.CellCount];
        private readonly Image[] _spellbookGridFlash = new Image[ActionBarLayout.Columns];
        private readonly double[] _spellbookGridFlashUntil = new double[ActionBarLayout.Columns];
        private readonly Image[] _disciplineBarFlash = new Image[ActionBarSlotIds.DisciplineColumns];
        private readonly double[] _disciplineBarFlashUntil = new double[ActionBarSlotIds.DisciplineColumns];
        private double _anySlotFlashActiveUntil;

        // --- Cast Bar ---
        private GameObject _castRoot     = null!;
        private Image      _castFill     = null!;
        private Text       _castName     = null!;
        private Text       _castTime     = null!;
        private long       _castStartMs;
        private long       _castEndMs;
        private string     _castKind     = "";
        private string     _castDisplayName = "";
        private bool       _castShowsCharge;
        private TimedActionPresentationStyle _castStyle;

        // --- Respawn ---
        private GameObject _respawnRoot = null!;
        private Text       _respawnText = null!;

        // --- Temp lists for status iteration ---
        private readonly List<StatusEffect> _tmpBuff   = new();
        private readonly List<StatusEffect> _tmpDebuff = new();
        private readonly List<PartyMember> _tmpPartyMembers = new();
        private readonly List<SpacetimeDB.Identity> _visiblePartyMembers = new();
        private Canvas _canvas = null!;
        private GraphicRaycaster _raycaster = null!;
        private SpacetimeDB.Identity? _lastTargetIdentity;
        private ulong? _lastPartyId;
        private float _nextTargetStaticRefreshTime;
        private float _nextTargetStatusRefreshTime;
        private float _nextSelfStatusRefreshTime;
        private float _nextPartyRosterRefreshTime;
        private float _nextPartyDebuffRefreshTime;
        private float _nextPartyInviteRefreshTime;
        private float _nextActionBarStaticRefreshTime;
        private bool _actionBarStaticDirty = true;

        // ---------------------------------------------------------------
        // Bootstrap
        // ---------------------------------------------------------------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("HUD");
            DontDestroyOnLoad(go);
            go.AddComponent<HUDController>();
        }

        // ---------------------------------------------------------------
        // Build
        // ---------------------------------------------------------------

        private void Awake()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 10;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution  = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight   = 0.5f;

            _raycaster = gameObject.AddComponent<GraphicRaycaster>();

            BuildPlayerFrame();
            BuildTargetFrame();
            BuildSelfBuffs();
            BuildPartyFrames();
            BuildPartyInvitePrompt();
            gameObject.AddComponent<PlaygroundTargetsPanel>();
            BuildActionBars();
            BuildCastBar();
            BuildRespawnTimer();
        }

        // ---------------------------------------------------------------
        // Player Frame (top-left)
        // ---------------------------------------------------------------

        private void BuildPlayerFrame()
        {
            var frame = Panel("PlayerFrame", new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(UnitFrameW, UnitFrameH), new Vector2(Pad, -Pad));
            var frameClickTarget = frame.AddComponent<UnitFrameClickTarget>();
            frameClickTarget.Configure(OnPlayerFrameSelected);
            AddUnitFrameArt(frame.transform, mirrored: false, raycastTarget: true);

            _playerName = Label(frame.transform, "Name",
                new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(86f, 20f), new Vector2(13f, 54f),
                13, Color.white, TextAnchor.MiddleCenter);

            _playerTempHpFill = BuildUnitFrameBarFill(
                frame.transform,
                "TempHp",
                UnitFrameHealthShape,
                ShieldGold);
            _playerTempHpFill.fillAmount = 0f;
            _playerTempHpFill.gameObject.SetActive(false);

            _playerHpFill = BuildUnitFrameBarFill(
                frame.transform,
                "Hp",
                UnitFrameHealthShape,
                HealthGreen);

            _playerHpText = Label(frame.transform, "HpText",
                new Vector2(0, 0), new Vector2(0, 0),
                UnitFrameHealthSize, UnitFrameHealthPos,
                12, Color.white, TextAnchor.MiddleCenter);

            _playerPrimaryFill = BuildUnitFrameBarFill(
                frame.transform,
                "Stamina",
                UnitFrameStaminaShape,
                ResourceBlue);
            SetUnitFrameBarFill(_playerPrimaryFill, 0f);
            _playerPrimaryText = Label(frame.transform, "StaminaText",
                new Vector2(0, 0), new Vector2(0, 0),
                UnitFrameSplitResourceSize, UnitFrameStaminaPos,
                9, Color.white, TextAnchor.MiddleCenter);
            _playerPrimaryText.text = "";

            _playerManaFill = BuildUnitFrameBarFill(
                frame.transform,
                "Mana",
                UnitFrameManaShape,
                ResourceBlue);
            SetUnitFrameBarFill(_playerManaFill, 0f);
            _playerManaText = Label(frame.transform, "ManaText",
                new Vector2(0, 0), new Vector2(0, 0),
                UnitFrameSplitResourceSize, UnitFrameManaPos,
                9, Color.white, TextAnchor.MiddleCenter);
            _playerManaText.text = "";

        }

        // ---------------------------------------------------------------
        // Target Frame (right of player frame, with buff/debuff rows)
        // ---------------------------------------------------------------

        private void BuildTargetFrame()
        {
            float x = Pad + UnitFrameW + 12f;
            _targetRoot = Panel("TargetFrame", new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(UnitFrameW, UnitFrameH), new Vector2(x, -Pad));
            AddUnitFrameArt(_targetRoot.transform, mirrored: true);

            _targetName = Label(_targetRoot.transform, "Name",
                new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(86f, 20f), new Vector2(UnitFrameW - 99f, 54f),
                13, new Color(1f, 0.3f, 0.3f), TextAnchor.MiddleCenter);
            _targetRelation = Label(_targetRoot.transform, "Relation",
                new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(86f, 14f), new Vector2(UnitFrameW - 99f, 75f),
                9, RelationHostileColor, TextAnchor.MiddleCenter);

            _targetInviteButton = MakeHudButton(_targetRoot.transform, "InviteButton", "INVITE",
                new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(74f, 20f), new Vector2(UnitFrameW - 93f, 90f),
                new Color(0.10f, 0.22f, 0.16f, 0.92f));
            _targetInviteButton.onClick.AddListener(OnInviteSelectedTarget);
            _targetInviteButton.gameObject.SetActive(false);

            Vector2 mirroredHealthPos = MirroredUnitFramePos(UnitFrameHealthPos, UnitFrameHealthSize);
            Vector2 mirroredResourcePos = MirroredUnitFramePos(UnitFrameResourcePos, UnitFrameResourceSize);
            _targetTempHpFill = BuildUnitFrameBarFill(
                _targetRoot.transform,
                "TempHp",
                MirroredUnitFrameShape(UnitFrameHealthShape),
                ShieldGold,
                fillFromRight: true);
            _targetTempHpFill.fillAmount = 0f;
            _targetTempHpFill.gameObject.SetActive(false);

            _targetHpFill = BuildUnitFrameBarFill(
                _targetRoot.transform,
                "Hp",
                MirroredUnitFrameShape(UnitFrameHealthShape),
                HealthGreen,
                fillFromRight: true);
            _targetHpText = Label(_targetRoot.transform, "HpText",
                new Vector2(0, 0), new Vector2(0, 0),
                UnitFrameHealthSize, mirroredHealthPos,
                12, Color.white, TextAnchor.MiddleCenter);

            _targetPrimaryFill = BuildUnitFrameBarFill(
                _targetRoot.transform,
                "Primary",
                MirroredUnitFrameShape(UnitFrameResourceShape),
                ResourceBlue,
                fillFromRight: true);
            SetUnitFrameBarFill(_targetPrimaryFill, 0f);
            _targetPrimaryText = Label(_targetRoot.transform, "PrimaryText",
                new Vector2(0, 0), new Vector2(0, 0),
                UnitFrameResourceSize, mirroredResourcePos,
                10, Color.white, TextAnchor.MiddleCenter);
            _targetPrimaryText.text = "";

            // Buff row beneath frame
            _tgtBuffRow = IconRow(_targetRoot.transform, "BuffRow",
                new Vector2(0, 0), new Vector2(0, 1),
                new Vector2(UnitFrameW, IconSz), new Vector2(6, -2));

            // Debuff row beneath buff row
            _tgtDebuffRow = IconRow(_targetRoot.transform, "DebuffRow",
                new Vector2(0, 0), new Vector2(0, 1),
                new Vector2(UnitFrameW, IconSz), new Vector2(6, -(IconSz + 4)));

            _targetRoot.SetActive(false);
        }

        // ---------------------------------------------------------------
        // Party Frames / Invites
        // ---------------------------------------------------------------

        private void BuildPartyFrames()
        {
            _partyRoot = Panel("PartyFrames", new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(PartyUnitFrameW, PartyMemberSlotH * PartyFrameCount + 28f),
                new Vector2(Pad, -(Pad + UnitFrameH + PartyFrameTopGap)));

            Vector2 partyFrameSize = new(PartyUnitFrameW, PartyUnitFrameH);
            Vector2[] partyHealthShape = ScaledUnitFrameShape(UnitFrameHealthShape, PartyUnitFrameScale);
            Vector2 partyHealthPos = UnitFrameHealthPos * PartyUnitFrameScale;
            Vector2 partyHealthSize = UnitFrameHealthSize * PartyUnitFrameScale;

            for (int i = 0; i < PartyFrameCount; i++)
            {
                var root = Child(_partyRoot.transform, $"Member_{i}",
                    new Vector2(0, 1), new Vector2(0, 1),
                    new Vector2(PartyUnitFrameW, PartyMemberSlotH),
                    new Vector2(0f, -i * PartyMemberSlotH));

                var frame = Child(root.transform, "UnitFrame",
                    new Vector2(0, 1), new Vector2(0, 1),
                    partyFrameSize, Vector2.zero);
                var frameClickTarget = frame.AddComponent<UnitFrameClickTarget>();
                AddUnitFrameArt(frame.transform, mirrored: false, raycastTarget: true);

                var name = Label(frame.transform, "Name",
                    new Vector2(0, 0), new Vector2(0, 0),
                    new Vector2(86f, 20f) * PartyUnitFrameScale,
                    new Vector2(13f, 54f) * PartyUnitFrameScale,
                    10, Color.white, TextAnchor.MiddleCenter);

                var tempHpFill = BuildUnitFrameBarFill(
                    frame.transform,
                    "TempHp",
                    partyHealthShape,
                    ShieldGold);
                tempHpFill.fillAmount = 0f;
                tempHpFill.gameObject.SetActive(false);

                var hpFill = BuildUnitFrameBarFill(
                    frame.transform,
                    "Hp",
                    partyHealthShape,
                    HealthGreen);

                var hpText = Label(frame.transform, "HpText",
                    new Vector2(0, 0), new Vector2(0, 0),
                    partyHealthSize, partyHealthPos,
                    9, Color.white, TextAnchor.MiddleCenter);

                var debuffRow = IconRow(root.transform, "DebuffRow",
                    new Vector2(0, 1), new Vector2(0, 1),
                    new Vector2(PartyUnitFrameW, PartyMemberDebuffRowH),
                    new Vector2(0f, -(PartyUnitFrameH + 2f)));

                _partyFrames.Add(new PartyFrameView(
                    root,
                    name,
                    tempHpFill,
                    hpFill,
                    hpText,
                    debuffRow,
                    frameClickTarget,
                    OnPartyFrameSelected));
            }

            _partyLeaveButton = MakeHudButton(_partyRoot.transform, "LeavePartyButton", "LEAVE PARTY",
                new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(112f, 24f), new Vector2(0f, -(PartyMemberSlotH * PartyFrameCount)),
                new Color(0.16f, 0.12f, 0.10f, 0.92f));
            _partyLeaveButton.onClick.AddListener(() => NetworkManager.Instance?.Conn?.Reducers.LeaveParty());

            _partyRoot.SetActive(false);
        }

        private void BuildPartyInvitePrompt()
        {
            _partyInviteRoot = Panel("PartyInvitePrompt", new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(360f, 76f), new Vector2(0f, -Pad));
            Img(_partyInviteRoot, new Color(0f, 0f, 0f, 0.82f));
            var outline = _partyInviteRoot.AddComponent<Outline>();
            outline.effectColor = FrameBorder;
            outline.effectDistance = new Vector2(1f, 1f);

            _partyInviteText = Label(_partyInviteRoot.transform, "Text",
                new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(332f, 28f), new Vector2(0f, -8f),
                13, Color.white, TextAnchor.MiddleCenter);

            _partyInviteAcceptButton = MakeHudButton(_partyInviteRoot.transform, "AcceptButton", "ACCEPT",
                new Vector2(0.5f, 0), new Vector2(1, 0),
                new Vector2(92f, 28f), new Vector2(-8f, 10f),
                new Color(0.08f, 0.28f, 0.12f, 0.95f));
            _partyInviteAcceptButton.onClick.AddListener(OnAcceptActiveInvite);

            _partyInviteDeclineButton = MakeHudButton(_partyInviteRoot.transform, "DeclineButton", "DECLINE",
                new Vector2(0.5f, 0), new Vector2(0, 0),
                new Vector2(92f, 28f), new Vector2(8f, 10f),
                new Color(0.28f, 0.08f, 0.08f, 0.95f));
            _partyInviteDeclineButton.onClick.AddListener(OnDeclineActiveInvite);

            _partyInviteRoot.SetActive(false);
        }

        // ---------------------------------------------------------------
        // Self Buffs/Debuffs (top-right)
        // ---------------------------------------------------------------

        private void BuildSelfBuffs()
        {
            _selfBuffRow = IconRow(transform, "SelfBuffs",
                new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(300, IconSz), new Vector2(-Pad, -Pad),
                TextAnchor.MiddleRight);

            _selfDebuffRow = IconRow(transform, "SelfDebuffs",
                new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(300, IconSz), new Vector2(-Pad, -(Pad + IconSz + 4)),
                TextAnchor.MiddleRight);
        }

        // ---------------------------------------------------------------
        // Action Bars (bottom-center, same shared action-bar grid)
        // ---------------------------------------------------------------

        private void BuildActionBars()
        {
            Vector2 gridSize = ActionBarLayout.GridSize;
            Vector2 disciplineSize = new(
                ActionBarSlotIds.DisciplineColumns * ActionBarLayout.SlotSize
                + (ActionBarSlotIds.DisciplineColumns - 1) * ActionBarLayout.Gap,
                ActionBarLayout.SlotSize);

            var box = Panel("ActionBars", new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                gridSize + new Vector2(16f, 16f), new Vector2(0, 10));

            var disciplineBox = Panel("CombatDisciplines", new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                disciplineSize + new Vector2(16f, 16f), new Vector2(0, 10 + gridSize.y + 12f));
            var disciplineRow = SlotRow(
                disciplineBox.transform,
                "DisciplineRow",
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                disciplineSize,
                Vector2.zero);

            int disciplineIndex = 0;
            foreach (ActionBarSlotBinding binding in DisciplineBarKeymap.SelectableBindings)
            {
                if (disciplineIndex >= ActionBarSlotIds.DisciplineColumns)
                    break;

                BuildSlot(
                    disciplineRow.transform,
                    binding.KeyLabel,
                    null,
                    out _disciplineBarIcons[disciplineIndex],
                    out _disciplineBarCd[disciplineIndex],
                    out _disciplineBarText[disciplineIndex],
                    out _disciplineBarChargeText[disciplineIndex],
                    out _disciplineBarTooltips[disciplineIndex],
                    out _disciplineBarClicks[disciplineIndex],
                    out _,
                    out _,
                    out _disciplineBarFlash[disciplineIndex]);
                disciplineIndex++;
            }

            var grid = SlotGrid(box.transform, "AbilityGrid",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                gridSize, Vector2.zero);

            for (int col = 0; col < ActionBarLayout.Columns; col++)
            {
                BuildSlot(
                    grid.transform,
                    SpellbookKeymap.KeyLabelForIndex(col),
                    null,
                    out _spellbookGridIcons[col],
                    out _spellbookGridCd[col],
                    out _spellbookGridText[col],
                    out _spellbookGridChargeText[col],
                    out _spellbookGridTooltips[col],
                    out _spellbookGridClicks[col],
                    out _,
                    out _,
                    out _spellbookGridFlash[col]);
            }

            for (int row = 0; row < ActionBarLayout.VisibleActionRows; row++)
            {
                for (int col = 0; col < ActionBarLayout.Columns; col++)
                {
                    int index = GridIndex(row, col);
                    string key = (col + 1).ToString();
                    BuildSlot(
                        grid.transform,
                        key,
                        null,
                        out _abilityGridIcons[index],
                        out _abilityGridCd[index],
                        out _abilityGridText[index],
                        out _abilityGridChargeText[index],
                        out _abilityGridTooltips[index],
                        out _abilityGridClicks[index],
                        out _,
                        out _,
                        out _abilityGridFlash[index]);
                }
            }
        }

        private void BuildSlot(Transform parent, string key, string? label,
            out Image iconImg,
            out Image cdImg,
            out Text cdTxt,
            out Text chargeTxt,
            out TooltipTarget tooltip,
            out ActionBarClickTarget clickTarget,
            out ActionBarDropSlot dropSlot,
            out ActionBarDragSource dragSource,
            out Image flashImg)
        {
            var slot = CreateActionBarSlot(parent, $"Slot_{key}");
            var rt = ArenaUiKit.EnsureComponent<RectTransform>(slot);
            rt.sizeDelta = ActionBarLayout.SlotVector;

            var slotImage = slot.GetComponent<Image>();
            if (slotImage == null)
            {
                Img(slot, SlotBg);
                slotImage = slot.GetComponent<Image>();
            }

            if (slotImage != null)
            {
                slotImage.color = HasPrefabFrame(slot) ? TransparentSlotInput : SlotBg;
                slotImage.raycastTarget = true;
            }

            if (!HasPrefabFrame(slot))
            {
                var ol = slot.AddComponent<Outline>();
                ol.effectColor    = SlotBorder;
                ol.effectDistance  = new Vector2(1, 1);
            }

            tooltip = slot.AddComponent<TooltipTarget>();
            tooltip.Configure(_canvas, default);
            clickTarget = slot.AddComponent<ActionBarClickTarget>();
            dropSlot = slot.AddComponent<ActionBarDropSlot>();
            dragSource = slot.AddComponent<ActionBarDragSource>();
            dragSource.Configure(_canvas, null, null);

            var iconGo = Child(slot.transform, "Icon");
            Stretch(iconGo);
            var iconRt = (RectTransform)iconGo.transform;
            iconRt.offsetMin = Vector2.one * ActionBarLayout.IconInset;
            iconRt.offsetMax = Vector2.one * -ActionBarLayout.IconInset;
            iconImg = iconGo.AddComponent<Image>();
            iconImg.color = Color.white;
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;
            iconImg.enabled = false;

            // Keybind label (top-left)
            var kt = Label(slot.transform, "Key",
                new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(48, 14), new Vector2(3, -2),
                10, new Color(0.8f, 0.8f, 0.6f), TextAnchor.UpperLeft);
            kt.text = key;

            // Slot label / abbreviation
            var lt = Label(slot.transform, "Lbl", stretch: true,
                fontSize: 10, alignment: TextAnchor.MiddleCenter);
            lt.resizeTextForBestFit = true;
            lt.resizeTextMinSize = 7;
            lt.resizeTextMaxSize = 10;
            lt.text = label ?? string.Empty;

            // CD sweep overlay
            var cdGo = Child(slot.transform, "CD");
            Stretch(cdGo);
            cdImg = cdGo.AddComponent<Image>();
            cdImg.color      = CdOverlay;
            cdImg.sprite     = DefaultFilledSprite();
            cdImg.type       = Image.Type.Filled;
            cdImg.fillMethod = Image.FillMethod.Vertical;
            cdImg.fillOrigin = (int)Image.OriginVertical.Top;
            cdImg.fillAmount = 0f;
            cdImg.raycastTarget = false;

            // Rejection denial flash (netcode design review S2) — above the
            // icon and CD sweep, below the cooldown/charge texts.
            var flashGo = Child(slot.transform, "RejectFlash");
            Stretch(flashGo);
            flashImg = flashGo.AddComponent<Image>();
            flashImg.color = new Color(RejectFlashColor.r, RejectFlashColor.g, RejectFlashColor.b, 0f);
            flashImg.raycastTarget = false;
            flashImg.enabled = false;

            // CD time text
            cdTxt = Label(slot.transform, "CdT", stretch: true,
                fontSize: 14, alignment: TextAnchor.MiddleCenter);
            cdTxt.fontStyle = FontStyle.Bold;
            cdTxt.text = "";

            chargeTxt = Label(slot.transform, "Charges",
                new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(18, 14), new Vector2(-3, 2),
                11, Color.white, TextAnchor.LowerRight);
            chargeTxt.fontStyle = FontStyle.Bold;
            chargeTxt.text = "";
        }

        private static GameObject CreateActionBarSlot(Transform parent, string name)
        {
            GameObject? prefab = Resources.Load<GameObject>(ActionBarLayout.SlotPrefabResourcePath);
            GameObject slot = prefab != null
                ? UnityEngine.Object.Instantiate(prefab, parent, false)
                : new GameObject(name);

            slot.name = name;
            if (prefab == null)
                slot.transform.SetParent(parent, false);

            return slot;
        }

        private static bool HasPrefabFrame(GameObject slot)
        {
            return slot.transform.Find("Frame") != null;
        }

        // ---------------------------------------------------------------
        // Cast Bar (above action bars)
        // ---------------------------------------------------------------

        private void BuildCastBar()
        {
            _castRoot = Panel("CastBar", new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(300, 28), new Vector2(0, 255));
            Img(_castRoot, CastBg);
            var ol = _castRoot.AddComponent<Outline>();
            ol.effectColor   = FrameBorder;
            ol.effectDistance = new Vector2(1, 1);

            _castFill = FilledBar(_castRoot.transform, "Fill", CastGold);
            _castFill.fillAmount = 0f;

            _castName = Label(_castRoot.transform, "Spell",
                new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(220, 28), new Vector2(8, 0),
                13, Color.white, TextAnchor.MiddleLeft);

            _castTime = Label(_castRoot.transform, "Time",
                new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(60, 28), new Vector2(-8, 0),
                13, Color.white, TextAnchor.MiddleRight);

            _castRoot.SetActive(false);
        }

        // ---------------------------------------------------------------
        // Respawn Timer (center, no overlay)
        // ---------------------------------------------------------------

        private void BuildRespawnTimer()
        {
            _respawnRoot = Panel("Respawn", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(400, 40), Vector2.zero);

            _respawnText = Label(_respawnRoot.transform, "Txt", stretch: true,
                fontSize: 24, alignment: TextAnchor.MiddleCenter);
            _respawnText.fontStyle = FontStyle.Bold;

            var sh = _respawnRoot.AddComponent<Shadow>();
            sh.effectColor    = Color.black;
            sh.effectDistance  = new Vector2(2, -2);

            _respawnRoot.SetActive(false);
        }

        // ===============================================================
        // UPDATE
        // ===============================================================

        private void Update()
        {
            if (ShouldSuppressInCurrentScene())
            {
                SetHudVisible(false);
                MarkHudDirty();
                return;
            }

            SetHudVisible(true);
            var local = EntityRegistry.Instance?.LocalPlayerEntity;
            if (local == null) return;

            UpdatePlayerFrame(local);
            UpdateTargetFrame();
            UpdatePartyFrames(local);
            UpdatePartyInvitePrompt(local);
            UpdateSelfBuffs(local);
            UpdateActionBars();
            UpdateCastBar();
            UpdateRespawn(local);
        }

        private void MarkHudDirty()
        {
            _lastTargetIdentity = null;
            _lastPartyId = null;
            _activeInviteId = null;
            _visiblePartyMembers.Clear();
            _nextTargetStaticRefreshTime = 0f;
            _nextTargetStatusRefreshTime = 0f;
            _nextSelfStatusRefreshTime = 0f;
            _nextPartyRosterRefreshTime = 0f;
            _nextPartyDebuffRefreshTime = 0f;
            _nextPartyInviteRefreshTime = 0f;
            _nextActionBarStaticRefreshTime = 0f;
            _actionBarStaticDirty = true;
        }

        private void SetHudVisible(bool visible)
        {
            if (_canvas.enabled != visible)
                _canvas.enabled = visible;

            if (_raycaster.enabled != visible)
                _raycaster.enabled = visible;
        }

        private static bool ShouldSuppressInCurrentScene()
        {
            string activeSceneName = SceneManager.GetActiveScene().name;
            return !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene()
                   || string.Equals(activeSceneName, "Hub", StringComparison.Ordinal)
                   || string.Equals(activeSceneName, "CharacterCreation", StringComparison.Ordinal)
                   || string.Equals(activeSceneName, "CharacterCustomization", StringComparison.Ordinal);
        }

        // --- Player Frame ---

        private void UpdatePlayerFrame(PlayerEntity p)
        {
            SetTextIfChanged(_playerName, string.IsNullOrEmpty(p.Username) ? "Player" : p.Username);
            HealthBarPresentation health = ResolveHealthBarPresentation(
                NetworkManager.Instance?.Conn,
                p.Identity,
                p.Hp,
                p.MaxHp);
            SetHealthBarFill(_playerHpFill, _playerTempHpFill, health);
            SetTextIfChanged(_playerHpText, health.Text);

            RenderPlayerResourceBar(
                _playerPrimaryFill,
                _playerPrimaryText,
                p.StaminaResourceKind,
                p.CurrentStamina,
                p.MaxStamina);
            RenderPlayerResourceBar(
                _playerManaFill,
                _playerManaText,
                p.ManaResourceKind,
                p.CurrentMana,
                p.MaxMana);

        }

        private void OnPlayerFrameSelected()
        {
            var local = EntityRegistry.Instance?.LocalPlayerEntity;
            if (local == null)
                return;

            TargetSelector.Instance?.SelectTarget(local);
        }

        // --- Target Frame ---

        private void UpdateTargetFrame()
        {
            var target = TargetSelector.Instance?.SelectedTarget;
            if (target == null || !target.IsAlive)
            {
                SetActiveIfChanged(_targetRoot, false);
                _lastTargetIdentity = null;
                return;
            }

            SetActiveIfChanged(_targetRoot, true);
            bool targetChanged = !_lastTargetIdentity.HasValue || _lastTargetIdentity.Value != target.TargetIdentity;
            if (targetChanged || Time.unscaledTime >= _nextTargetStaticRefreshTime)
            {
                _lastTargetIdentity = target.TargetIdentity;
                _nextTargetStaticRefreshTime = Time.unscaledTime + PartyRosterRefreshInterval;
                RefreshTargetStatic(target);
                if (targetChanged)
                    _nextTargetStatusRefreshTime = 0f;
            }

            HealthBarPresentation health = ResolveHealthBarPresentation(
                NetworkManager.Instance?.Conn,
                target.TargetIdentity,
                target.Hp,
                target.MaxHp);
            SetHealthBarFill(_targetHpFill, _targetTempHpFill, health);
            SetTextIfChanged(_targetHpText, health.Text);

            PlayerEntity? targetPlayer = target as PlayerEntity;
            var primaryPresentation = targetPlayer != null
                ? ResolvePrimaryResourcePresentation(targetPlayer)
                : (isVisible: false, label: string.Empty, color: ResourceBlue);
            if (targetPlayer != null && primaryPresentation.isVisible)
            {
                float primaryFrac = targetPlayer.MaxPrimaryResource > 0.001f
                    ? Mathf.Clamp01(targetPlayer.CurrentPrimaryResource / targetPlayer.MaxPrimaryResource)
                    : 0f;
                SetColorIfChanged(_targetPrimaryFill, primaryPresentation.color);
                SetUnitFrameBarFill(_targetPrimaryFill, primaryFrac);
                SetTextIfChanged(
                    _targetPrimaryText,
                    $"{primaryPresentation.label} {Mathf.RoundToInt(targetPlayer.CurrentPrimaryResource)} / {Mathf.RoundToInt(targetPlayer.MaxPrimaryResource)}");
            }
            else
            {
                SetColorIfChanged(_targetPrimaryFill, ResourceBlue);
                SetUnitFrameBarFill(_targetPrimaryFill, 0f);
                SetTextIfChanged(_targetPrimaryText, "");
            }

            if (Time.unscaledTime >= _nextTargetStatusRefreshTime)
            {
                _nextTargetStatusRefreshTime = Time.unscaledTime + StatusIconRefreshInterval;
                RefreshStatusIcons(target.TargetIdentity, _tgtBuffRow, _tgtBuffIcons, _tgtDebuffRow, _tgtDebuffIcons);
            }
        }

        private void RefreshTargetStatic(ICombatTargetEntity target)
        {
            var relation = PartyRelationship.RelationToLocal(target);
            SetTextIfChanged(_targetName, string.IsNullOrEmpty(target.DisplayName) ? "???" : target.DisplayName);
            SetColorIfChanged(_targetName, RelationColor(relation));
            SetTextIfChanged(_targetRelation, RelationLabel(relation));
            SetColorIfChanged(_targetRelation, RelationColor(relation));
            SetActiveIfChanged(
                _targetInviteButton.gameObject,
                target is PlayerEntity playerTarget && PartyRelationship.CanInvite(playerTarget));
        }

        private void UpdatePartyFrames(PlayerEntity local)
        {
            var conn = NetworkManager.Instance?.Conn;
            ulong? partyId = PartyRelationship.LocalPartyId();
            if (conn == null || !partyId.HasValue)
            {
                SetActiveIfChanged(_partyRoot, false);
                _lastPartyId = null;
                _visiblePartyMembers.Clear();
                return;
            }

            bool partyChanged = !_lastPartyId.HasValue || _lastPartyId.Value != partyId.Value;
            if (partyChanged || Time.unscaledTime >= _nextPartyRosterRefreshTime)
            {
                _lastPartyId = partyId.Value;
                _nextPartyRosterRefreshTime = Time.unscaledTime + PartyRosterRefreshInterval;
                RefreshPartyRoster(conn, local.Identity, partyId.Value);
                _nextPartyDebuffRefreshTime = 0f;
            }

            SetActiveIfChanged(_partyRoot, true);
            SetActiveIfChanged(_partyLeaveButton.gameObject, true);
            bool refreshDebuffs = Time.unscaledTime >= _nextPartyDebuffRefreshTime;
            if (refreshDebuffs)
                _nextPartyDebuffRefreshTime = Time.unscaledTime + StatusIconRefreshInterval;

            for (int i = 0; i < _partyFrames.Count; i++)
            {
                if (i >= _visiblePartyMembers.Count)
                {
                    SetActiveIfChanged(_partyFrames[i].Root, false);
                    continue;
                }

                SpacetimeDB.Identity member = _visiblePartyMembers[i];
                PlayerEntity? entity = null;
                if (EntityRegistry.Instance != null && EntityRegistry.Instance.TryGetEntity(member, out var memberEntity))
                    entity = memberEntity;
                string name = entity != null && !string.IsNullOrEmpty(entity.Username)
                    ? entity.Username
                    : ShortIdentity(member);
                HealthBarPresentation health = entity != null
                    ? ResolveHealthBarPresentation(conn, entity.Identity, entity.Hp, entity.MaxHp)
                    : HealthBarPresentation.Empty;
                _partyFrames[i].Set(member, name, health, health.Text);
                if (refreshDebuffs)
                    RefreshDebuffIcons(member, _partyFrames[i].DebuffRow, _partyFrames[i].DebuffIcons);
            }
        }

        private void OnPartyFrameSelected(SpacetimeDB.Identity member)
        {
            var registry = EntityRegistry.Instance;
            if (registry == null || !registry.TryGetEntity(member, out var entity))
                return;

            TargetSelector.Instance?.SelectTarget(entity);
        }

        private void RefreshPartyRoster(DbConnection conn, SpacetimeDB.Identity localIdentity, ulong partyId)
        {
            _tmpPartyMembers.Clear();
            foreach (PartyMember member in conn.Db.PartyMember.PartyId.Filter(partyId))
            {
                if (member.Member != localIdentity)
                    _tmpPartyMembers.Add(member);
            }

            _tmpPartyMembers.Sort((left, right) =>
                left.JoinedAt.MicrosecondsSinceUnixEpoch.CompareTo(right.JoinedAt.MicrosecondsSinceUnixEpoch));

            _visiblePartyMembers.Clear();
            int count = Mathf.Min(_tmpPartyMembers.Count, _partyFrames.Count);
            for (int i = 0; i < count; i++)
                _visiblePartyMembers.Add(_tmpPartyMembers[i].Member);
        }

        private void UpdatePartyInvitePrompt(PlayerEntity local)
        {
            if (Time.unscaledTime < _nextPartyInviteRefreshTime)
                return;

            _nextPartyInviteRefreshTime = Time.unscaledTime + PartyInviteRefreshInterval;
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                _activeInviteId = null;
                SetActiveIfChanged(_partyInviteRoot, false);
                return;
            }

            long nowMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            PartyInvite? invite = null;
            foreach (PartyInvite row in conn.Db.PartyInvite.Invitee.Filter(local.Identity))
            {
                if (row.ExpiresAt.MicrosecondsSinceUnixEpoch <= nowMicros)
                    continue;

                if (invite == null
                    || row.CreatedAt.MicrosecondsSinceUnixEpoch < invite.CreatedAt.MicrosecondsSinceUnixEpoch)
                    invite = row;
            }

            if (invite == null)
            {
                _activeInviteId = null;
                SetActiveIfChanged(_partyInviteRoot, false);
                return;
            }

            _activeInviteId = invite.InviteId;
            string inviter = ShortIdentity(invite.Inviter);
            if (EntityRegistry.Instance != null && EntityRegistry.Instance.TryGetEntity(invite.Inviter, out var inviterEntity))
                inviter = string.IsNullOrEmpty(inviterEntity.Username) ? inviter : inviterEntity.Username;
            SetTextIfChanged(_partyInviteText, $"{inviter} invited you to a party");
            SetActiveIfChanged(_partyInviteRoot, true);
        }

        private void OnInviteSelectedTarget()
        {
            var target = TargetSelector.Instance?.SelectedTarget;
            var conn = NetworkManager.Instance?.Conn;
            if (target is not PlayerEntity playerTarget || conn == null || !PartyRelationship.CanInvite(playerTarget))
                return;

            conn.Reducers.InviteToParty(playerTarget.Identity.ToString());
        }

        private void OnAcceptActiveInvite()
        {
            if (!_activeInviteId.HasValue)
                return;

            NetworkManager.Instance?.Conn?.Reducers.AcceptPartyInvite(_activeInviteId.Value);
            _activeInviteId = null;
            _partyInviteRoot.SetActive(false);
        }

        private void OnDeclineActiveInvite()
        {
            if (!_activeInviteId.HasValue)
                return;

            NetworkManager.Instance?.Conn?.Reducers.DeclinePartyInvite(_activeInviteId.Value);
            _activeInviteId = null;
            _partyInviteRoot.SetActive(false);
        }

        private static string ShortIdentity(SpacetimeDB.Identity identity)
        {
            string value = identity.ToString();
            return value.Length <= 8 ? value : value.Substring(0, 8);
        }

        private static string RelationLabel(ClientCombatRelation relation)
            => relation switch
            {
                ClientCombatRelation.Self => "SELF",
                ClientCombatRelation.PartyAlly => "PARTY",
                ClientCombatRelation.Neutral => "NEUTRAL",
                ClientCombatRelation.Hostile => "HOSTILE",
                _ => "NEUTRAL",
            };

        private static Color RelationColor(ClientCombatRelation relation)
            => relation switch
            {
                ClientCombatRelation.Self => RelationSelfColor,
                ClientCombatRelation.PartyAlly => RelationPartyColor,
                ClientCombatRelation.Neutral => RelationNeutralColor,
                ClientCombatRelation.Hostile => RelationHostileColor,
                _ => RelationNeutralColor,
            };

        private static HealthBarPresentation ResolveHealthBarPresentation(
            DbConnection? conn,
            SpacetimeDB.Identity owner,
            int hp,
            int maxHp)
        {
            int temporaryHitpoints = ResolveTemporaryHitpoints(conn, owner);
            float displayedCapacity = Mathf.Max(1f, Mathf.Max(hp, maxHp) + temporaryHitpoints);
            float healthFraction = Mathf.Clamp01(Mathf.Max(0, hp) / displayedCapacity);
            float protectedFraction = Mathf.Clamp01((Mathf.Max(0, hp) + temporaryHitpoints) / displayedCapacity);
            string text = temporaryHitpoints > 0
                ? $"{hp} / {maxHp} +{temporaryHitpoints}"
                : $"{hp} / {maxHp}";

            return new HealthBarPresentation(
                healthFraction,
                protectedFraction,
                temporaryHitpoints,
                text);
        }

        private static int ResolveTemporaryHitpoints(DbConnection? conn, SpacetimeDB.Identity owner)
        {
            if (conn == null)
                return 0;

            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            long total = 0;
            foreach (StatusEffect status in conn.Db.StatusEffect.Target.Filter(owner))
            {
                if (!IsTemporaryHitpointsStatus(status))
                    continue;
                if (status.ExpiresAtMicros > 0 && status.ExpiresAtMicros < nowUs)
                    continue;
                if (status.AbsorbAmount <= 0)
                    continue;

                total += status.AbsorbAmount;
                if (total >= int.MaxValue)
                    return int.MaxValue;
            }

            return (int)total;
        }

        private static bool IsTemporaryHitpointsStatus(StatusEffect status) =>
            string.Equals(
                WireIdentifier.Normalize(status.EffectKind),
                TemporaryHitpointsStatusKind,
                StringComparison.Ordinal);

        private static void SetHealthBarFill(
            Image healthFill,
            Image temporaryHitpointsFill,
            HealthBarPresentation health)
        {
            SetUnitFrameBarFill(temporaryHitpointsFill, health.ProtectedFraction);
            SetUnitFrameBarFill(healthFill, health.HealthFraction);
            SetActiveIfChanged(
                temporaryHitpointsFill.gameObject,
                health.TemporaryHitpoints > 0 && health.ProtectedFraction > health.HealthFraction + 0.0005f);
        }

        // --- Self Buffs ---

        private void UpdateSelfBuffs(PlayerEntity local)
        {
            if (Time.unscaledTime < _nextSelfStatusRefreshTime)
                return;

            _nextSelfStatusRefreshTime = Time.unscaledTime + StatusIconRefreshInterval;
            RefreshStatusIcons(local.Identity, _selfBuffRow, _selfBuffIcons, _selfDebuffRow, _selfDebuffIcons);
            UpdateSelfCombatModeIcon(local.Identity);
        }

        private void RefreshStatusIcons(SpacetimeDB.Identity id,
            Transform buffRow, List<GameObject> buffIcons,
            Transform debuffRow, List<GameObject> debuffIcons)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null) return;

            _tmpBuff.Clear();
            _tmpDebuff.Clear();

            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            foreach (var se in conn.Db.StatusEffect.Target.Filter(id))
            {
                if (se.ExpiresAtMicros > 0 && se.ExpiresAtMicros < nowUs) continue;

                if (IsBuffPolarity(se.Polarity))
                    _tmpBuff.Add(se);
                else
                    _tmpDebuff.Add(se);
            }

            SyncRow(buffRow, buffIcons, _tmpBuff, true);
            SyncRow(debuffRow, debuffIcons, _tmpDebuff, false);
        }

        private void RefreshDebuffIcons(SpacetimeDB.Identity id, Transform debuffRow, List<GameObject> debuffIcons)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null) return;

            _tmpDebuff.Clear();

            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            foreach (var se in conn.Db.StatusEffect.Target.Filter(id))
            {
                if (se.ExpiresAtMicros > 0 && se.ExpiresAtMicros < nowUs) continue;
                if (!IsBuffPolarity(se.Polarity))
                    _tmpDebuff.Add(se);
            }

            SyncRow(debuffRow, debuffIcons, _tmpDebuff, false);
        }

        private void SyncRow(Transform row, List<GameObject> pool,
            List<StatusEffect> effects, bool isBuff)
        {
            while (pool.Count < effects.Count)
                pool.Add(MakeIcon(row, isBuff));

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            for (int i = 0; i < pool.Count; i++)
            {
                if (i >= effects.Count) { pool[i].SetActive(false); continue; }

                pool[i].SetActive(true);
                var se = effects[i];
                if (_statusIconViews.TryGetValue(pool[i], out StatusIconView view))
                {
                    SetTextIfChanged(view.Label, se.EffectKind.Length >= 2
                        ? se.EffectKind[..2].ToUpper()
                        : se.EffectKind.ToUpper());
                    SetTextIfChanged(view.Stack, se.Stacks > 1 ? se.Stacks.ToString() : "");
                    long expMs = se.ExpiresAtMicros / 1000L;
                    float left = Mathf.Max(0f, (expMs - nowMs) / 1000f);
                    SetTextIfChanged(view.Time, left > 1f ? $"{left:F0}" : left > 0.1f ? $"{left:F1}" : "");
                    string tooltipKey = $"{se.StatusId}:{se.EffectKind}:{se.Stacks}:{se.AbsorbAmount}:{se.ExpiresAtMicros}";
                    if (!string.Equals(view.TooltipKey, tooltipKey, StringComparison.Ordinal))
                    {
                        view.TooltipKey = tooltipKey;
                        view.Tooltip.Configure(_canvas, StatusTooltipResolver.Resolve(NetworkManager.Instance?.Conn, se), pollHover: true);
                    }
                }
            }
        }

        private static bool IsBuffPolarity(string polarity) =>
            string.Equals(WireIdentifier.Normalize(polarity), "BUFF", StringComparison.Ordinal)
            || string.Equals(polarity, "positive", StringComparison.OrdinalIgnoreCase);

        private GameObject MakeIcon(Transform parent, bool isBuff)
        {
            var go = new GameObject("Icon");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(IconSz, IconSz);

            Img(go, isBuff ? BuffBg : DebuffBg);
            var image = go.GetComponent<Image>();
            if (image != null)
                image.raycastTarget = true;
            var ol = go.AddComponent<Outline>();
            ol.effectColor   = isBuff ? new Color(0.3f, 0.6f, 0.3f) : new Color(0.6f, 0.3f, 0.3f);
            ol.effectDistance = new Vector2(1, 1);

            Text label = Label(go.transform, "Lbl", stretch: true,
                fontSize: 10, alignment: TextAnchor.MiddleCenter);

            var stack = Label(go.transform, "Stk",
                new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(14, 12), new Vector2(-1, 1),
                9, Color.white, TextAnchor.LowerRight);
            stack.text = "";

            var time = Label(go.transform, "Tm",
                new Vector2(0.5f, 0), new Vector2(0.5f, 1),
                new Vector2(IconSz, 12), Vector2.zero,
                8, Color.white, TextAnchor.UpperCenter);
            time.text = "";

            var tooltip = go.AddComponent<TooltipTarget>();
            tooltip.Configure(_canvas, default, pollHover: true);
            _statusIconViews[go] = new StatusIconView(label, stack, time, tooltip);

            return go;
        }

        private void UpdateSelfCombatModeIcon(SpacetimeDB.Identity owner)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                SetSelfCombatModeIconVisible(false);
                return;
            }

            string combatProfile = CombatProfileResolver.ResolveForOwner(conn, owner);
            if (!TryResolveCurrentCombatMode(conn, owner, combatProfile, out CombatModeCatalog mode))
            {
                SetSelfCombatModeIconVisible(false);
                return;
            }

            GameObject icon = EnsureSelfCombatModeIcon();
            icon.SetActive(true);
            icon.transform.SetAsLastSibling();

            string modeId = WireIdentifier.Normalize(mode.ModeId);
            var lbl = icon.transform.Find("Lbl")?.GetComponent<Text>();
            if (lbl != null)
                SetTextIfChanged(lbl, CombatModeShelfLabel(modeId));

            var stk = icon.transform.Find("Stk")?.GetComponent<Text>();
            if (stk != null)
                SetTextIfChanged(stk, "");

            var tm = icon.transform.Find("Tm")?.GetComponent<Text>();
            if (tm != null)
                SetTextIfChanged(tm, "DRAW");

            var img = icon.GetComponent<Image>();
            if (img != null)
                SetColorIfChanged(img, CombatModeShelfColor(modeId));
        }

        private void SetSelfCombatModeIconVisible(bool visible)
        {
            if (_selfCombatModeIcon != null)
                _selfCombatModeIcon.SetActive(visible);
        }

        private GameObject EnsureSelfCombatModeIcon()
        {
            if (_selfCombatModeIcon != null)
                return _selfCombatModeIcon;

            _selfCombatModeIcon = MakeIcon(_selfBuffRow, true);
            _selfCombatModeIcon.name = "CombatModeIcon";
            return _selfCombatModeIcon;
        }

        private static bool TryResolveCurrentCombatMode(
            DbConnection conn,
            SpacetimeDB.Identity owner,
            string combatProfile,
            out CombatModeCatalog mode)
        {
            mode = null!;
            string normalizedProfile = CombatProfileIds.Normalize(combatProfile);
            if (string.IsNullOrWhiteSpace(normalizedProfile))
                return false;

            var modes = new List<CombatModeCatalog>();
            foreach (CombatModeCatalog row in conn.Db.CombatModeCatalog.CombatProfileId.Filter(normalizedProfile))
                modes.Add(row);

            if (modes.Count == 0)
                return false;

            ActiveCombatMode? active = conn.Db.ActiveCombatMode.Owner.Find(owner);
            if (active != null
                && string.Equals(WireIdentifier.Normalize(active.CombatProfileId), normalizedProfile, StringComparison.Ordinal))
            {
                string activeMode = WireIdentifier.Normalize(active.ModeId);
                for (int i = 0; i < modes.Count; i++)
                {
                    if (string.Equals(WireIdentifier.Normalize(modes[i].ModeId), activeMode, StringComparison.Ordinal))
                    {
                        mode = modes[i];
                        return true;
                    }
                }
            }

            modes.Sort((left, right) =>
            {
                int sort = left.SortOrder.CompareTo(right.SortOrder);
                return sort != 0
                    ? sort
                    : string.Compare(
                        WireIdentifier.Normalize(left.ModeId),
                        WireIdentifier.Normalize(right.ModeId),
                        StringComparison.Ordinal);
            });

            mode = modes.Find(row => row.IsDefault) ?? modes[0];
            return true;
        }

        private static string CombatModeShelfLabel(string modeId)
        {
            return modeId switch
            {
                CombatModeIds.ShortDraw => "SD",
                CombatModeIds.FullDraw => "FD",
                CombatModeIds.Ready => "RD",
                CombatModeIds.Stealthed => "ST",
                _ => modeId.Length >= 2 ? modeId[..2] : modeId,
            };
        }

        private static Color CombatModeShelfColor(string modeId)
        {
            return modeId switch
            {
                CombatModeIds.ShortDraw => Hex("#2C5F72"),
                CombatModeIds.FullDraw => Hex("#72542C"),
                CombatModeIds.Ready => Hex("#3F4650"),
                CombatModeIds.Stealthed => Hex("#2F5C46"),
                _ => BuffBg,
            };
        }

        // --- Action Bars ---

        private void UpdateActionBars()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var combat = LocalCombatState.Instance;
            float gcdFrac = combat.GlobalCooldownFractionRemaining(nowMs);

            if (_actionBarStaticDirty || Time.unscaledTime >= _nextActionBarStaticRefreshTime)
            {
                _nextActionBarStaticRefreshTime = Time.unscaledTime + ActionBarStaticRefreshInterval;
                _actionBarStaticDirty = false;
                RefreshActionBarStaticState();
            }

            UpdateActionBarCooldownPresentation(nowMs, gcdFrac, combat);
            UpdateSlotRejectionFlashPresentation(Time.unscaledTimeAsDouble);
        }

        private void OnEnable()
        {
            LocalCombatState.PredictionRejected += OnPredictionRejected;
        }

        private void OnDisable()
        {
            LocalCombatState.PredictionRejected -= OnPredictionRejected;
        }

        // Rejection denial flash (netcode design review S2): flash every slot
        // whose resolved action is the rejected press. Matched on the pressed
        // id first (the bar shows the opener even when a combo follow-up was
        // resolved), falling back to the resolved action kind.
        private void OnPredictionRejected(string actionKind, string pressedActionId, ActionRejectReason reason)
        {
            _ = reason;
            double until = Time.unscaledTimeAsDouble + SlotRejectionFlashSeconds;
            if (!StampSlotRejectionFlash(pressedActionId, until))
                StampSlotRejectionFlash(actionKind, until);
        }

        private bool StampSlotRejectionFlash(string actionId, double until)
        {
            if (string.IsNullOrWhiteSpace(actionId))
                return false;

            bool any = StampSlotRejectionFlash(_abilityGridStates, _abilityGridFlashUntil, actionId, until);
            any |= StampSlotRejectionFlash(_spellbookGridStates, _spellbookGridFlashUntil, actionId, until);
            any |= StampSlotRejectionFlash(_disciplineBarStates, _disciplineBarFlashUntil, actionId, until);
            if (any)
                _anySlotFlashActiveUntil = Math.Max(_anySlotFlashActiveUntil, until);
            return any;
        }

        private static bool StampSlotRejectionFlash(
            ActionBarSlotState[] states,
            double[] flashUntil,
            string actionId,
            double until)
        {
            bool any = false;
            for (int index = 0; index < states.Length; index++)
            {
                ActionBarSlotState? state = states[index];
                if (state == null || !state.IsVisible)
                    continue;
                if (!string.Equals(state.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
                    continue;

                flashUntil[index] = until;
                any = true;
            }

            return any;
        }

        private void UpdateSlotRejectionFlashPresentation(double now)
        {
            // Trailing slack so expired flashes get their final alpha-0 write.
            if (now > _anySlotFlashActiveUntil + 0.1)
                return;

            RenderSlotRejectionFlash(_abilityGridFlash, _abilityGridFlashUntil, now);
            RenderSlotRejectionFlash(_spellbookGridFlash, _spellbookGridFlashUntil, now);
            RenderSlotRejectionFlash(_disciplineBarFlash, _disciplineBarFlashUntil, now);
        }

        private static void RenderSlotRejectionFlash(Image[] flashes, double[] flashUntil, double now)
        {
            for (int index = 0; index < flashes.Length; index++)
            {
                Image flash = flashes[index];
                if (flash == null)
                    continue;

                float remaining = (float)(flashUntil[index] - now);
                float alpha = remaining <= 0f
                    ? 0f
                    : RejectFlashColor.a * Mathf.Clamp01(remaining / SlotRejectionFlashSeconds);
                Color color = RejectFlashColor;
                color.a = alpha;
                SetColorIfChanged(flash, color);
                bool visible = alpha > 0f;
                if (flash.enabled != visible)
                    flash.enabled = visible;
            }
        }

        private void RefreshActionBarStaticState()
        {
            var conn = NetworkManager.Instance?.Conn;
            var owner = conn?.Identity;

            RefreshSpellbookStaticState(conn, owner);
            RefreshDisciplineBarStaticState(conn, owner);

            for (int row = 0; row < ActionBarLayout.VisibleActionRows; row++)
            {
                for (int col = 0; col < ActionBarLayout.Columns; col++)
                {
                    int index = GridIndex(row, col);
                    _abilityGridStates[index] = ActionBarSlotState.Empty(KeyLabelForCell(row, col));
                    SetActionBarSlotPresentation(_abilityGridCd[index], _abilityGridIcons[index], _abilityGridStates[index], SlotBg, null);
                    _abilityGridTooltips[index].Configure(_canvas, default);
                    _abilityGridClicks[index].Configure(null);
                    SetFillIfChanged(_abilityGridCd[index], 0f);
                    SetColorIfChanged(_abilityGridCd[index], CdOverlay);
                    SetTextIfChanged(_abilityGridText[index], string.Empty);
                    SetTextIfChanged(_abilityGridChargeText[index], string.Empty);
                }
            }

            foreach (ActionBarSlotBinding binding in ActionBarKeymap.SelectableBindings)
            {
                if (binding.Row >= ActionBarLayout.VisibleActionRows)
                    continue;

                ActiveActionBarAction resolved = ActiveActionBarResolver.ResolveActiveSelectableAction(
                    conn,
                    owner,
                    binding.SlotId);

                bool isVisible = resolved.HasAssignedAction;
                string label = isVisible ? resolved.DisplayName : string.Empty;
                Sprite? iconSprite = isVisible ? ActionIconResolver.ResolveForAction(resolved) : null;
                Color slotColor = resolved.IsFixed
                    ? (FixedActionDispatcher.IsEnabled(resolved.ActionId, conn)
                        ? FixedActionColor(resolved.ActionId)
                        : DisabledFixedActionColor(resolved.ActionId))
                    : ResolveActionBarColor(conn, resolved.AbilityId, isVisible);
                int index = GridIndex(binding.Row, binding.Col);
                bool usesHoldInput = resolved.IsFixed
                    && string.Equals(
                        WireIdentifier.Normalize(resolved.ActionId),
                        FixedActionIds.Parry,
                        StringComparison.Ordinal);
                _abilityGridStates[index] = new ActionBarSlotState(
                    binding.KeyLabel,
                    iconSprite == null ? label : string.Empty,
                    isVisible,
                    resolved.IsFixed,
                    resolved.ActionId,
                    !resolved.IsCombatModeToggleAbility && UsesGlobalCooldown(resolved.ActionId, string.Empty),
                    ResolveRequiresTargetLos(conn, resolved));
                SetActionBarSlotPresentation(_abilityGridCd[index], _abilityGridIcons[index], _abilityGridStates[index], slotColor, iconSprite);
                _abilityGridClicks[index].Configure(
                    isVisible ? () => TriggerActionRef(conn, resolved) : null,
                    isVisible && usesHoldInput ? () => ReleaseActionRef(conn, resolved) : null,
                    usesHoldInput);
                _abilityGridTooltips[index].Configure(
                    _canvas,
                    ActionTooltipResolver.ResolveForActionRef(conn, owner, resolved),
                    pollHover: true);
            }
        }

        private void RefreshDisciplineBarStaticState(DbConnection? conn, SpacetimeDB.Identity? owner)
        {
            for (int i = 0; i < ActionBarSlotIds.DisciplineColumns; i++)
            {
                string keyLabel = i < DisciplineBarKeymap.SelectableBindings.Count
                    ? DisciplineBarKeymap.SelectableBindings[i].KeyLabel
                    : string.Empty;
                _disciplineBarStates[i] = ActionBarSlotState.Empty(keyLabel);
                SetActionBarSlotPresentation(_disciplineBarCd[i], _disciplineBarIcons[i], _disciplineBarStates[i], SlotBg, null);
                _disciplineBarTooltips[i].Configure(_canvas, default);
                _disciplineBarClicks[i].Configure(null);
                SetFillIfChanged(_disciplineBarCd[i], 0f);
                SetColorIfChanged(_disciplineBarCd[i], CdOverlay);
                SetTextIfChanged(_disciplineBarText[i], string.Empty);
                SetTextIfChanged(_disciplineBarChargeText[i], string.Empty);
            }

            if (conn == null || !owner.HasValue)
                return;

            int index = 0;
            foreach (ActionBarSlotBinding binding in DisciplineBarKeymap.SelectableBindings)
            {
                if (index >= ActionBarSlotIds.DisciplineColumns)
                    break;

                ActiveActionBarAction resolved = ActiveActionBarResolver.ResolveGlobalActionBarAction(
                    conn,
                    owner,
                    binding.SlotId);
                bool isVisible = resolved.HasAssignedAction;
                string label = isVisible ? resolved.DisplayName : string.Empty;
                Sprite? iconSprite = isVisible ? ActionIconResolver.ResolveForAction(resolved) : null;
                Color slotColor = !isVisible
                    ? SlotBg
                    : resolved.IsAvailable
                        ? ResolveResourceActionBarColor(conn, resolved.ResourceKind)
                        : DisabledSlotBg;

                _disciplineBarStates[index] = new ActionBarSlotState(
                    binding.KeyLabel,
                    iconSprite == null ? label : string.Empty,
                    isVisible,
                    false,
                    resolved.ActionId,
                    false,
                    ResolveRequiresTargetLos(conn, resolved));
                SetActionBarSlotPresentation(
                    _disciplineBarCd[index],
                    _disciplineBarIcons[index],
                    _disciplineBarStates[index],
                    slotColor,
                    iconSprite);
                _disciplineBarClicks[index].Configure(
                    resolved.CanTrigger ? () => TriggerActionRef(conn, resolved) : null);
                _disciplineBarTooltips[index].Configure(
                    _canvas,
                    ActionTooltipResolver.ResolveForActionRef(conn, owner, resolved),
                    pollHover: true);
                index++;
            }
        }

        private void RefreshSpellbookStaticState(DbConnection? conn, SpacetimeDB.Identity? owner)
        {
            List<ItemSpell> spells = ReadEquippedSpellbookSpells(conn, owner);

            for (int col = 0; col < ActionBarLayout.Columns; col++)
            {
                string keyLabel = SpellbookKeymap.KeyLabelForIndex(col);
                _spellbookGridStates[col] = ActionBarSlotState.Empty(keyLabel);
                SetActionBarSlotPresentation(_spellbookGridCd[col], _spellbookGridIcons[col], _spellbookGridStates[col], SlotBg, null);
                _spellbookGridTooltips[col].Configure(_canvas, default);
                _spellbookGridClicks[col].Configure(null);
                SetFillIfChanged(_spellbookGridCd[col], 0f);
                SetColorIfChanged(_spellbookGridCd[col], CdOverlay);
                SetTextIfChanged(_spellbookGridText[col], string.Empty);
                SetTextIfChanged(_spellbookGridChargeText[col], string.Empty);
            }

            if (conn == null || !owner.HasValue)
                return;

            for (int col = 0; col < ActionBarLayout.Columns; col++)
            {
                ItemSpell? itemSpell = FindSpellbookSlot(spells, col);
                if (itemSpell == null)
                    continue;

                string spellId = WireIdentifier.Normalize(itemSpell.SpellId);
                if (string.IsNullOrWhiteSpace(spellId))
                    continue;

                AbilityCatalog? ability = FindAbilityForSpell(conn, spellId);
                string displayName = ActionPresentation.ResolveDisplayName(conn, owner.Value, spellId, spellId);
                Sprite? iconSprite = ability == null
                    ? ActionIconResolver.Resolve(ActionKinds.Ability, spellId)
                    : ActionIconResolver.Resolve(ActionKinds.Ability, ability.AbilityId);
                ActiveActionBarAction resolved = ActiveActionBarResolver.ResolveEquippedSpellbookAction(conn, owner.Value, (uint)col);
                bool canTrigger = resolved.HasAssignedAction;
                string keyLabel = SpellbookKeymap.KeyLabelForIndex(col);

                _spellbookGridStates[col] = new ActionBarSlotState(
                    keyLabel,
                    iconSprite == null ? displayName : string.Empty,
                    true,
                    false,
                    spellId,
                    UsesGlobalCooldown(spellId, string.Empty),
                    ResolveSpellRequiresTargetLos(conn, spellId));
                SetActionBarSlotPresentation(
                    _spellbookGridCd[col],
                    _spellbookGridIcons[col],
                    _spellbookGridStates[col],
                    ResolveActionBarColor(conn, ability?.AbilityId ?? spellId, true),
                    iconSprite);
                _spellbookGridClicks[col].Configure(canTrigger ? () => TriggerActionRef(conn, resolved) : null);
                _spellbookGridTooltips[col].Configure(
                    _canvas,
                    ability == null
                        ? new TooltipData(displayName, "Spellbook", SpellbookSpellMetadata(conn, spellId))
                        : ActionTooltipResolver.ResolveForAbility(conn, owner, ability),
                    pollHover: true);
            }
        }

        // Advisory LOS gray-out participation (netcode design review S4): a
        // slot dims when its action is a target-requiring action whose
        // authored requires_target_los is set — melee abilities via the melee
        // catalog row, spells via the spell definition. Fixed actions and
        // non-targeted sweeps never dim.
        private static bool ResolveRequiresTargetLos(DbConnection? conn, ActiveActionBarAction resolved)
        {
            if (conn == null || !resolved.HasAssignedAction || resolved.IsFixed)
                return false;

            if (!string.IsNullOrWhiteSpace(resolved.AbilityId))
            {
                MeleeAbilityCatalog? melee = conn.Db.MeleeAbilityCatalog.AbilityId.Find(resolved.AbilityId);
                if (melee != null)
                    return melee.RequiresTarget && melee.RequiresTargetLos;
            }

            return ResolveSpellRequiresTargetLos(conn, resolved.ActionId);
        }

        private static bool ResolveSpellRequiresTargetLos(DbConnection? conn, string spellId)
        {
            SpellDefinition? spell = conn?.Db.SpellDefinition.Kind.Find(spellId);
            return spell is { RequiresTarget: true, RequiresTargetLos: true };
        }

        private static List<ItemSpell> ReadEquippedSpellbookSpells(DbConnection? conn, SpacetimeDB.Identity? owner)
        {
            List<ItemSpell> spells = new();
            if (conn == null || !owner.HasValue)
                return spells;

            EquipmentLoadout? loadout = conn.Db.EquipmentLoadout.Owner.Find(owner.Value);
            if (loadout == null || string.IsNullOrWhiteSpace(loadout.SpellbookItemId))
                return spells;

            foreach (ItemSpell spell in conn.Db.ItemSpell.ItemInstanceId.Filter(loadout.SpellbookItemId))
                spells.Add(spell);

            spells.Sort((left, right) =>
            {
                int sort = left.SlotIndex.CompareTo(right.SlotIndex);
                return sort != 0
                    ? sort
                    : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });
            return spells;
        }

        private static ItemSpell? FindSpellbookSlot(List<ItemSpell> spells, int slotIndex)
        {
            foreach (ItemSpell spell in spells)
            {
                if (spell.SlotIndex == (uint)slotIndex)
                    return spell;
            }

            return null;
        }

        private static AbilityCatalog? FindAbilityForSpell(DbConnection? conn, string spellId)
        {
            if (conn == null)
                return null;

            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            foreach (AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
            {
                if (!string.Equals(WireIdentifier.Normalize(ability.AbilityKind), AbilityKinds.Spell, StringComparison.Ordinal))
                    continue;
                if (string.Equals(WireIdentifier.Normalize(ability.ActionId), normalizedSpellId, StringComparison.Ordinal))
                    return ability;
            }

            return null;
        }

        private static string SpellbookSpellMetadata(DbConnection? conn, string spellId)
        {
            SpellDefinition? spell = conn?.Db.SpellDefinition.Kind.Find(WireIdentifier.Normalize(spellId));
            if (spell == null)
                return string.Empty;

            AbilityCatalog? ability = FindAbilityForSpell(conn, spellId);
            List<string> parts = new();
            if (spell.PrimaryResourceCost > 0.0001f)
                parts.Add(FormatSpellResourceCost(conn, ability, spell));
            if (spell.CooldownMs > 0)
                parts.Add($"{spell.CooldownMs / 1000f:0.#}s");
            if (!string.IsNullOrWhiteSpace(spell.Targeting))
                parts.Add(WireIdentifier.Normalize(spell.Targeting));

            return string.Join(" - ", parts);
        }

        private static string FormatSpellResourceCost(DbConnection? conn, AbilityCatalog? ability, SpellDefinition spell)
        {
            string resource = ResolveResourceDisplayName(conn, ability?.ResourceKind);
            string suffix = SpellDefinitionContracts.UsesPerSecondResourceCost(spell)
                ? "/s"
                : string.Empty;
            return $"{spell.PrimaryResourceCost:0.#} {resource}{suffix}";
        }

        private static string ResolveResourceDisplayName(DbConnection? conn, string? resourceKind)
        {
            string normalized = WireIdentifier.Normalize(resourceKind);
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = "MANA";

            ResourceCatalog? resource = conn?.Db.ResourceCatalog.ResourceKind.Find(normalized);
            return string.IsNullOrWhiteSpace(resource?.DisplayName)
                ? normalized
                : resource.DisplayName;
        }

        private void UpdateActionBarCooldownPresentation(long nowMs, float gcdFrac, LocalCombatState combat)
        {
            bool targetLosBlocked = AdvisoryTargetLineOfSight.IsSelectedTargetBlockedCached();
            UpdateSlotCooldownPresentation(_disciplineBarStates, _disciplineBarCd, _disciplineBarText, _disciplineBarChargeText, nowMs, gcdFrac, combat, targetLosBlocked);
            UpdateSlotCooldownPresentation(_spellbookGridStates, _spellbookGridCd, _spellbookGridText, _spellbookGridChargeText, nowMs, gcdFrac, combat, targetLosBlocked);
            UpdateSlotCooldownPresentation(_abilityGridStates, _abilityGridCd, _abilityGridText, _abilityGridChargeText, nowMs, gcdFrac, combat, targetLosBlocked);
        }

        private static void UpdateSlotCooldownPresentation(
            ActionBarSlotState[] states,
            Image[] overlays,
            Text[] cooldownTexts,
            Text[] chargeTexts,
            long nowMs,
            float gcdFrac,
            LocalCombatState combat,
            bool targetLosBlocked)
        {
            for (int index = 0; index < states.Length; index++)
            {
                Image overlay = overlays[index];
                if (overlay == null)
                    continue;

                ActionBarSlotState state = states[index] ?? ActionBarSlotState.Empty(string.Empty);
                Text cooldownText = cooldownTexts[index];
                Text chargeText = chargeTexts[index];

                if (!state.IsVisible)
                {
                    SetFillIfChanged(overlay, 0f);
                    SetColorIfChanged(overlay, CdOverlay);
                    SetTextIfChanged(cooldownText, string.Empty);
                    SetTextIfChanged(chargeText, string.Empty);
                    continue;
                }

                if (state.IsFixed)
                {
                    RenderFixedActionState(
                        NetworkManager.Instance?.Conn,
                        state.ActionId,
                        chargeText,
                        combat,
                        nowMs,
                        out float fixedFrac,
                        out float fixedRemSec);
                    SetFillIfChanged(overlay, fixedFrac);
                    SetColorIfChanged(overlay, CdOverlay);
                    SetTextIfChanged(cooldownText, fixedRemSec > 1f ? $"{fixedRemSec:F0}" : string.Empty);
                    continue;
                }

                float frac = state.UsesGlobalCooldown ? gcdFrac : 0f;
                Color col = frac > 0f ? GcdOverlay : CdOverlay;
                float remSec = 0f;

                if (combat.SpellCooldowns.TryGetValue(state.ActionId, out var cd) && cd.durationMs > 0)
                {
                    float rem = Mathf.Max(0f, cd.lastCastMs + cd.durationMs - nowMs);
                    float spellFrac = rem / cd.durationMs;
                    if (spellFrac > frac)
                    {
                        frac = spellFrac;
                        col = CdOverlay;
                        remSec = rem / 1000f;
                    }
                }

                // Advisory LOS gray-out (S4): only when no cooldown or GCD is
                // drawing on the overlay, so timing presentation keeps
                // precedence over the advisory dim.
                if (frac <= 0f && targetLosBlocked && state.RequiresTargetLos)
                {
                    frac = 1f;
                    col = LosBlockedOverlay;
                }

                SetFillIfChanged(overlay, frac);
                SetColorIfChanged(overlay, col);
                SetTextIfChanged(cooldownText, remSec > 1f ? $"{remSec:F0}" : string.Empty);
                SetTextIfChanged(chargeText, string.Empty);
            }
        }

        private static void RenderFixedActionState(
            DbConnection? conn,
            string actionId,
            Text chargeText,
            LocalCombatState combat,
            long nowMs,
            out float cooldownFraction,
            out float remainingSeconds)
        {
            cooldownFraction = 0f;
            remainingSeconds = 0f;
            SetTextIfChanged(chargeText, string.Empty);

            string normalizedFixedActionId = WireIdentifier.Normalize(actionId);
            if (string.Equals(normalizedFixedActionId, FixedActionIds.Dodge, StringComparison.Ordinal))
            {
                RenderDodgeChargeState(chargeText, combat, nowMs, out cooldownFraction, out remainingSeconds);
                return;
            }

            if (string.Equals(normalizedFixedActionId, FixedActionIds.Parry, StringComparison.Ordinal))
            {
                RenderParryCooldownState(conn, nowMs, out cooldownFraction, out remainingSeconds);
                return;
            }

            if (combat.SpellCooldowns.TryGetValue(normalizedFixedActionId, out var cd) && cd.durationMs > 0)
            {
                float rem = Mathf.Max(0f, cd.lastCastMs + cd.durationMs - nowMs);
                cooldownFraction = Mathf.Clamp01(rem / cd.durationMs);
                remainingSeconds = rem / 1000f;
            }
        }

        private static void RenderParryCooldownState(
            DbConnection? conn,
            long nowMs,
            out float cooldownFraction,
            out float remainingSeconds)
        {
            cooldownFraction = 0f;
            remainingSeconds = 0f;
            PlayerEntity? entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (conn == null || entity == null)
                return;

            if (!LocalDefensePrediction.TryGetSuccessfulParryCooldown(
                    conn,
                    entity,
                    nowMs,
                    out long remainingMs,
                    out long durationMs))
                return;

            cooldownFraction = Mathf.Clamp01(remainingMs / (float)durationMs);
            remainingSeconds = remainingMs / 1000f;
        }

        private static void RenderDodgeChargeState(
            Text chargeText,
            LocalCombatState combat,
            long nowMs,
            out float cooldownFraction,
            out float remainingSeconds)
        {
            cooldownFraction = 0f;
            remainingSeconds = 0f;

            if (!combat.FixedActionCharges.TryGetValue(FixedActionIds.Dodge, out var charges))
                return;

            uint current = Math.Min(charges.CurrentCharges, charges.MaxCharges);
            SetTextIfChanged(chargeText, charges.MaxCharges > 0 ? current.ToString() : string.Empty);
            if (current >= charges.MaxCharges || !charges.IsRecharging || charges.RechargeDurationMs <= 0L)
                return;

            long remainingMs = Math.Max(0L, charges.NextChargeReadyMs - nowMs);
            cooldownFraction = Mathf.Clamp01(remainingMs / (float)charges.RechargeDurationMs);
            if (current == 0)
                remainingSeconds = remainingMs / 1000f;
        }

        private static void TriggerActionRef(DbConnection? conn, ActiveActionBarAction resolved)
        {
            if (conn == null || !resolved.HasAssignedAction)
                return;

            if (resolved.IsFixed)
            {
                FixedActionDispatcher.TryTrigger(resolved, conn);
                return;
            }

            ActionBarInputDispatcher.TryTrigger(resolved, conn);
        }

        private static void ReleaseActionRef(DbConnection? conn, ActiveActionBarAction resolved)
        {
            if (conn == null || !resolved.HasAssignedAction || !resolved.IsFixed)
                return;

            FixedActionDispatcher.TryRelease(resolved, conn);
        }

        private static int GridIndex(int row, int col) => row * ActionBarLayout.Columns + col;

        private static string KeyLabelForCell(int row, int col)
        {
            return ActionBarKeymap.KeyLabelForCell(row, col);
        }

        private static void SetActionBarSlotPresentation(
            Image overlay,
            Image icon,
            ActionBarSlotState state,
            Color slotColor,
            Sprite? iconSprite)
        {
            var slot = overlay.transform.parent;
            var slotImage = slot.GetComponent<Image>();
            if (slotImage != null)
                SetColorIfChanged(slotImage, slot.Find("Frame") != null ? TransparentSlotInput : slotColor);

            if (icon != null)
            {
                icon.sprite = iconSprite;
                icon.enabled = state.IsVisible && iconSprite != null;
            }

            var keyText = slot.Find("Key")?.GetComponent<Text>();
            if (keyText != null)
                SetTextIfChanged(keyText, state.KeyLabel);

            var labelText = slot.Find("Lbl")?.GetComponent<Text>();
            if (labelText != null)
                SetTextIfChanged(labelText, state.IsVisible ? state.Label : string.Empty);
        }

        private static Color ResolveActionBarColor(DbConnection? conn, string abilityId, bool isVisible)
        {
            if (!isVisible || conn == null || string.IsNullOrWhiteSpace(abilityId))
                return SlotBg;

            AbilityCatalog? ability = conn.Db.AbilityCatalog.AbilityId.Find(abilityId);
            if (ability == null)
                return SlotBg;

            int hash = Mathf.Abs(ability.AbilityId.GetHashCode());
            Color[] colors =
            {
                new(0.72f, 0.10f, 0.04f, 0.95f),
                new(0.45f, 0.06f, 0.75f, 0.95f),
                new(0.11f, 0.35f, 0.62f, 0.95f),
                new(0.40f, 0.22f, 0.08f, 0.95f),
            };
            return colors[hash % colors.Length];
        }

        private static Color ResolveResourceActionBarColor(DbConnection? conn, string resourceKind)
        {
            if (conn == null || string.IsNullOrWhiteSpace(resourceKind))
                return SlotBg;

            ResourceCatalog? resource = conn.Db.ResourceCatalog.ResourceKind.Find(WireIdentifier.Normalize(resourceKind));
            return resource == null ? SlotBg : Hex(resource.ColorHex);
        }

        private static Color FixedActionColor(string actionId)
        {
            return WireIdentifier.Normalize(actionId) switch
            {
                FixedActionIds.Dodge => new Color(0.16f, 0.36f, 0.34f, 0.95f),
                FixedActionIds.Parry => new Color(0.50f, 0.42f, 0.16f, 0.95f),
                _ => new Color(0.58f, 0.20f, 0.08f, 0.95f),
            };
        }

        private static Color DisabledFixedActionColor(string actionId)
        {
            Color color = FixedActionColor(actionId);
            color.a = 0.42f;
            return color;
        }

        private static bool UsesGlobalCooldown(string spellId, string combatProfile)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return true;

            string resolvedCombatProfile = string.IsNullOrWhiteSpace(combatProfile)
                ? CombatProfileResolver.ResolveForOwner(conn, conn.Identity)
                : combatProfile;

            var meleeGameplay = MeleeGameplayResolver.ResolveForAction(
                conn,
                conn.Identity,
                resolvedCombatProfile,
                spellId);
            if (meleeGameplay != null)
                return meleeGameplay.UsesGlobalCooldown;

            var def = conn.Db.SpellDefinition.Kind.Find(spellId);
            return def?.UsesGlobalCooldown ?? true;
        }

        private sealed class ActionBarClickTarget : MonoBehaviour, IPointerClickHandler, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
        {
            private Action? _onActivate;
            private Action? _onRelease;
            private bool _holdMode;
            private bool _pressed;

            public void Configure(Action? onActivate, Action? onRelease = null, bool holdMode = false)
            {
                _onActivate = onActivate;
                _onRelease = onRelease;
                _holdMode = holdMode;
                _pressed = false;
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (_holdMode
                    || eventData.button != PointerEventData.InputButton.Left
                    || eventData.dragging
                    || ActionBarDragSource.ShouldSuppressClick(gameObject))
                    return;

                _onActivate?.Invoke();
            }

            public void OnPointerDown(PointerEventData eventData)
            {
                if (!_holdMode || eventData.button != PointerEventData.InputButton.Left)
                    return;

                _pressed = true;
                _onActivate?.Invoke();
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                if (!_holdMode || eventData.button != PointerEventData.InputButton.Left)
                    return;

                ReleaseIfPressed();
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                if (!_holdMode)
                    return;

                ReleaseIfPressed();
            }

            private void ReleaseIfPressed()
            {
                if (!_pressed)
                    return;

                _pressed = false;
                _onRelease?.Invoke();
            }
        }

        private sealed class PartyFrameView
        {
            public readonly GameObject Root;
            public readonly Transform DebuffRow;
            public readonly List<GameObject> DebuffIcons = new();

            private readonly Action<SpacetimeDB.Identity> _onSelected;
            private readonly Text _name;
            private readonly Image _tempHpFill;
            private readonly Image _hpFill;
            private readonly Text _hpText;
            private SpacetimeDB.Identity? _member;

            public PartyFrameView(
                GameObject root,
                Text name,
                Image tempHpFill,
                Image hpFill,
                Text hpText,
                Transform debuffRow,
                UnitFrameClickTarget clickTarget,
                Action<SpacetimeDB.Identity> onSelected)
            {
                Root = root;
                DebuffRow = debuffRow;
                _onSelected = onSelected;
                _name = name;
                _tempHpFill = tempHpFill;
                _hpFill = hpFill;
                _hpText = hpText;
                clickTarget.Configure(SelectCurrentMember);
            }

            public void Set(SpacetimeDB.Identity member, string name, HealthBarPresentation health, string hpText)
            {
                _member = member;
                SetActiveIfChanged(Root, true);
                SetTextIfChanged(_name, name);
                SetHealthBarFill(_hpFill, _tempHpFill, health);
                SetTextIfChanged(_hpText, hpText);
            }

            private void SelectCurrentMember()
            {
                if (_member.HasValue)
                    _onSelected(_member.Value);
            }
        }

        private readonly struct HealthBarPresentation
        {
            public static readonly HealthBarPresentation Empty = new(0f, 0f, 0, string.Empty);

            public readonly float HealthFraction;
            public readonly float ProtectedFraction;
            public readonly int TemporaryHitpoints;
            public readonly string Text;

            public HealthBarPresentation(
                float healthFraction,
                float protectedFraction,
                int temporaryHitpoints,
                string text)
            {
                HealthFraction = healthFraction;
                ProtectedFraction = protectedFraction;
                TemporaryHitpoints = temporaryHitpoints;
                Text = text;
            }
        }

        private sealed class UnitFrameClickTarget : MonoBehaviour, IPointerClickHandler
        {
            private Action? _onClick;

            public void Configure(Action onClick)
            {
                _onClick = onClick;
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left)
                    return;

                _onClick?.Invoke();
            }
        }

        private sealed class ActionBarSlotState
        {
            public readonly string KeyLabel;
            public readonly string Label;
            public readonly bool IsVisible;
            public readonly bool IsFixed;
            public readonly string ActionId;
            public readonly bool UsesGlobalCooldown;
            public readonly bool RequiresTargetLos;

            public ActionBarSlotState(
                string keyLabel,
                string label,
                bool isVisible,
                bool isFixed,
                string actionId,
                bool usesGlobalCooldown,
                bool requiresTargetLos)
            {
                KeyLabel = keyLabel;
                Label = label;
                IsVisible = isVisible;
                IsFixed = isFixed;
                ActionId = actionId;
                UsesGlobalCooldown = usesGlobalCooldown;
                RequiresTargetLos = requiresTargetLos;
            }

            public static ActionBarSlotState Empty(string keyLabel) =>
                new(keyLabel, string.Empty, false, false, string.Empty, false, false);
        }

        private static (bool isVisible, string label, Color color) ResolvePrimaryResourcePresentation(PlayerEntity player)
        {
            if (string.IsNullOrWhiteSpace(player.PrimaryResourceKind) || player.MaxPrimaryResource <= 0.001f)
                return (false, string.Empty, ResourceBlue);

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return (true, player.PrimaryResourceKind, ResourceBlue);

            ResourceCatalog? resource = conn.Db.ResourceCatalog.ResourceKind.Find(player.PrimaryResourceKind);
            string label = resource?.DisplayName ?? player.PrimaryResourceKind;
            Color color = resource != null ? Hex(resource.ColorHex) : ResourceBlue;
            return (true, label.ToUpperInvariant(), color);
        }

        private static void RenderPlayerResourceBar(
            Image fill,
            Text text,
            string resourceKind,
            float current,
            float max)
        {
            var presentation = ResolveResourcePresentation(resourceKind, max);
            if (!presentation.isVisible)
            {
                SetColorIfChanged(fill, ResourceBlue);
                SetUnitFrameBarFill(fill, 0f);
                SetTextIfChanged(text, string.Empty);
                return;
            }

            float fraction = max > 0.001f ? Mathf.Clamp01(current / max) : 0f;
            SetColorIfChanged(fill, presentation.color);
            SetUnitFrameBarFill(fill, fraction);
            SetTextIfChanged(text, $"{presentation.label} {Mathf.RoundToInt(current)} / {Mathf.RoundToInt(max)}");
        }

        private static (bool isVisible, string label, Color color) ResolveResourcePresentation(
            string resourceKind,
            float max)
        {
            if (string.IsNullOrWhiteSpace(resourceKind) || max <= 0.001f)
                return (false, string.Empty, ResourceBlue);

            var conn = NetworkManager.Instance?.Conn;
            string normalizedKind = WireIdentifier.Normalize(resourceKind);
            if (conn == null)
                return (true, normalizedKind, ResourceBlue);

            ResourceCatalog? resource = conn.Db.ResourceCatalog.ResourceKind.Find(normalizedKind);
            string label = resource?.DisplayName ?? normalizedKind;
            Color color = resource != null ? Hex(resource.ColorHex) : ResourceBlue;
            return (true, label.ToUpperInvariant(), color);
        }

        // --- Cast Bar ---

        private void UpdateCastBar()
        {
            long nowMs = ArenaServerClock.HasEstimate
                ? ArenaServerClock.ServerNowMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TimedActionPresentationSnapshot? cast =
                TimedActionPresentation.Select(
                    LocalCombatState.Instance.CurrentTimedAction(nowMs),
                    LocalInteractionState.Instance.CurrentTimedAction(nowMs));
            if (cast is not { } c)
            {
                SetActiveIfChanged(_castRoot, false);
                _castKind = "";
                _castDisplayName = "";
                _castShowsCharge = false;
                _castStartMs = 0L;
                _castEndMs = 0L;
                return;
            }

            long totalMs = c.EndMs - c.StartMs;
            if (totalMs <= 0) { SetActiveIfChanged(_castRoot, false); return; }

            if (_castKind != c.ActionId
                || _castStartMs != c.StartMs
                || _castEndMs != c.EndMs
                || _castStyle != c.Style)
            {
                _castStartMs = c.StartMs;
                _castEndMs = c.EndMs;
                _castKind = c.ActionId;
                _castStyle = c.Style;
                if (c.Style == TimedActionPresentationStyle.CombatCast)
                {
                    var conn = NetworkManager.Instance?.Conn;
                    var spellDef = conn?.Db.SpellDefinition.Kind.Find(c.Label);
                    _castShowsCharge =
                        SpellDefinitionContracts.ShowsChargePresentation(spellDef);
                    _castDisplayName = ActionPresentation.ResolveDisplayName(
                        conn,
                        conn?.Identity,
                        c.Label,
                        c.Label);
                }
                else
                {
                    _castShowsCharge = false;
                    _castDisplayName = c.Label;
                }
            }

            float elapsed = nowMs - _castStartMs;
            float progress = Mathf.Clamp01(elapsed / totalMs);
            float remaining = Mathf.Max(0f, totalMs - elapsed) / 1000f;

            SetFillIfChanged(_castFill, progress);
            SetColorIfChanged(_castFill, _castShowsCharge ? CastOrange : CastGold);
            SetTextIfChanged(_castName, _castShowsCharge ? $"{_castDisplayName} (CHARGING)" : _castDisplayName);
            SetTextIfChanged(_castTime, $"{remaining:F1}s");
            SetActiveIfChanged(_castRoot, true);
        }

        // --- Respawn ---

        private void UpdateRespawn(PlayerEntity p)
        {
            if (p.IsAlive) { SetActiveIfChanged(_respawnRoot, false); return; }

            // Eliminated in an arena match — no respawn countdown.
            if (p.IsEliminated)
            {
                SetActiveIfChanged(_respawnRoot, true);
                SetTextIfChanged(_respawnText, "Eliminated");
                return;
            }

            // Training / practice — existing respawn timer unchanged.
            SetActiveIfChanged(_respawnRoot, true);
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long rMs   = p.RespawnAt.MicrosecondsSinceUnixEpoch / 1000L;
            float left = Mathf.Max(0f, (rMs - nowMs) / 1000f);
            SetTextIfChanged(_respawnText, left > 0.1f
                ? $"Respawning in {left:F1}s"
                : "Respawning...");
        }

        // ===============================================================
        // UI CONSTRUCTION HELPERS
        // ===============================================================

        private void FrameChrome(GameObject go)
        {
            var bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.75f);
            bg.raycastTarget = false;
            var ol = go.AddComponent<Outline>();
            ol.effectColor   = FrameBorder;
            ol.effectDistance = new Vector2(1, 1);
        }

        private GameObject Panel(string name, Vector2 anchor, Vector2 pivot,
            Vector2 size, Vector2 offset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin        = anchor;
            rt.anchorMax        = anchor;
            rt.pivot            = pivot;
            rt.sizeDelta        = size;
            rt.anchoredPosition = offset;
            return go;
        }

        private static GameObject Child(Transform parent, string name,
            Vector2? anchor = null, Vector2? pivot = null,
            Vector2? size = null, Vector2? offset = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            if (anchor.HasValue) { rt.anchorMin = anchor.Value; rt.anchorMax = anchor.Value; }
            if (pivot.HasValue) rt.pivot = pivot.Value;
            if (size.HasValue) rt.sizeDelta = size.Value;
            if (offset.HasValue) rt.anchoredPosition = offset.Value;
            return go;
        }

        private static void Stretch(GameObject go)
        {
            var rt = ArenaUiKit.EnsureComponent<RectTransform>(go);
            rt.anchorMin        = Vector2.zero;
            rt.anchorMax        = Vector2.one;
            rt.sizeDelta        = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
        }

        private static void AddUnitFrameArt(Transform parent, bool mirrored, bool raycastTarget = false)
        {
            var art = Child(parent, "FrameArt");
            Stretch(art);
            var image = art.AddComponent<Image>();
            image.sprite = Resources.Load<Sprite>(UnitFrameSpritePath);
            image.color = Color.white;
            image.preserveAspect = true;
            image.raycastTarget = raycastTarget;
            art.transform.SetAsFirstSibling();
            if (mirrored)
                art.transform.localScale = new Vector3(-1f, 1f, 1f);
        }

        private static Image BuildUnitFrameBarFill(
            Transform parent,
            string name,
            IReadOnlyList<Vector2> shape,
            Color color,
            bool fillFromRight = false)
        {
            // The fill is a normal Unity Image so it follows the same renderer path as the
            // rest of the HUD. UnitFrameBarSprite provides an alpha mask for the slanted shape;
            // Image.fillAmount then handles the horizontal fill clipping.
            Rect bounds = UnitFrameBarBounds(shape);
            var go = Child(parent, $"{name}Fill",
                new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(bounds.width, bounds.height),
                new Vector2(bounds.xMin, bounds.yMin));
            var image = go.AddComponent<Image>();
            image.sprite = UnitFrameBarSprite(shape, bounds, $"{name}Fill");
            image.color = color;
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillOrigin = fillFromRight
                ? (int)Image.OriginHorizontal.Right
                : (int)Image.OriginHorizontal.Left;
            image.fillAmount = 1f;
            image.raycastTarget = false;
            go.transform.SetAsLastSibling();
            return image;
        }

        private static Rect UnitFrameBarBounds(IReadOnlyList<Vector2> shape)
        {
            float minX = shape[0].x;
            float minY = shape[0].y;
            float maxX = shape[0].x;
            float maxY = shape[0].y;
            for (int i = 1; i < shape.Count; i++)
            {
                minX = Mathf.Min(minX, shape[i].x);
                minY = Mathf.Min(minY, shape[i].y);
                maxX = Mathf.Max(maxX, shape[i].x);
                maxY = Mathf.Max(maxY, shape[i].y);
            }

            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private static readonly Dictionary<string, Sprite> UnitFrameBarSprites = new();
        private static Sprite UnitFrameBarSprite(IReadOnlyList<Vector2> shape, Rect bounds, string name)
        {
            string key = $"{name}:{bounds.xMin:F1},{bounds.yMin:F1},{bounds.width:F1},{bounds.height:F1}";
            if (UnitFrameBarSprites.TryGetValue(key, out var cached))
                return cached;

            // Generate the alpha mask at runtime from the annotated polygon above. This keeps
            // the tunable geometry in one place while avoiding a custom Graphic renderer.
            int width = Mathf.Max(1, Mathf.CeilToInt(bounds.width));
            int height = Mathf.Max(1, Mathf.CeilToInt(bounds.height));
            var pixels = new Color32[width * height];
            var clear = new Color32(255, 255, 255, 0);
            var white = new Color32(255, 255, 255, 255);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Vector2 point = new(bounds.xMin + x + 0.5f, bounds.yMin + y + 0.5f);
                    pixels[y * width + x] = IsInsidePolygon(point, shape) ? white : clear;
                }
            }

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = $"HUD_{name}_Shape",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            var sprite = Sprite.Create(tex, new Rect(0, 0, width, height), Vector2.zero, 100f, 0, SpriteMeshType.FullRect);
            sprite.name = tex.name;
            UnitFrameBarSprites[key] = sprite;
            return sprite;
        }

        private static bool IsInsidePolygon(Vector2 point, IReadOnlyList<Vector2> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                bool crossesY = a.y > point.y != b.y > point.y;
                if (!crossesY)
                    continue;

                float crossX = (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x;
                if (point.x < crossX)
                    inside = !inside;
            }

            return inside;
        }

        private static Vector2 MirroredUnitFramePos(Vector2 position, Vector2 size)
        {
            // Mirror label rectangles around the frame width while preserving their size.
            return new Vector2(UnitFrameW - position.x - size.x, position.y);
        }

        private static Vector2[] MirroredUnitFrameShape(IReadOnlyList<Vector2> shape)
        {
            // Mirror polygon X coordinates only; point order remains valid for the alpha-mask
            // generator and the fill direction is handled by Image.fillOrigin.
            var mirrored = new Vector2[shape.Count];
            for (int i = 0; i < shape.Count; i++)
                mirrored[i] = new Vector2(UnitFrameW - shape[i].x, shape[i].y);
            return mirrored;
        }

        private static Vector2[] ScaledUnitFrameShape(IReadOnlyList<Vector2> shape, float scale)
        {
            var scaled = new Vector2[shape.Count];
            for (int i = 0; i < shape.Count; i++)
                scaled[i] = shape[i] * scale;
            return scaled;
        }

        private static Image FilledBar(Transform parent, string name, Color color, bool fillFromRight = false)
        {
            var go = Child(parent, name);
            Stretch(go);
            var img = go.AddComponent<Image>();
            img.color      = color;
            img.sprite     = DefaultFilledSprite();
            img.type       = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Horizontal;
            img.fillOrigin = fillFromRight
                ? (int)Image.OriginHorizontal.Right
                : (int)Image.OriginHorizontal.Left;
            img.fillAmount = 1f;
            img.raycastTarget = false;
            return img;
        }

        private static Sprite? _defaultFilledSprite;
        private static Sprite DefaultFilledSprite()
        {
            if (_defaultFilledSprite != null) return _defaultFilledSprite;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "HUD_Default_White" };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _defaultFilledSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 100f);
            _defaultFilledSprite.name = "HUD_Default_White";
            return _defaultFilledSprite;
        }

        private static void SetUnitFrameBarFill(Image graphic, float frac)
        {
            SetFillIfChanged(graphic, Mathf.Clamp01(frac));
        }

        private static void SetFillIfChanged(Image graphic, float fillAmount)
        {
            fillAmount = Mathf.Clamp01(fillAmount);
            if (Mathf.Abs(graphic.fillAmount - fillAmount) > 0.0005f)
                graphic.fillAmount = fillAmount;
        }

        private static void SetTextIfChanged(Text text, string value)
        {
            if (!string.Equals(text.text, value, StringComparison.Ordinal))
                text.text = value;
        }

        private static void SetColorIfChanged(Graphic graphic, Color color)
        {
            if (graphic.color != color)
                graphic.color = color;
        }

        private static void SetActiveIfChanged(GameObject go, bool active)
        {
            if (go.activeSelf != active)
                go.SetActive(active);
        }

        private static void Img(GameObject go, Color color)
        {
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        private static Text Label(Transform parent, string name,
            Vector2? anchor = null, Vector2? pivot = null,
            Vector2? size = null, Vector2? offset = null,
            int fontSize = 12, Color? color = null,
            TextAnchor alignment = TextAnchor.MiddleLeft,
            bool stretch = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            if (stretch)
            {
                rt.anchorMin        = Vector2.zero;
                rt.anchorMax        = Vector2.one;
                rt.sizeDelta        = Vector2.zero;
                rt.anchoredPosition = Vector2.zero;
            }
            else
            {
                if (anchor.HasValue) { rt.anchorMin = anchor.Value; rt.anchorMax = anchor.Value; }
                if (pivot.HasValue) rt.pivot = pivot.Value;
                if (size.HasValue) rt.sizeDelta = size.Value;
                if (offset.HasValue) rt.anchoredPosition = offset.Value;
            }

            var txt = go.AddComponent<Text>();
            txt.font          = Font();
            txt.fontSize      = fontSize;
            txt.alignment     = alignment;
            txt.color         = color ?? Color.white;
            txt.raycastTarget = false;

            var sh = go.AddComponent<Shadow>();
            sh.effectColor    = new Color(0, 0, 0, 0.8f);
            sh.effectDistance  = new Vector2(1, -1);

            return txt;
        }

        private static Button MakeHudButton(Transform parent, string name, string text,
            Vector2 anchor, Vector2 pivot, Vector2 size, Vector2 offset, Color fill)
        {
            var go = Child(parent, name, anchor, pivot, size, offset);
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

            var label = Label(go.transform, "Label", stretch: true,
                fontSize: 10, color: Color.white, alignment: TextAnchor.MiddleCenter);
            label.fontStyle = FontStyle.Bold;
            label.text = text;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 7;
            label.resizeTextMaxSize = 10;

            return button;
        }

        private Transform IconRow(Transform parent, string name,
            Vector2 anchor, Vector2 pivot, Vector2 size, Vector2 offset,
            TextAnchor childAlign = TextAnchor.MiddleLeft)
        {
            GameObject go;
            if (parent == transform)
                go = Panel(name, anchor, pivot, size, offset);
            else
            {
                go = Child(parent, name, anchor, pivot, size, offset);
            }
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing                = IconGap;
            layout.childAlignment         = childAlign;
            layout.childForceExpandWidth  = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth      = false;
            layout.childControlHeight     = false;
            return go.transform;
        }

        private static GameObject SlotRow(Transform parent, string name,
            Vector2 anchor, Vector2 pivot, Vector2 size, Vector2 offset)
        {
            var go = Child(parent, name, anchor, pivot, size, offset);
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing                = ActionBarLayout.Gap;
            layout.childAlignment         = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth  = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth      = false;
            layout.childControlHeight     = false;
            return go;
        }

        private static GameObject SlotGrid(Transform parent, string name,
            Vector2 anchor, Vector2 pivot, Vector2 size, Vector2 offset)
        {
            var go = Child(parent, name, anchor, pivot, size, offset);
            var layout = go.AddComponent<GridLayoutGroup>();
            layout.cellSize = ActionBarLayout.SlotVector;
            layout.spacing = ActionBarLayout.GapVector;
            layout.startCorner = GridLayoutGroup.Corner.UpperLeft;
            layout.startAxis = GridLayoutGroup.Axis.Horizontal;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = ActionBarLayout.Columns;
            return go;
        }

        private static Font Font() =>
            Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        private sealed class StatusIconView
        {
            public readonly Text Label;
            public readonly Text Stack;
            public readonly Text Time;
            public readonly TooltipTarget Tooltip;
            public string TooltipKey = string.Empty;

            public StatusIconView(Text label, Text stack, Text time, TooltipTarget tooltip)
            {
                Label = label;
                Stack = stack;
                Time = time;
                Tooltip = tooltip;
            }
        }
    }
}
