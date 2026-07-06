#nullable enable

using Arena.Input;
using Arena.Presentation;
using Arena.UI;
using Arena.World;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Arena.Editor
{
    [InitializeOnLoad]
    internal static class HubSceneAuthoringBuilder
    {
        private const string HubSceneName = "Hub";
        private const string RootName = "HubSceneRoot";
        private const float ShowcaseAvatarHeight = 2.85f;
        private const float ShowcaseLift = 0.92f;

        static HubSceneAuthoringBuilder()
        {
            EditorApplication.delayCall += AutoBuildIfHubOpen;
            EditorSceneManager.sceneOpened += (_, _) => EditorApplication.delayCall += AutoBuildIfHubOpen;
            EditorSceneManager.activeSceneChangedInEditMode += (_, _) => EditorApplication.delayCall += AutoBuildIfHubOpen;
        }

        [MenuItem("Tools/Hub/Rebuild Authored Hub")]
        private static void Rebuild()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !string.Equals(scene.name, HubSceneName, System.StringComparison.Ordinal))
            {
                Debug.LogError("[HubSceneAuthoringBuilder] Open Hub.unity before rebuilding the authored hub.");
                return;
            }

            GameObject? existing = GameObject.Find(RootName);
            if (existing != null)
                Object.DestroyImmediate(existing);

            Build(scene);
        }

        private static void AutoBuildIfHubOpen()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !string.Equals(scene.name, HubSceneName, System.StringComparison.Ordinal))
                return;

            GameObject? existingRoot = GameObject.Find(RootName);
            if (existingRoot != null)
            {
                SyncAuthoredLayout(existingRoot.transform);
                return;
            }

            Build(scene);
        }

        private static void SyncAuthoredLayout(Transform root)
        {
            bool changed = false;
            changed |= SetLocalPosition(root.Find("StageRoot/ShowcaseAnchor"), new Vector3(0f, ShowcaseLift, 0f));
            changed |= SetLocalPosition(root.Find("StageRoot/HeroPlatform"), new Vector3(0f, ShowcaseLift - 0.08f, 0f));
            changed |= SetLocalPosition(root.Find("StageRoot/PlatformRedRing"), new Vector3(0f, ShowcaseLift - 0.14f, 0f));
            changed |= RemoveAuthoredRuntimeAvatarModel(root);

            if (changed)
                EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        }

        private static bool SetLocalPosition(Transform? transform, Vector3 localPosition)
        {
            if (transform == null || transform.localPosition == localPosition)
                return false;

            Undo.RecordObject(transform, "Sync authored Hub layout");
            transform.localPosition = localPosition;
            EditorUtility.SetDirty(transform);
            return true;
        }

        private static void Build(Scene scene)
        {
            RemoveRuntimePlayerObjects(scene);

            GameObject root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Create authored Hub");
            root.AddComponent<HubController>();

            BuildStage(root.transform);
            BuildCanvas(root.transform);
            AttachShowcaseRotator(root.transform);
            EnsureEventSystem();
            ConfigureCamera();
            ConfigureExistingDirectionalLight();

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = root;
        }

        private static void RemoveRuntimePlayerObjects(Scene scene)
        {
            foreach (GameObject rootObject in scene.GetRootGameObjects())
            {
                if (rootObject.name.StartsWith("Player_Dummy", System.StringComparison.Ordinal) ||
                    rootObject.name.StartsWith("Player_Player", System.StringComparison.Ordinal))
                {
                    Object.DestroyImmediate(rootObject);
                }
            }
        }

        private static void BuildCanvas(Transform root)
        {
            RectTransform canvas = CreateRect(root, "HubCanvas");
            SetStretch(canvas);

            Canvas canvasComponent = canvas.gameObject.AddComponent<Canvas>();
            canvasComponent.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasComponent.sortingOrder = 20;

            CanvasScaler scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvas.gameObject.AddComponent<GraphicRaycaster>();
            canvas.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.10f);

            BuildTopBar(canvas);
            BuildHome(canvas);
        }

        private static void BuildTopBar(RectTransform canvas)
        {
            RectTransform topBar = CreateRect(canvas, "TopBar");
            topBar.anchorMin = new Vector2(0f, 1f);
            topBar.anchorMax = new Vector2(1f, 1f);
            topBar.pivot = new Vector2(0.5f, 1f);
            topBar.offsetMin = new Vector2(0f, -82f);
            topBar.offsetMax = Vector2.zero;
            topBar.gameObject.AddComponent<Image>().color = new Color(0.025f, 0.025f, 0.032f, 0.94f);

            RectTransform navRow = CreateRect(topBar, "NavRow");
            SetStretch(navRow, new Vector2(28f, 12f), new Vector2(-28f, -12f));

            CreateNavButton(navRow, "PlayButton", "PLAY", new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(132f, 46f));
            CreateNavButton(navRow, "EquipmentButton", "EQUIPMENT", new Vector2(0f, 0.5f), new Vector2(148f, 0f), new Vector2(160f, 46f));
            CreateNavButton(navRow, "StoreButton", "STORE", new Vector2(0f, 0.5f), new Vector2(326f, 0f), new Vector2(126f, 46f), false);
            CreateNavButton(navRow, "SeasonButton", "SEASON", new Vector2(0f, 0.5f), new Vector2(466f, 0f), new Vector2(140f, 46f), false);
            CreateNavButton(navRow, "ProfileButton", "PROFILE", new Vector2(0f, 0.5f), new Vector2(620f, 0f), new Vector2(134f, 46f), false);

            TMP_Text gold = CreateText(navRow, "GoldText", "2,450", 20, FontStyles.Bold, TextAlignmentOptions.Right, new Color(0.95f, 0.82f, 0.41f));
            SetAnchored(gold.rectTransform, new Vector2(1f, 0.5f), new Vector2(-154f, 0f), new Vector2(112f, 24f));
            TMP_Text crystal = CreateText(navRow, "CrystalText", "14,680", 20, FontStyles.Bold, TextAlignmentOptions.Right, new Color(0.38f, 0.80f, 0.96f));
            SetAnchored(crystal.rectTransform, new Vector2(1f, 0.5f), new Vector2(-26f, 0f), new Vector2(112f, 24f));
            TMP_Text menu = CreateText(navRow, "MenuText", "SET", 12, FontStyles.Bold, TextAlignmentOptions.Center, new Color(0.72f, 0.75f, 0.80f));
            SetAnchored(menu.rectTransform, new Vector2(1f, 0.5f), new Vector2(44f, 0f), new Vector2(34f, 24f));
        }

        private static void BuildHome(RectTransform canvas)
        {
            RectTransform home = CreateRect(canvas, "HomeRoot");
            SetStretch(home);

            RectTransform left = CreatePanel(home, "IdentityPanel", new Vector2(0f, 1f), new Vector2(28f, -132f), new Vector2(360f, 548f), new Vector2(0f, 1f));
            CreateTextAt(left, "GearEyebrow", "GEAR", 13, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(24f, -26f), new Vector2(120f, 18f), new Color(0.88f, 0.56f, 0.48f));
            CreateTextAt(left, "GearName", "GEAR", 34, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(24f, -52f), new Vector2(260f, 40f), Color.white);
            CreateTextAt(left, "GearMeta", "MELEE | GREATSWORD", 15, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(24f, -96f), new Vector2(280f, 24f), new Color(0.72f, 0.75f, 0.80f));
            TMP_Text blurb = CreateTextAt(left, "IdentityBlurb", "A heavy vanguard built around commitment, pressure, and decisive greatsword attacks.", 15, FontStyles.Normal, TextAlignmentOptions.TopLeft, new Vector2(24f, -136f), new Vector2(308f, 74f), new Color(0.88f, 0.89f, 0.91f, 0.92f));
            blurb.textWrappingMode = TextWrappingModes.Normal;
            CreateTextAt(left, "RankLabel", "RANK", 13, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(24f, -232f), new Vector2(80f, 18f), new Color(0.72f, 0.75f, 0.80f));
            CreateTextAt(left, "RankValue", "GOLD II", 24, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(24f, -256f), new Vector2(160f, 28f), new Color(0.96f, 0.73f, 0.26f));
            BuildProgressBar(left, new Vector2(24f, -300f), 66f, 252f);
            CreateTextAt(left, "ChallengeLabel", "DAILY CHALLENGES", 13, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(24f, -350f), new Vector2(180f, 18f), new Color(0.88f, 0.56f, 0.48f));
            BuildChallengeRow(left, 0, "Win 2 Matches", "1/2", "250");
            BuildChallengeRow(left, 1, "Deal 10,000 Damage", "0/10K", "250");
            BuildChallengeRow(left, 2, "Play 3 Matches", "3/3", "250");

            RectTransform viewAll = CreateRect(left, "ViewAllButton");
            SetTopLeft(viewAll, new Vector2(96f, -490f), new Vector2(168f, 38f));
            viewAll.gameObject.AddComponent<Image>().color = new Color(0.12f, 0.13f, 0.15f, 0.95f);
            viewAll.gameObject.AddComponent<Button>();
            CreateTextCentered(viewAll, "Label", "VIEW ALL", 13, FontStyles.Bold, Color.white);

            RectTransform right = CreatePanel(home, "EquipmentPanel", new Vector2(1f, 1f), new Vector2(-28f, -132f), new Vector2(360f, 430f), new Vector2(1f, 1f));
            CreateTextAt(right, "Title", "EQUIPMENT", 18, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(24f, -28f), new Vector2(160f, 24f), Color.white);
            BuildAllocatedStats(right);
            CreateTextAt(right, "ProfessionLabel", "PROFESSION", 12, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(24f, -238f), new Vector2(160f, 18f), new Color(0.72f, 0.75f, 0.80f));
            BuildProfession(right);
            CreateTextAt(right, "TrinketLabel", "TRINKETS", 12, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(24f, -318f), new Vector2(160f, 18f), new Color(0.72f, 0.75f, 0.80f));
            BuildTrinket(right);

            RectTransform ctaPanel = CreatePanel(home, "CtaPanel", new Vector2(1f, 0f), new Vector2(-28f, 28f), new Vector2(360f, 116f), new Vector2(1f, 0f));
            Button play = CreateButton(ctaPanel, "PlayButton", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(316f, 82f), new Color(0.72f, 0.08f, 0.04f, 0.96f), true);
            CreateTextAt(play.transform, "Label", "PLAY", 34, FontStyles.Bold, TextAlignmentOptions.Center, new Vector2(0f, 12f), new Vector2(316f, 36f), Color.white, new Vector2(0.5f, 0.5f));
            CreateTextAt(play.transform, "SubLabel", OpenWorldTravelCatalog.CurrentDisplayName.ToUpperInvariant(), 13, FontStyles.Bold, TextAlignmentOptions.Center, new Vector2(0f, -20f), new Vector2(240f, 18f), new Color(1f, 1f, 1f, 0.72f), new Vector2(0.5f, 0.5f));

            BuildTravelMenu(home);
            BuildCornerButtons(home);
        }

        private static void BuildStage(Transform root)
        {
            Transform stage = CreateWorld(root, "StageRoot");
            Material dark = MaterialFor("HubScene_DarkStone", new Color(0.08f, 0.08f, 0.09f));
            Material mid = MaterialFor("HubScene_MidStone", new Color(0.14f, 0.14f, 0.16f));
            Material red = MaterialFor("HubScene_BannerRed", new Color(0.34f, 0.03f, 0.02f));
            Material ember = MaterialFor("HubScene_Ember", new Color(1.0f, 0.34f, 0.10f), emission: 1.8f);

            Primitive(stage, "HeroPlatform", PrimitiveType.Cylinder, new Vector3(0f, ShowcaseLift - 0.08f, 0f), new Vector3(3.0f, 0.10f, 3.0f), dark);
            Primitive(stage, "PlatformRedRing", PrimitiveType.Cylinder, new Vector3(0f, ShowcaseLift - 0.14f, 0f), new Vector3(3.24f, 0.02f, 3.24f), red);
            Primitive(stage, "RearWall", PrimitiveType.Cube, new Vector3(0f, 2.4f, 5.2f), new Vector3(9.8f, 5.2f, 0.8f), mid);
            Primitive(stage, "RearGateDark", PrimitiveType.Cube, new Vector3(0f, 2.1f, 4.72f), new Vector3(3.4f, 4.2f, 0.32f), dark);
            Primitive(stage, "LeftRuin", PrimitiveType.Cube, new Vector3(-5.9f, 2.0f, 2.7f), new Vector3(1.5f, 4.8f, 3.2f), mid);
            Primitive(stage, "RightRuin", PrimitiveType.Cube, new Vector3(5.9f, 2.0f, 2.7f), new Vector3(1.5f, 4.8f, 3.2f), mid);
            Primitive(stage, "CenterBanner", PrimitiveType.Cube, new Vector3(0f, 3.55f, 4.25f), new Vector3(0.72f, 3.2f, 0.07f), red);
            Primitive(stage, "LeftBanner", PrimitiveType.Cube, new Vector3(-3.0f, 3.15f, 4.35f), new Vector3(0.54f, 2.35f, 0.07f), red);
            Primitive(stage, "RightBanner", PrimitiveType.Cube, new Vector3(3.0f, 3.15f, 4.35f), new Vector3(0.54f, 2.35f, 0.07f), red);

            for (int i = 0; i < 10; i++)
            {
                float side = i % 2 == 0 ? -1f : 1f;
                float depth = 1.8f + (i / 2) * 0.58f;
                Primitive(stage, $"Candle_{i}", PrimitiveType.Cylinder, new Vector3(side * (2.3f + i * 0.08f), 0.16f, depth), new Vector3(0.055f, 0.18f, 0.055f), ember);
            }

            Transform anchor = CreateWorld(stage, "ShowcaseAnchor");
            anchor.localPosition = new Vector3(0f, ShowcaseLift, 0f);
            EnsureAvatar(anchor);

            Light(stage, "KeyLight", LightType.Spot, new Vector3(1.8f, 3.6f, -2.8f), new Vector3(38f, -28f, 0f), new Color(1f, 0.84f, 0.72f), 1.9f, 18f, 62f);
            Light(stage, "ColdFill", LightType.Spot, new Vector3(-2.7f, 3.0f, -2.1f), new Vector3(32f, 36f, 0f), new Color(0.55f, 0.68f, 0.95f), 0.9f, 18f, 70f);
            Light(stage, "RedRim", LightType.Spot, new Vector3(0f, 3.6f, 3.0f), new Vector3(54f, 180f, 0f), new Color(1f, 0.28f, 0.14f), 1.6f, 18f, 58f);
            Light(stage, "EmberLeft", LightType.Point, new Vector3(-2.7f, 0.5f, 2.6f), Vector3.zero, new Color(1f, 0.38f, 0.16f), 2.0f, 4.2f, 0f);
            Light(stage, "EmberRight", LightType.Point, new Vector3(2.7f, 0.5f, 2.6f), Vector3.zero, new Color(1f, 0.38f, 0.16f), 2.0f, 4.2f, 0f);
        }

        private static void BuildProgressBar(RectTransform parent, Vector2 position, float fillWidth, float totalWidth)
        {
            RectTransform bar = CreateRect(parent, "RankProgress");
            SetTopLeft(bar, position, new Vector2(totalWidth, 8f));
            bar.gameObject.AddComponent<Image>().color = new Color(0.12f, 0.13f, 0.15f, 1f);
            RectTransform fill = CreateRect(bar, "Fill");
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(0f, 1f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.sizeDelta = new Vector2(fillWidth, 0f);
            fill.anchoredPosition = Vector2.zero;
            fill.gameObject.AddComponent<Image>().color = new Color(0.94f, 0.65f, 0.22f, 1f);
        }

        private static void BuildChallengeRow(RectTransform parent, int row, string label, string progress, string reward)
        {
            float y = -382f - row * 36f;
            CreateTextAt(parent, $"ChallengeLabel_{row}", label, 12, FontStyles.Normal, TextAlignmentOptions.TopLeft, new Vector2(24f, y), new Vector2(180f, 18f), new Color(0.88f, 0.89f, 0.91f));
            CreateTextAt(parent, $"ChallengeProgress_{row}", progress, 12, FontStyles.Bold, TextAlignmentOptions.TopRight, new Vector2(205f, y), new Vector2(50f, 18f), new Color(0.72f, 0.75f, 0.80f));
            CreateTextAt(parent, $"ChallengeReward_{row}", reward, 12, FontStyles.Bold, TextAlignmentOptions.TopRight, new Vector2(270f, y), new Vector2(40f, 18f), new Color(0.94f, 0.73f, 0.32f));
        }

        private static void BuildAllocatedStats(RectTransform panel)
        {
            RectTransform root = CreateRect(panel, "AllocatedStatsRoot");
            SetTopLeft(root, new Vector2(24f, -78f), new Vector2(300f, 132f));
            CreateTextAt(root, "AllocatedStatsLabel", "ALLOCATED STATS", 12, FontStyles.Bold, TextAlignmentOptions.TopLeft, Vector2.zero, new Vector2(160f, 18f), new Color(0.72f, 0.75f, 0.80f));

            string[] statKinds = { "MIGHT", "INSIGHT", "FINESSE", "QUICKNESS", "FORTITUDE" };
            for (int i = 0; i < statKinds.Length; i++)
            {
                string statKind = statKinds[i];
                RectTransform row = CreateRect(root, $"AllocatedStat_{statKind}");
                SetTopLeft(row, new Vector2(0f, -24f - i * 21f), new Vector2(284f, 20f));
                CreateTextAt(row, "Name", PrettyStatName(statKind).ToUpperInvariant(), 12, FontStyles.Bold, TextAlignmentOptions.Left, new Vector2(0f, 0f), new Vector2(150f, 18f), StatColor(statKind), new Vector2(0f, 0.5f));
                CreateTextAt(row, "Value", "0", 13, FontStyles.Bold, TextAlignmentOptions.Right, new Vector2(228f, 0f), new Vector2(56f, 18f), Color.white, new Vector2(0f, 0.5f));
            }
        }

        private static void BuildProfession(RectTransform panel)
        {
            RectTransform card = CreateRect(panel, "ProfessionCard");
            SetTopLeft(card, new Vector2(24f, -266f), new Vector2(284f, 42f));
            card.gameObject.AddComponent<Image>().color = new Color(0.10f, 0.12f, 0.14f, 0.92f);
            Outline outline = card.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.72f, 0.45f, 0.22f, 0.32f);
            outline.effectDistance = new Vector2(1f, -1f);

            RectTransform sigil = CreateRect(card, "Sigil");
            SetAnchored(sigil, new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(28f, 28f), new Vector2(0f, 0.5f));
            sigil.gameObject.AddComponent<Image>().color = new Color(0.58f, 0.34f, 0.14f, 1f);
            CreateTextCentered(sigil, "Glyph", "E", 16, FontStyles.Bold, Color.white);

            CreateTextAt(card, "ProfessionName", "ENCHANTING", 18, FontStyles.Bold, TextAlignmentOptions.Left, new Vector2(64f, 0f), new Vector2(190f, 22f), new Color(0.96f, 0.82f, 0.46f), new Vector2(0f, 0.5f));
        }

        private static void BuildTrinket(RectTransform panel)
        {
            RectTransform icon = CreateRect(panel, "TrinketIcon");
            SetTopLeft(icon, new Vector2(24f, -350f), new Vector2(42f, 42f));
            icon.gameObject.AddComponent<Image>().color = new Color(0.50f, 0.40f, 0.20f, 1f);
            CreateTextCentered(icon, "Glyph", "T", 16, FontStyles.Bold, Color.white);
            CreateTextAt(panel, "TrinketName", "Charm of Resolve", 16, FontStyles.Bold, TextAlignmentOptions.Left, new Vector2(78f, -362f), new Vector2(210f, 24f), new Color(0.96f, 0.82f, 0.46f));
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

        private static void BuildTravelMenu(RectTransform home)
        {
            RectTransform menu = CreatePanel(home, "TravelMenu", new Vector2(1f, 0f), new Vector2(-28f, 164f), new Vector2(360f, 244f), new Vector2(1f, 0f));
            CreateTextAt(menu, "Title", "SELECT DESTINATION", 16, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(22f, -22f), new Vector2(260f, 22f), Color.white);
            RectTransform list = CreateRect(menu, "DestinationButtons");
            SetTopLeft(list, new Vector2(22f, -64f), new Vector2(316f, 160f));

            OpenWorldTravelCatalog.Destination[] destinations = OpenWorldTravelCatalog.All;
            for (int i = 0; i < destinations.Length; i++)
            {
                Button button = CreateButton(list, $"Travel_{destinations[i].SceneName}", new Vector2(0f, 1f), new Vector2(0f, -i * 38f), new Vector2(316f, 30f), new Color(0.17f, 0.18f, 0.21f, 0.96f), true);
                CreateTextAt(button.transform, "Label", destinations[i].DisplayName, 13, FontStyles.Bold, TextAlignmentOptions.Left, new Vector2(14f, 0f), new Vector2(260f, 18f), Color.white, new Vector2(0f, 0.5f));
            }

            menu.gameObject.SetActive(false);
        }

        private static void BuildCornerButtons(RectTransform home)
        {
            string[] labels = { "CHAT", "TEAM", "OPT" };
            for (int i = 0; i < labels.Length; i++)
            {
                RectTransform button = CreateRect(home, $"CornerButton_{i}");
                SetAnchored(button, new Vector2(0f, 0f), new Vector2(28f + i * 44f, 28f), new Vector2(34f, 34f), new Vector2(0f, 0f));
                button.gameObject.AddComponent<Image>().color = new Color(0.04f, 0.05f, 0.06f, 0.82f);
                button.gameObject.AddComponent<Button>();
                CreateTextCentered(button, "Label", labels[i], 9, FontStyles.Bold, new Color(0.72f, 0.75f, 0.80f));
            }
        }

        private static Button CreateNavButton(RectTransform parent, string name, string text, Vector2 anchor, Vector2 position, Vector2 size, bool interactable = true)
        {
            Button button = CreateButton(parent, name, anchor, position, size, new Color(0f, 0f, 0f, 0f), interactable);
            CreateTextCentered((RectTransform)button.transform, "Label", text, 18, FontStyles.Bold, interactable ? Color.white : new Color(1f, 1f, 1f, 0.35f));
            return button;
        }

        private static Button CreateButton(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, Color fill, bool interactable)
        {
            RectTransform rect = CreateRect(parent, name);
            SetAnchored(rect, anchor, position, size);
            rect.gameObject.AddComponent<Image>().color = fill;
            Button button = rect.gameObject.AddComponent<Button>();
            button.interactable = interactable;
            ColorBlock colors = button.colors;
            colors.normalColor = fill;
            colors.highlightedColor = Color.Lerp(fill, Color.white, 0.10f);
            colors.selectedColor = colors.highlightedColor;
            colors.pressedColor = Color.Lerp(fill, Color.black, 0.20f);
            colors.disabledColor = new Color(0.12f, 0.12f, 0.13f, 0.5f);
            button.colors = colors;
            return button;
        }

        private static RectTransform CreatePanel(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, Vector2 pivot)
        {
            RectTransform panel = CreateRect(parent, name);
            SetAnchored(panel, anchor, position, size, pivot);
            panel.gameObject.AddComponent<Image>().color = new Color(0.035f, 0.04f, 0.05f, 0.84f);
            Outline outline = panel.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.10f);
            outline.effectDistance = new Vector2(1f, -1f);
            return panel;
        }

        private static TMP_Text CreateTextAt(Transform parent, string name, string text, float fontSize, FontStyles style, TextAlignmentOptions alignment, Vector2 position, Vector2 size, Color color, Vector2? anchor = null)
        {
            TMP_Text label = CreateText(parent, name, text, fontSize, style, alignment, color);
            SetAnchored(label.rectTransform, anchor ?? new Vector2(0f, 1f), position, size, anchor ?? new Vector2(0f, 1f));
            return label;
        }

        private static TMP_Text CreateTextCentered(RectTransform parent, string name, string text, float fontSize, FontStyles style, Color color)
        {
            TMP_Text label = CreateText(parent, name, text, fontSize, style, TextAlignmentOptions.Center, color);
            SetStretch(label.rectTransform);
            return label;
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

        private static RectTransform CreateRect(Transform parent, string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static Transform CreateWorld(Transform parent, string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static GameObject Primitive(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            Renderer? renderer = go.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = material;
            return go;
        }

        private static void Light(Transform parent, string name, LightType type, Vector3 position, Vector3 rotation, Color color, float intensity, float range, float spotAngle)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localEulerAngles = rotation;
            Light light = go.AddComponent<Light>();
            light.type = type;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            if (type == LightType.Spot)
                light.spotAngle = spotAngle;
        }

        private static void EnsureAvatar(Transform anchor)
        {
            Transform? existing = anchor.Find("HubShowcaseAvatar");
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            Transform showcase = CreateWorld(anchor, "HubShowcaseAvatar");
            showcase.localPosition = Vector3.zero;
            showcase.localRotation = Quaternion.Euler(0f, 182f, 0f);
            showcase.localScale = Vector3.one;
        }

        private static bool RemoveAuthoredRuntimeAvatarModel(Transform root)
        {
            Transform? staleModel = root.Find("StageRoot/ShowcaseAnchor/HubShowcaseAvatar/RuntimeAvatarModel");
            if (staleModel == null)
                return false;

            Object.DestroyImmediate(staleModel.gameObject);
            return true;
        }

        private static GameObject? LoadShowcaseAvatarPrefab()
        {
            GameObject? runtimeAvatar = RuntimeAvatarPrefabResolver.LoadRuntimePlayerPrefab();
            if (runtimeAvatar != null)
                return runtimeAvatar;

            return Resources.Load<GameObject>(RuntimeAvatarPrefabResolver.FallbackResourceName);
        }

        private static CombatAnimationSet? LoadAnimationSet(string combatProfile)
        {
            return CombatAnimationSetCatalog.Resolve(combatProfile);
        }

        private static void EquipAvatar(GameObject avatar, CombatAnimationSet? animationSet)
        {
            if (animationSet == null)
                return;

            GameObject attachmentHost = avatar.GetComponentInChildren<AvatarWeaponMounts>(true)?.gameObject ?? avatar;
            WeaponAttachmentController attachments =
                attachmentHost.GetComponent<WeaponAttachmentController>() ??
                attachmentHost.AddComponent<WeaponAttachmentController>();
            attachments.Initialize();
            AvatarWeaponMounts? mounts = attachmentHost.GetComponent<AvatarWeaponMounts>() ??
                attachmentHost.GetComponentInChildren<AvatarWeaponMounts>(true);
            if (mounts != null)
                attachments.BindMounts(mounts);
            attachments.ApplyAnimationSet(animationSet);
            attachments.SetInCombat(true);
        }

        private static void PrepareShowcaseModel(GameObject model)
        {
            model.SetActive(true);
            StarterAssetsRuntimeStripper.StripFrom(model);

            Animator? animator = model.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                animator.enabled = true;
                animator.Rebind();
                animator.Update(0f);
            }

            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(false))
            {
                renderer.enabled = true;

                if (renderer is SkinnedMeshRenderer skinnedRenderer)
                    PrepareShowcaseSkinnedRenderer(skinnedRenderer);
            }

            HideEmbeddedPrefabWeapons(model.transform);

            foreach (Collider collider in model.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            CharacterController? characterController = model.GetComponentInChildren<CharacterController>(true);
            if (characterController != null)
                characterController.enabled = false;

            foreach (MonoBehaviour behaviour in model.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null ||
                    behaviour is AvatarWeaponMounts ||
                    behaviour is WeaponAttachmentController)
                {
                    continue;
                }

                string typeName = behaviour.GetType().Name;
                if (typeName.Contains("Controller", System.StringComparison.Ordinal) ||
                    typeName.Contains("Input", System.StringComparison.Ordinal) ||
                    typeName.Contains("Motor", System.StringComparison.Ordinal))
                {
                    behaviour.enabled = false;
                }
            }
        }

        private static void ApplyShowcaseLoopPose(GameObject model, CombatAnimationSet? animationSet)
        {
            AnimationClip? poseClip = GetShowcaseLoopClip(animationSet);
            if (poseClip == null)
                return;

            poseClip.SampleAnimation(model, GetShowcasePoseSampleTime(poseClip));

            Animator? animator = model.GetComponentInChildren<Animator>(true);
            if (animator != null)
                animator.enabled = true;
        }

        private static void PrepareShowcaseSkinnedRenderer(SkinnedMeshRenderer renderer)
        {
            // The showcase retargets old combat clips onto whichever runtime avatar is
            // active. Some imported modular pieces have tight/stale local bounds after
            // retargeting, so Unity can cull the mesh while the bones and weapon mounts
            // remain visible/selectable. This is a hub/editor preview, so favor reliable
            // visibility over renderer culling optimization.
            renderer.updateWhenOffscreen = true;

            Bounds bounds = renderer.localBounds;
            if (bounds.size.x >= 1.5f && bounds.size.y >= 1.5f && bounds.size.z >= 1.5f)
                return;

            renderer.localBounds = new Bounds(new Vector3(0f, 1.25f, 0f), new Vector3(4f, 5f, 4f));
        }

        private static float GetShowcasePoseSampleTime(AnimationClip poseClip)
        {
            return poseClip.length > 0.001f
                ? Mathf.Min(poseClip.length * 0.35f, poseClip.length - 0.001f)
                : 0f;
        }

        private static void ConfigureShowcaseLoop(GameObject model, CombatAnimationSet? animationSet)
        {
            AnimationClip? poseClip = GetShowcaseLoopClip(animationSet);
            Animator? animator = model.GetComponentInChildren<Animator>(true);
            if (poseClip == null || animator == null)
                return;

            HubShowcaseAnimationPlayer player =
                model.GetComponent<HubShowcaseAnimationPlayer>() ??
                model.AddComponent<HubShowcaseAnimationPlayer>();
            player.Configure(animator, poseClip, startTime: GetShowcasePoseSampleTime(poseClip));
        }

        private static AnimationClip? GetShowcaseLoopClip(CombatAnimationSet? animationSet)
        {
            return animationSet?.locomotionIdleCombat ??
                animationSet?.enterCombatIdle ??
                animationSet?.locomotionIdle ??
                animationSet?.DrawWeaponClip;
        }

        private static void SyncShowcaseWeaponMountsFromRuntimeAvatar(GameObject model)
        {
            AvatarWeaponMounts? targetMounts = model.GetComponentInChildren<AvatarWeaponMounts>(true);
            if (targetMounts == null)
                return;

            string? runtimeAvatarPath = RuntimeAvatarPrefabResolver.ResolveRuntimePlayerPrefabAssetPath();
            if (string.IsNullOrWhiteSpace(runtimeAvatarPath) ||
                AssetDatabase.LoadAssetAtPath<GameObject>(runtimeAvatarPath) == null)
            {
                return;
            }

            GameObject? runtimeAvatar = null;
            try
            {
                runtimeAvatar = PrefabUtility.LoadPrefabContents(runtimeAvatarPath);
                AvatarWeaponMounts? sourceMounts = runtimeAvatar.GetComponentInChildren<AvatarWeaponMounts>(true);
                if (sourceMounts == null)
                    return;

                foreach (AvatarWeaponMountDefinition definition in sourceMounts.MountDefinitions)
                {
                    if (definition == null ||
                        string.IsNullOrWhiteSpace(definition.mountId) ||
                        definition.mount == null)
                    {
                        continue;
                    }

                    Transform targetMount = ResolveShowcaseMount(
                        sourceMounts.transform,
                        definition.mount,
                        targetMounts.transform);
                    targetMounts.SetOrReplaceMount(definition.mountId, targetMount);
                }
            }
            finally
            {
                if (runtimeAvatar != null)
                    PrefabUtility.UnloadPrefabContents(runtimeAvatar);
            }
        }

        private static Transform ResolveShowcaseMount(Transform sourceRoot, Transform sourceMount, Transform targetRoot)
        {
            string mountPath = GetRelativePath(sourceRoot, sourceMount);
            Transform? existing = targetRoot.Find(mountPath);
            if (existing != null)
                return existing;

            Transform sourceParent = sourceMount.parent != null ? sourceMount.parent : sourceRoot;
            Transform targetParent = targetRoot;
            if (sourceParent != sourceRoot)
            {
                string parentPath = GetRelativePath(sourceRoot, sourceParent);
                targetParent = targetRoot.Find(parentPath) ?? targetRoot;
            }

            Transform? existingChild = targetParent.Find(sourceMount.name);
            Transform mount = existingChild ?? CreateWorld(targetParent, sourceMount.name);
            mount.localPosition = sourceMount.localPosition;
            mount.localRotation = sourceMount.localRotation;
            mount.localScale = sourceMount.localScale;
            return mount;
        }

        private static string GetRelativePath(Transform root, Transform child)
        {
            if (child == root)
                return string.Empty;

            string path = child.name;
            Transform? cursor = child.parent;
            while (cursor != null && cursor != root)
            {
                path = $"{cursor.name}/{path}";
                cursor = cursor.parent;
            }

            return path;
        }

        private static void HideEmbeddedPrefabWeapons(Transform root)
        {
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (IsEmbeddedPrefabWeaponRenderer(renderer))
                    renderer.enabled = false;
            }
        }

        private static bool IsEmbeddedPrefabWeaponRenderer(MeshRenderer renderer)
        {
            string objectName = renderer.gameObject.name;
            return
                string.Equals(objectName, "Sword01", System.StringComparison.Ordinal) ||
                string.Equals(objectName, "Shield01", System.StringComparison.Ordinal);
        }

        private static void NormalizeModelToShowcase(Transform model, Transform showcase)
        {
            CharacterController? characterController = model.GetComponentInChildren<CharacterController>(true);
            if (characterController != null && characterController.height > 0.001f)
            {
                float controllerScale = ShowcaseAvatarHeight / characterController.height;
                model.localScale *= controllerScale;

                Vector3 worldCenter = characterController.transform.TransformPoint(characterController.center);
                Vector3 worldBottom = characterController.transform.TransformPoint(
                    characterController.center + Vector3.down * (characterController.height * 0.5f));
                Vector3 controllerTargetCenter = showcase.TransformPoint(new Vector3(0f, ShowcaseAvatarHeight * 0.5f, 0f));
                Vector3 targetBottom = showcase.TransformPoint(new Vector3(0f, 0.04f, 0f));

                model.position += new Vector3(
                    controllerTargetCenter.x - worldCenter.x,
                    targetBottom.y - worldBottom.y,
                    controllerTargetCenter.z - worldCenter.z);
                return;
            }

            if (!TryCalculateRendererBounds(model, out Bounds bounds) || bounds.size.y <= 0.001f)
                return;

            float scale = ShowcaseAvatarHeight / bounds.size.y;
            model.localScale *= scale;

            if (!TryCalculateRendererBounds(model, out bounds))
                return;

            Vector3 targetCenter = showcase.TransformPoint(new Vector3(0f, ShowcaseAvatarHeight * 0.5f, 0f));
            model.position += targetCenter - bounds.center;
        }

        private static bool TryCalculateRendererBounds(Transform root, out Bounds bounds)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            bool initialized = false;
            bounds = default;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                if (!initialized)
                {
                    bounds = renderer.bounds;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return initialized;
        }

        private static Material MaterialFor(string name, Color color, float emission = 0f)
        {
            string folder = "Assets/Arena/Content/Art/Generated/Hub/Materials";
            EnsureFolder("Assets", "Art");
            EnsureFolder("Assets/Arena/Content/Art", "Generated");
            EnsureFolder("Assets/Arena/Content/Art/Generated", "Hub");
            EnsureFolder("Assets/Arena/Content/Art/Generated/Hub", "Materials");

            string path = $"{folder}/{name}.mat";
            Material? material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.color = color;
            if (emission > 0f && material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * emission);
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = $"{parent}/{child}";
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, child);
        }

        private static void AttachShowcaseRotator(Transform root)
        {
            Transform? canvas = root.Find("HubCanvas");
            Transform? avatar = root.Find("StageRoot/ShowcaseAnchor/HubShowcaseAvatar");
            if (canvas == null || avatar == null)
                return;

            HubShowcaseRotator rotator =
                canvas.gameObject.GetComponent<HubShowcaseRotator>() ??
                canvas.gameObject.AddComponent<HubShowcaseRotator>();
            rotator.Configure(avatar);
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindAnyObjectByType<EventSystem>() != null)
                return;
            GameObject go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
        }

        private static void ConfigureCamera()
        {
            Camera? camera = Camera.main ?? Object.FindAnyObjectByType<Camera>();
            if (camera == null)
                return;
            camera.transform.position = new Vector3(0f, 2.22f, -7.8f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.85f, 0f) - camera.transform.position);
            camera.fieldOfView = 27f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.035f, 0.045f, 1f);
        }

        private static void ConfigureExistingDirectionalLight()
        {
            Light? light = Object.FindAnyObjectByType<Light>();
            if (light == null)
                return;
            light.type = LightType.Directional;
            light.intensity = 0.35f;
            light.color = new Color(0.45f, 0.48f, 0.55f, 1f);
            light.transform.rotation = Quaternion.Euler(36f, 132f, 0f);
        }

        private static void SetStretch(RectTransform rect)
        {
            SetStretch(rect, Vector2.zero, Vector2.zero);
        }

        private static void SetStretch(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static void SetAnchored(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size, Vector2? pivot = null)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot ?? anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void SetTopLeft(RectTransform rect, Vector2 position, Vector2 size)
        {
            SetAnchored(rect, new Vector2(0f, 1f), position, size, new Vector2(0f, 1f));
        }
    }
}
