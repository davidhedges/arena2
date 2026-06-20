#nullable enable

using Arena.Combat;
using Arena.Input;
using Arena.Network;
using Arena.Presentation;
using Arena.Presentation.Appearance;
using Arena.World;
using System.Collections.Generic;
using System.Linq;
using SpacetimeDB;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Arena.UI
{
    [ExecuteAlways]
    public sealed class HubController : MonoBehaviour, IEscapeCloseable
    {
        private const string HubSceneName = "Hub";
        private static readonly string[] StatKinds = { "MIGHT", "INSIGHT", "FINESSE", "QUICKNESS", "FORTITUDE" };
        private static readonly Color ActiveRed = new(0.72f, 0.08f, 0.04f, 0.96f);
        private static readonly Color Transparent = new(0f, 0f, 0f, 0f);

        private Transform? _root;
        private GameObject? _homeRoot;
        private GameObject? _stageRoot;
        private GameObject? _travelMenu;
        private GameObject? _showcaseAvatar;
        private RuntimeAvatarController? _showcaseAvatarController;
        private RuntimeAvatarBinding? _showcaseAvatarBinding;
        private WeaponAttachmentController? _showcaseWeaponAttachments;
        private Button? _playButton;
        private Button? _loadoutButton;
        private Button? _ctaButton;
        private TMP_Text? _ctaSubtitle;
        private TMP_Text? _classNameText;
        private TMP_Text? _classMetaText;
        private TMP_Text? _identityBlurbText;
        private Transform? _loadoutPanel;
        private GameObject? _weaponLabel;
        private GameObject? _weaponName;
        private GameObject? _weaponPreview;
        private RectTransform? _loadoutStatsRoot;
        private readonly Dictionary<string, TMP_Text> _loadoutStatValues = new();
        private TMP_Text? _professionNameText;
        private TMP_Text? _trinketNameText;
        private GameObject? _greatswordPreview;
        private GameObject? _swordShieldPreview;
        private bool _wired;
        private string _lastCombatProfile = string.Empty;
        private string _lastShowcaseAppearanceSignature = string.Empty;
        private string _lastFailedShowcaseAppearanceSignature = string.Empty;

        public int EscapeClosePriority => IsTravelMenuOpen ? 80 : 30;
        public bool IsEscapeCloseable => IsTravelMenuOpen || IsLoadoutViewOpen;

        private bool IsTravelMenuOpen => Application.isPlaying
            && _travelMenu != null
            && _travelMenu.activeSelf;

        private bool IsLoadoutViewOpen => Application.isPlaying
            && string.Equals(SceneManager.GetActiveScene().name, HubSceneName, System.StringComparison.Ordinal)
            && HubViewState.Current == HubViewScreen.Loadout;

        private void OnEnable()
        {
            if (Application.isPlaying)
                RuntimeUiEscapeRouter.Register(this);
            RemoveGeneratedCombinedCharacterPreview();
            Resolve();
            WireButtons();
            ApplyState();
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
                RuntimeUiEscapeRouter.Unregister(this);
        }

        private void Update()
        {
            RemoveGeneratedCombinedCharacterPreview();
            Resolve();
            if (!_wired)
                WireButtons();
            ApplyState();
        }

        private static void RemoveGeneratedCombinedCharacterPreview()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded)
                return;

            if (!string.Equals(activeScene.name, HubSceneName, System.StringComparison.Ordinal))
                return;

            foreach (GameObject rootObject in activeScene.GetRootGameObjects())
            {
                if (!string.Equals(rootObject.name, "Combined Character", System.StringComparison.Ordinal))
                    continue;

                if (Application.isPlaying)
                    Destroy(rootObject);
                else
                    DestroyImmediate(rootObject);
            }
        }

        private void Resolve()
        {
            _root = transform;
            _homeRoot = _root.Find("HubCanvas/HomeRoot")?.gameObject;
            _stageRoot = _root.Find("StageRoot")?.gameObject;
            _travelMenu = _root.Find("HubCanvas/HomeRoot/TravelMenu")?.gameObject;
            _showcaseAvatar = _root.Find("StageRoot/ShowcaseAnchor/HubShowcaseAvatar/RuntimeAvatarModel")?.gameObject ??
                _root.Find("StageRoot/ShowcaseAnchor/HubShowcaseAvatar")?.gameObject;
            if (Application.isPlaying)
                StarterAssetsRuntimeStripper.StripFrom(_showcaseAvatar);
            _playButton = _root.Find("HubCanvas/TopBar/NavRow/PlayButton")?.GetComponent<Button>();
            _loadoutButton = _root.Find("HubCanvas/TopBar/NavRow/LoadoutButton")?.GetComponent<Button>();
            _ctaButton = _root.Find("HubCanvas/HomeRoot/CtaPanel/PlayButton")?.GetComponent<Button>();
            _ctaSubtitle = _root.Find("HubCanvas/HomeRoot/CtaPanel/PlayButton/SubLabel")?.GetComponent<TMP_Text>();
            _classNameText = _root.Find("HubCanvas/HomeRoot/IdentityPanel/ClassName")?.GetComponent<TMP_Text>();
            _classMetaText = _root.Find("HubCanvas/HomeRoot/IdentityPanel/ClassMeta")?.GetComponent<TMP_Text>();
            _identityBlurbText = _root.Find("HubCanvas/HomeRoot/IdentityPanel/IdentityBlurb")?.GetComponent<TMP_Text>();
            _loadoutPanel = _root.Find("HubCanvas/HomeRoot/LoadoutPanel");
            _weaponLabel = _root.Find("HubCanvas/HomeRoot/LoadoutPanel/WeaponLabel")?.gameObject;
            _weaponName = _root.Find("HubCanvas/HomeRoot/LoadoutPanel/WeaponName")?.gameObject;
            _weaponPreview = _root.Find("HubCanvas/HomeRoot/LoadoutPanel/WeaponPreview")?.gameObject;
            _loadoutStatsRoot = _root.Find("HubCanvas/HomeRoot/LoadoutPanel/AllocatedStatsRoot")?.GetComponent<RectTransform>();
            _professionNameText = _root.Find("HubCanvas/HomeRoot/LoadoutPanel/ProfessionCard/ProfessionName")?.GetComponent<TMP_Text>();
            _trinketNameText = _root.Find("HubCanvas/HomeRoot/LoadoutPanel/TrinketName")?.GetComponent<TMP_Text>();
            _greatswordPreview = _root.Find("HubCanvas/HomeRoot/LoadoutPanel/WeaponPreview/GreatswordSilhouette")?.gameObject;
            _swordShieldPreview = _root.Find("HubCanvas/HomeRoot/LoadoutPanel/WeaponPreview/SwordShieldSilhouette")?.gameObject;
            CacheLoadoutStatValues();
            EnsureLoadoutSummaryUi();
            EnsureTravelDestinationButtons();
        }

        private void WireButtons()
        {
            if (_playButton != null)
            {
                _playButton.onClick.RemoveAllListeners();
                _playButton.onClick.AddListener(() =>
                {
                    HubViewState.Show(HubViewScreen.Play);
                    if (_travelMenu != null)
                        _travelMenu.SetActive(false);
                    ApplyState();
                });
            }

            if (_loadoutButton != null)
            {
                _loadoutButton.onClick.RemoveAllListeners();
                _loadoutButton.onClick.AddListener(() =>
                {
                    HubViewState.Show(HubViewScreen.Loadout);
                    if (_travelMenu != null)
                        _travelMenu.SetActive(false);
                    ApplyState();
                });
            }

            if (_ctaButton != null)
            {
                _ctaButton.onClick.RemoveAllListeners();
                _ctaButton.onClick.AddListener(() =>
                {
                    if (_travelMenu != null && Application.isPlaying)
                        _travelMenu.SetActive(!_travelMenu.activeSelf);
                });
            }

            Transform? destinations = _root?.Find("HubCanvas/HomeRoot/TravelMenu/DestinationButtons");
            if (destinations != null)
            {
                EnsureTravelDestinationButtons();
                foreach (Transform child in destinations)
                {
                    Button? destinationButton = child.GetComponent<Button>();
                    if (destinationButton == null)
                        continue;

                    string sceneName = child.name.StartsWith("Travel_", System.StringComparison.Ordinal)
                        ? child.name.Substring("Travel_".Length)
                        : string.Empty;

                    destinationButton.onClick.RemoveAllListeners();
                    destinationButton.onClick.AddListener(() =>
                    {
                        if (string.IsNullOrWhiteSpace(sceneName))
                            return;

                        OpenWorldTravelCatalog.SetCurrentScene(sceneName);
                        NetworkManager.Instance?.Conn?.Reducers.SetOpenWorldScene(sceneName);
                        SceneManager.LoadScene(sceneName);
                    });
                }
            }

            _wired = _playButton != null && _loadoutButton != null && _ctaButton != null;
        }

        private void ApplyState()
        {
            bool activeHub = string.Equals(SceneManager.GetActiveScene().name, HubSceneName, System.StringComparison.Ordinal);
            bool showHome = activeHub && HubViewState.Current == HubViewScreen.Play;
            bool showStage = activeHub && HubViewState.Current == HubViewScreen.Play;

            if (_homeRoot != null)
                _homeRoot.SetActive(showHome);
            if (_stageRoot != null)
                _stageRoot.SetActive(showStage);
            if (_travelMenu != null && (!showHome || !Application.isPlaying))
                _travelMenu.SetActive(false);

            ApplyNavVisual(_playButton, HubViewState.Current == HubViewScreen.Play);
            ApplyNavVisual(_loadoutButton, HubViewState.Current == HubViewScreen.Loadout);

            if (_ctaSubtitle != null)
                _ctaSubtitle.text = OpenWorldTravelCatalog.CurrentDisplayName.ToUpperInvariant();

            string classId = ResolveClassId();
            ApplyClassIdentity(classId);
            if (showStage)
                ApplyShowcaseAppearance(classId);
            string combatProfile = ResolveCombatProfile();
            ApplyCombatProfile(combatProfile);
            ApplyLoadoutSummary();
            if (_professionNameText != null)
                _professionNameText.text = "ENCHANTING";
            if (_trinketNameText != null)
                _trinketNameText.text = "Charm of Resolve";
        }

        public bool TryCloseForEscape()
        {
            Resolve();
            if (IsTravelMenuOpen)
            {
                _travelMenu!.SetActive(false);
                return true;
            }

            if (IsLoadoutViewOpen)
            {
                HubViewState.Show(HubViewScreen.Play);
                ApplyState();
                return true;
            }

            return false;
        }

        private string ResolveClassId()
        {
            if (Application.isPlaying)
            {
                DbConnection? conn = NetworkManager.Instance?.Conn;
                Identity? identity = conn?.Identity;
                if (conn != null && identity.HasValue)
                {
                    CharacterProgression? progression = conn.Db.CharacterProgression.Owner.Find(identity.Value);
                    if (progression != null && !string.IsNullOrWhiteSpace(progression.ClassId))
                        return progression.ClassId;
                }
            }

            return "WARRIOR";
        }

        private string ResolveCombatProfile()
        {
            if (Application.isPlaying)
            {
                DbConnection? conn = NetworkManager.Instance?.Conn;
                Identity? identity = conn?.Identity;
                if (conn != null && identity.HasValue)
                {
                    CharacterProgression? progression = conn.Db.CharacterProgression.Owner.Find(identity.Value);
                    if (progression != null)
                        return CombatProfileResolver.ResolveForClass(conn, progression.ClassId);
                }
            }

            return CombatProfileIds.TwoHandedSword;
        }

        private void ApplyClassIdentity(string classId)
        {
            string normalized = string.IsNullOrWhiteSpace(classId)
                ? "WARRIOR"
                : classId.Trim().ToUpperInvariant();

            if (_classNameText != null)
                _classNameText.text = normalized;

            if (_classMetaText != null)
            {
                _classMetaText.text = normalized switch
                {
                    "PALADIN" => "MELEE | SWORD & SHIELD",
                    "RANGER" => "RANGED | BOW",
                    "WARRIOR" => "MELEE | GREATSWORD",
                    _ => "MELEE | COMBAT",
                };
            }

            if (_identityBlurbText != null)
            {
                _identityBlurbText.text = normalized switch
                {
                    "PALADIN" => "A shield-bearing frontliner built around protection, counter-pressure, and control.",
                    "RANGER" => "A mobile ranged fighter built around bow pressure, spacing, and mana-backed shots.",
                    "WARRIOR" => "A heavy vanguard built around commitment, pressure, and decisive greatsword attacks.",
                    _ => "A combat specialist with a focused loadout and readable battlefield role.",
                };
            }
        }

        private void ApplyCombatProfile(string combatProfile)
        {
            combatProfile = CombatProfileIds.Normalize(combatProfile);
            if (string.Equals(_lastCombatProfile, combatProfile, System.StringComparison.Ordinal))
                return;

            _lastCombatProfile = combatProfile;

            if (_weaponLabel != null)
                _weaponLabel.SetActive(false);
            if (_weaponName != null)
                _weaponName.SetActive(false);
            if (_weaponPreview != null)
                _weaponPreview.SetActive(false);

            bool swordAndShield = string.Equals(combatProfile, CombatProfileIds.SwordAndShield, System.StringComparison.Ordinal);
            bool twoHandedSword = string.Equals(combatProfile, CombatProfileIds.TwoHandedSword, System.StringComparison.Ordinal);
            if (_greatswordPreview != null)
                _greatswordPreview.SetActive(twoHandedSword);
            if (_swordShieldPreview != null)
                _swordShieldPreview.SetActive(swordAndShield);

            if (!Application.isPlaying || _showcaseAvatar == null || _showcaseAvatarBinding == null)
                return;

            CombatAnimationSet? animationSet = CombatAnimationSetCatalog.Resolve(combatProfile);
            if (animationSet == null)
                return;

            Transform host = GetShowcaseHost();
            WeaponAttachmentController? attachments = _showcaseWeaponAttachments;
            if (attachments == null)
            {
                attachments = host.GetComponent<WeaponAttachmentController>();
                if (attachments == null)
                    attachments = host.gameObject.AddComponent<WeaponAttachmentController>();
                _showcaseWeaponAttachments = attachments;
            }

            attachments.BindMounts(_showcaseAvatarBinding.Mounts);
            attachments.ApplyAnimationSet(animationSet);
            attachments.SetInCombat(true);
            ApplyShowcaseLoop(_showcaseAvatarBinding.AvatarRoot, animationSet);
        }

        private void ApplyShowcaseAppearance(string classId)
        {
            if (!Application.isPlaying || _showcaseAvatar == null)
                return;

            string normalizedClass = string.IsNullOrWhiteSpace(classId)
                ? "WARRIOR"
                : classId.Trim().ToUpperInvariant();
            CharacterAppearance? appearance = ResolveLocalAppearance();
            string signature = appearance != null
                ? RuntimeAvatarController.SignatureFor(appearance)
                : $"CLASS_DEFAULT|HUMAN|MALE|{normalizedClass}";
            if (string.Equals(_lastShowcaseAppearanceSignature, signature, System.StringComparison.Ordinal) &&
                _showcaseAvatarBinding != null)
            {
                return;
            }
            if (string.Equals(_lastFailedShowcaseAppearanceSignature, signature, System.StringComparison.Ordinal) &&
                _showcaseAvatarBinding == null)
            {
                return;
            }

            Transform host = GetShowcaseHost();
            _showcaseAvatar.SetActive(false);

            if (_showcaseAvatarController == null)
            {
                _showcaseAvatarController = host.GetComponent<RuntimeAvatarController>();
                if (_showcaseAvatarController == null)
                    _showcaseAvatarController = host.gameObject.AddComponent<RuntimeAvatarController>();
                _showcaseAvatarController.SetVisualRootParent(host);
            }

            _showcaseWeaponAttachments?.ClearVisuals();
            RuntimeAvatarBinding binding;
            string error;
            bool applied = appearance != null
                ? _showcaseAvatarController.Apply(appearance, out binding, out error)
                : _showcaseAvatarController.ApplyClassDefault(normalizedClass, out binding, out error);
            if (applied)
            {
                _showcaseAvatarBinding = binding;
                _lastShowcaseAppearanceSignature = appearance != null ? binding.AppearanceSignature : signature;
                _lastFailedShowcaseAppearanceSignature = string.Empty;
                _lastCombatProfile = string.Empty;
                StarterAssetsRuntimeStripper.StripFrom(binding.AvatarRoot);
                return;
            }

            Debug.LogWarning($"[{nameof(HubController)}] Failed to assemble showcase avatar: {error}");
            _lastFailedShowcaseAppearanceSignature = signature;
            if (_showcaseAvatarBinding == null)
            {
                _lastShowcaseAppearanceSignature = string.Empty;
                _showcaseAvatar.SetActive(true);
            }
        }

        private Transform GetShowcaseHost()
        {
            if (_showcaseAvatar != null && _showcaseAvatar.transform.parent != null)
                return _showcaseAvatar.transform.parent;

            return _showcaseAvatar != null ? _showcaseAvatar.transform : transform;
        }

        private static CharacterAppearance? ResolveLocalAppearance()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            Identity? identity = conn?.Identity;
            if (conn == null || !identity.HasValue)
                return null;

            return conn.Db.CharacterAppearance.Owner.Filter(identity.Value).FirstOrDefault();
        }

        private void EnsureLoadoutSummaryUi()
        {
            if (_loadoutPanel == null || _loadoutStatsRoot != null)
                return;

            _loadoutStatsRoot = CreateRect(_loadoutPanel, "AllocatedStatsRoot");
            SetTopLeft(_loadoutStatsRoot, new Vector2(24f, -78f), new Vector2(300f, 132f));

            TMP_Text title = CreateText(_loadoutStatsRoot, "AllocatedStatsLabel", "ALLOCATED STATS", 12, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Color(0.72f, 0.75f, 0.80f));
            SetTopLeft(title.rectTransform, Vector2.zero, new Vector2(160f, 18f));

            for (int i = 0; i < StatKinds.Length; i++)
            {
                string statKind = StatKinds[i];
                RectTransform row = CreateRect(_loadoutStatsRoot, $"AllocatedStat_{statKind}");
                SetTopLeft(row, new Vector2(0f, -24f - i * 21f), new Vector2(284f, 20f));

                TMP_Text name = CreateText(row, "Name", PrettyStatName(statKind).ToUpperInvariant(), 12, FontStyles.Bold, TextAlignmentOptions.Left, StatColor(statKind));
                SetAnchored(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(150f, 18f), new Vector2(0f, 0.5f));

                TMP_Text value = CreateText(row, "Value", "0", 13, FontStyles.Bold, TextAlignmentOptions.Right, Color.white);
                LayoutAllocatedStatValue(value);
                _loadoutStatValues[statKind] = value;
            }

            if (_trinketNameText == null && _loadoutPanel.Find("TrinketIcon") != null)
            {
                _trinketNameText = CreateText(_loadoutPanel, "TrinketName", "Charm of Resolve", 16, FontStyles.Bold, TextAlignmentOptions.Left, new Color(0.96f, 0.82f, 0.46f));
                SetAnchored(_trinketNameText.rectTransform, new Vector2(0f, 1f), new Vector2(78f, -372f), new Vector2(210f, 24f), new Vector2(0f, 0.5f));
            }
        }

        private void EnsureTravelDestinationButtons()
        {
            if (_travelMenu == null)
                return;

            RectTransform? menuRect = _travelMenu.GetComponent<RectTransform>();
            Transform? existingList = _travelMenu.transform.Find("DestinationButtons");
            RectTransform list = existingList as RectTransform ?? CreateRect(_travelMenu.transform, "DestinationButtons");

            OpenWorldTravelCatalog.Destination[] destinations = OpenWorldTravelCatalog.All;
            const float buttonHeight = 28f;
            const float buttonGap = 7f;
            const float buttonStep = buttonHeight + buttonGap;
            float listHeight = destinations.Length * buttonStep;
            float menuHeight = Mathf.Max(244f, 92f + listHeight);

            if (menuRect != null)
                menuRect.sizeDelta = new Vector2(menuRect.sizeDelta.x, menuHeight);

            SetTopLeft(list, new Vector2(22f, -64f), new Vector2(316f, listHeight));

            var destinationNames = new HashSet<string>(destinations.Select(destination => $"Travel_{destination.SceneName}"), System.StringComparer.Ordinal);
            for (int i = list.childCount - 1; i >= 0; i--)
            {
                Transform child = list.GetChild(i);
                if (!destinationNames.Contains(child.name))
                {
                    if (Application.isPlaying)
                        Destroy(child.gameObject);
                    else
                        DestroyImmediate(child.gameObject);
                }
            }

            for (int i = 0; i < destinations.Length; i++)
            {
                OpenWorldTravelCatalog.Destination destination = destinations[i];
                string buttonName = $"Travel_{destination.SceneName}";
                Transform? child = list.Find(buttonName);
                RectTransform buttonRect = child as RectTransform ?? CreateRect(list, buttonName);
                buttonRect.SetSiblingIndex(i);
                SetTopLeft(buttonRect, new Vector2(0f, -i * buttonStep), new Vector2(316f, buttonHeight));

                Image image = buttonRect.GetComponent<Image>() ?? buttonRect.gameObject.AddComponent<Image>();
                image.color = new Color(0.05f, 0.055f, 0.065f, 0.96f);

                Button button = buttonRect.GetComponent<Button>() ?? buttonRect.gameObject.AddComponent<Button>();
                button.interactable = true;
                ColorBlock colors = button.colors;
                colors.normalColor = image.color;
                colors.highlightedColor = Color.Lerp(image.color, Color.white, 0.10f);
                colors.selectedColor = colors.highlightedColor;
                colors.pressedColor = Color.Lerp(image.color, Color.black, 0.20f);
                button.colors = colors;

                TMP_Text? label = buttonRect.Find("Label")?.GetComponent<TMP_Text>();
                if (label == null)
                {
                    label = CreateText(buttonRect, "Label", destination.DisplayName, 12, FontStyles.Bold, TextAlignmentOptions.Left, Color.white);
                    SetAnchored(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(14f, 0f), new Vector2(280f, 18f), new Vector2(0f, 0.5f));
                }
                else
                {
                    label.text = destination.DisplayName;
                    label.fontSize = 12;
                    SetAnchored(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(14f, 0f), new Vector2(280f, 18f), new Vector2(0f, 0.5f));
                }
            }
        }

        private void CacheLoadoutStatValues()
        {
            if (_loadoutStatsRoot == null)
                return;

            foreach (string statKind in StatKinds)
            {
                TMP_Text? value = _loadoutStatsRoot.Find($"AllocatedStat_{statKind}/Value")?.GetComponent<TMP_Text>();
                if (value != null)
                {
                    LayoutAllocatedStatValue(value);
                    _loadoutStatValues[statKind] = value;
                }
            }
        }

        private void ApplyLoadoutSummary()
        {
            EnsureLoadoutSummaryUi();
            if (_loadoutStatValues.Count == 0)
                return;

            Dictionary<string, uint> allocations = ActiveStatAllocations();
            foreach (string statKind in StatKinds)
            {
                if (_loadoutStatValues.TryGetValue(statKind, out TMP_Text? value))
                    value.text = allocations.TryGetValue(statKind, out uint allocated) ? allocated.ToString() : "0";
            }
        }

        private Dictionary<string, uint> ActiveStatAllocations()
        {
            var result = StatKinds.ToDictionary(stat => stat, _ => 0u, System.StringComparer.OrdinalIgnoreCase);
            if (!Application.isPlaying)
                return result;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            Identity? identity = conn?.Identity;
            if (conn == null || !identity.HasValue)
                return result;

            CharacterProgression? progression = conn.Db.CharacterProgression.Owner.Find(identity.Value);
            if (progression == null)
                return result;

            string specId = ResolveVisibleSpecId(conn, identity.Value, progression);
            if (string.IsNullOrWhiteSpace(specId))
                return result;

            foreach (SavedSpecStatAllocation allocation in conn.Db.SavedSpecStatAllocation.SpecId.Filter(specId))
            {
                if (result.ContainsKey(allocation.StatKind))
                    result[allocation.StatKind] = allocation.AllocatedPoints;
            }

            return result;
        }

        private static string ResolveVisibleSpecId(DbConnection conn, Identity owner, CharacterProgression progression)
        {
            string? selectedSpecId = LoadoutController.Instance?.SelectedSpecId;
            if (SpecBelongsToCurrentClass(conn, owner, selectedSpecId, progression.ClassId))
                return selectedSpecId!;

            if (ActiveLoadoutResolver.TryResolveActiveSpec(conn, owner, out string classId, out string activeSpecId)
                && string.Equals(classId, ClassIds.Canonicalize(progression.ClassId), System.StringComparison.Ordinal)
                && SpecBelongsToCurrentClass(conn, owner, activeSpecId, progression.ClassId))
            {
                return activeSpecId;
            }

            SavedSpec? fallbackSpec = conn.Db.SavedSpec.Owner
                .Filter(owner)
                .Where(spec => string.Equals(ClassIds.Canonicalize(spec.ClassId), ClassIds.Canonicalize(progression.ClassId), System.StringComparison.Ordinal))
                .OrderBy(spec => spec.CreatedAt.MicrosecondsSinceUnixEpoch)
                .ThenBy(spec => spec.Name, System.StringComparer.Ordinal)
                .FirstOrDefault();

            return fallbackSpec?.SpecId ?? string.Empty;
        }

        private static bool SpecBelongsToCurrentClass(
            DbConnection conn,
            Identity owner,
            string? specId,
            string classId)
        {
            if (string.IsNullOrWhiteSpace(specId))
                return false;

            SavedSpec? spec = conn.Db.SavedSpec.SpecId.Find(specId);
            return spec != null
                && spec.Owner.Equals(owner)
                && string.Equals(ClassIds.Canonicalize(spec.ClassId), ClassIds.Canonicalize(classId), System.StringComparison.Ordinal);
        }

        private static void ApplyShowcaseLoop(GameObject showcaseAvatar, CombatAnimationSet animationSet)
        {
            AnimationClip? loopClip = GetShowcaseLoopClip(animationSet);
            Animator? animator = showcaseAvatar.GetComponentInChildren<Animator>(true);
            if (loopClip == null || animator == null)
                return;

            HubShowcaseAnimationPlayer player =
                showcaseAvatar.GetComponent<HubShowcaseAnimationPlayer>() ??
                showcaseAvatar.AddComponent<HubShowcaseAnimationPlayer>();
            player.Configure(animator, loopClip, startTime: GetShowcasePoseSampleTime(loopClip));
        }

        private static AnimationClip? GetShowcaseLoopClip(CombatAnimationSet animationSet)
        {
            return animationSet.locomotionIdleCombat ??
                animationSet.enterCombatIdle ??
                animationSet.locomotionIdle ??
                animationSet.DrawWeaponClip;
        }

        private static float GetShowcasePoseSampleTime(AnimationClip poseClip)
        {
            return poseClip.length > 0.001f
                ? Mathf.Min(poseClip.length * 0.35f, poseClip.length - 0.001f)
                : 0f;
        }

        private static string PrettyStatName(string statKind)
        {
            return statKind switch
            {
                "MIGHT" => "Might",
                "INSIGHT" => "Insight",
                "FINESSE" => "Finesse",
                "QUICKNESS" => "Quickness",
                "FORTITUDE" => "Fortitude",
                _ => statKind,
            };
        }

        private static Color StatColor(string statKind)
        {
            return statKind switch
            {
                "MIGHT" => new Color(1f, 0.34f, 0.18f),
                "INSIGHT" => new Color(0.74f, 0.28f, 1f),
                "FINESSE" => new Color(0.50f, 1f, 0.36f),
                "QUICKNESS" => new Color(0.28f, 0.72f, 1f),
                "FORTITUDE" => new Color(1f, 0.78f, 0.24f),
                _ => Color.white,
            };
        }

        private static void ApplyNavVisual(Button? button, bool active)
        {
            if (button == null)
                return;

            Image? image = button.GetComponent<Image>();
            TMP_Text? label = button.GetComponentInChildren<TMP_Text>(true);
            if (image != null)
                image.color = active ? ActiveRed : Transparent;
            if (label != null)
                label.color = active ? Color.white : new Color(1f, 1f, 1f, 0.82f);
        }

        private static RectTransform CreateRect(Transform parent, string name)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            return go.AddComponent<RectTransform>();
        }

        private static TMP_Text CreateText(Transform parent, string name, string text, float fontSize, FontStyles style, TextAlignmentOptions alignment, Color color)
        {
            RectTransform rect = CreateRect(parent, name);
            TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.font = TMP_Settings.defaultFontAsset ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            label.text = text;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.alignment = alignment;
            label.color = color;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            return label;
        }

        private static void SetTopLeft(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void SetAnchored(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size, Vector2? pivot = null)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot ?? anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void LayoutAllocatedStatValue(TMP_Text value)
        {
            value.alignment = TextAlignmentOptions.Right;
            SetAnchored(
                value.rectTransform,
                new Vector2(0f, 0.5f),
                new Vector2(228f, 0f),
                new Vector2(56f, 18f),
                new Vector2(0f, 0.5f));
        }
    }
}
