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
        private Button? _equipmentButton;
        private Button? _ctaButton;
        private TMP_Text? _ctaSubtitle;
        private TMP_Text? _identityNameText;
        private TMP_Text? _identityMetaText;
        private TMP_Text? _identityBlurbText;
        private GameObject? _weaponLabel;
        private GameObject? _weaponName;
        private GameObject? _weaponPreview;
        private GameObject? _greatswordPreview;
        private GameObject? _swordShieldPreview;
        private bool _wired;
        private string _lastCombatProfile = string.Empty;
        private string _lastShowcaseAppearanceSignature = string.Empty;
        private string _lastFailedShowcaseAppearanceSignature = string.Empty;

        public int EscapeClosePriority => IsTravelMenuOpen ? 80 : 30;
        public bool IsEscapeCloseable => IsTravelMenuOpen;

        private bool IsTravelMenuOpen => Application.isPlaying
            && _travelMenu != null
            && _travelMenu.activeSelf;

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
            _equipmentButton = _root.Find("HubCanvas/TopBar/NavRow/EquipmentButton")?.GetComponent<Button>();
            _ctaButton = _root.Find("HubCanvas/HomeRoot/CtaPanel/PlayButton")?.GetComponent<Button>();
            _ctaSubtitle = _root.Find("HubCanvas/HomeRoot/CtaPanel/PlayButton/SubLabel")?.GetComponent<TMP_Text>();
            _identityNameText = _root.Find("HubCanvas/HomeRoot/IdentityPanel/GearName")?.GetComponent<TMP_Text>();
            _identityMetaText = _root.Find("HubCanvas/HomeRoot/IdentityPanel/GearMeta")?.GetComponent<TMP_Text>();
            _identityBlurbText = _root.Find("HubCanvas/HomeRoot/IdentityPanel/IdentityBlurb")?.GetComponent<TMP_Text>();
            _weaponLabel = _root.Find("HubCanvas/HomeRoot/EquipmentPanel/WeaponLabel")?.gameObject;
            _weaponName = _root.Find("HubCanvas/HomeRoot/EquipmentPanel/WeaponName")?.gameObject;
            _weaponPreview = _root.Find("HubCanvas/HomeRoot/EquipmentPanel/WeaponPreview")?.gameObject;
            _greatswordPreview = _root.Find("HubCanvas/HomeRoot/EquipmentPanel/WeaponPreview/GreatswordSilhouette")?.gameObject;
            _swordShieldPreview = _root.Find("HubCanvas/HomeRoot/EquipmentPanel/WeaponPreview/SwordShieldSilhouette")?.gameObject;
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

            if (_equipmentButton != null)
            {
                _equipmentButton.onClick.RemoveAllListeners();
                _equipmentButton.interactable = false;
                _equipmentButton.gameObject.SetActive(false);
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

            _wired = _playButton != null && _ctaButton != null;
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

            if (_ctaSubtitle != null)
                _ctaSubtitle.text = OpenWorldTravelCatalog.CurrentDisplayName.ToUpperInvariant();

            string combatProfile = ResolveCombatProfile();
            ApplyGearIdentity(combatProfile);
            if (showStage)
                ApplyShowcaseAppearance();
            ApplyCombatProfile(combatProfile);
        }

        public bool TryCloseForEscape()
        {
            Resolve();
            if (IsTravelMenuOpen)
            {
                _travelMenu!.SetActive(false);
                return true;
            }

            return false;
        }

        private string ResolveCombatProfile()
        {
            if (Application.isPlaying)
            {
                DbConnection? conn = NetworkManager.Instance?.Conn;
                Identity? identity = conn?.Identity;
                if (conn != null && identity.HasValue)
                    return CombatProfileResolver.ResolveForOwner(conn, identity.Value);
            }

            return CombatProfileIds.SwordAndShield;
        }

        private void ApplyGearIdentity(string combatProfile)
        {
            string normalized = CombatProfileIds.Normalize(combatProfile);

            if (_identityNameText != null)
                _identityNameText.text = "GEAR";

            if (_identityMetaText != null)
            {
                _identityMetaText.text = normalized switch
                {
                    CombatProfileIds.ArcherBow => "RANGED | BOW",
                    CombatProfileIds.TwoHandedSword => "MELEE | GREATSWORD",
                    CombatProfileIds.SwordAndShield => "MELEE | SWORD & SHIELD",
                    _ => "COMBAT PROFILE",
                };
            }

            if (_identityBlurbText != null)
            {
                _identityBlurbText.text = normalized switch
                {
                    CombatProfileIds.ArcherBow => "Equipped for bow pressure, spacing, and mobile ranged control.",
                    CombatProfileIds.TwoHandedSword => "Equipped for committed melee pressure and decisive greatsword attacks.",
                    CombatProfileIds.SwordAndShield => "Equipped for shield pressure, counterplay, and controlled melee pacing.",
                    _ => "Combat role is determined by equipped gear.",
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

        private void ApplyShowcaseAppearance()
        {
            if (!Application.isPlaying || _showcaseAvatar == null)
                return;

            CharacterAppearance? appearance = ResolveLocalAppearance();
            string signature = appearance != null
                ? RuntimeAvatarController.SignatureFor(appearance)
                : "STARTER_DEFAULT|HUMAN|MALE|GEAR";
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
                : _showcaseAvatarController.ApplyStarterDefault(out binding, out error);
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

    }
}
