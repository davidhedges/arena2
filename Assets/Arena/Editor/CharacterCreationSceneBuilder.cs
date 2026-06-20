#nullable enable

using Arena.Presentation.Appearance;
using Arena.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Arena.EditorTools
{
    public static class CharacterCreationSceneBuilder
    {
        private const string ScenePath = "Assets/Arena/Content/Scenes/CharacterCreation.unity";
        private const string RootName = "CharacterCreationRoot";

        [MenuItem("Arena/Appearance/Rebuild Character Creation Scene")]
        public static void RebuildCharacterCreationScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Build(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        public static void RebuildCharacterCreationSceneBatch()
        {
            RebuildCharacterCreationScene();
        }

        private static void Build(Scene scene)
        {
            GameObject root = new(RootName);
            root.AddComponent<CharacterCreationController>();
            BuildStage(root.transform);
            BuildCanvas(root.transform);
            EnsureEventSystem();
            ConfigureCamera();
            ConfigureLight();
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = root;
        }

        private static void BuildStage(Transform root)
        {
            Transform stage = new GameObject("StageRoot").transform;
            stage.SetParent(root, false);

            Transform anchor = new GameObject("PreviewAnchor").transform;
            anchor.SetParent(stage, false);
            anchor.localPosition = Vector3.zero;
            anchor.gameObject.AddComponent<CharacterAvatarAssembler>();

            Material floorMaterial = new(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            floorMaterial.name = "CharacterCreation_Floor";
            floorMaterial.color = new Color(0.09f, 0.10f, 0.11f);

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            floor.name = "PreviewPlatform";
            floor.transform.SetParent(stage, false);
            floor.transform.localPosition = new Vector3(0f, -0.08f, 0f);
            floor.transform.localScale = new Vector3(2.8f, 0.10f, 2.8f);
            floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;
        }

        private static void BuildCanvas(Transform root)
        {
            RectTransform canvas = CreateRect(root, "CharacterCreationCanvas");
            SetStretch(canvas);

            Canvas canvasComponent = canvas.gameObject.AddComponent<Canvas>();
            canvasComponent.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasComponent.sortingOrder = 20;

            CanvasScaler scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvas.gameObject.AddComponent<GraphicRaycaster>();

            RectTransform left = CreatePanel(canvas, "LeftPanel", new Vector2(0f, 1f), new Vector2(36f, -112f), new Vector2(390f, 460f), new Vector2(0f, 1f));
            CreateText(left, "Title", "CHARACTER CREATION", 26, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(22f, -24f), new Vector2(330f, 34f), Color.white);
            CreateText(left, "RaceSexLabel", "RACE / SEX", 12, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(22f, -92f), new Vector2(160f, 18f), new Color(0.72f, 0.75f, 0.80f));
            CreateText(left, "RaceSexValue", "HUMAN / MALE", 18, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(22f, -116f), new Vector2(250f, 28f), Color.white);
            CreateText(left, "GearLabel", "STARTING GEAR", 12, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(22f, -170f), new Vector2(160f, 18f), new Color(0.72f, 0.75f, 0.80f));
            CreateText(left, "GearValue", "SWORD & SHIELD", 22, FontStyles.Bold, TextAlignmentOptions.TopLeft, new Vector2(22f, -196f), new Vector2(260f, 34f), Color.white);

            RectTransform buttons = CreateRect(left, "GearSummary");
            SetTopLeft(buttons, new Vector2(22f, -260f), new Vector2(330f, 180f));
            CreateButton(buttons, "MainHandSummary", "SWORD & SHIELD", new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(330f, 48f));
            CreateButton(buttons, "OffHandSummary", "ONE-HAND SWORD", new Vector2(0f, 1f), new Vector2(0f, -62f), new Vector2(330f, 48f));
            CreateButton(buttons, "SpellSlotSummary", "SHIELD", new Vector2(0f, 1f), new Vector2(0f, -124f), new Vector2(330f, 48f));

            RectTransform bottom = CreatePanel(canvas, "BottomBar", new Vector2(0.5f, 0f), new Vector2(0f, 32f), new Vector2(620f, 92f), new Vector2(0.5f, 0f));
            CreateText(bottom, "StatusText", string.Empty, 13, FontStyles.Bold, TextAlignmentOptions.Left, new Vector2(22f, 0f), new Vector2(300f, 42f), new Color(0.95f, 0.62f, 0.55f), new Vector2(0f, 0.5f));
            CreateButton(bottom, "CreateButton", "CREATE", new Vector2(1f, 0.5f), new Vector2(-150f, 0f), new Vector2(260f, 54f), new Color(0.72f, 0.08f, 0.04f, 0.96f));
        }

        private static Button CreateButton(Transform parent, string name, string label, Vector2 anchor, Vector2 position, Vector2 size, Color? fill = null)
        {
            RectTransform rect = CreateRect(parent, name);
            SetAnchored(rect, anchor, position, size);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = fill ?? new Color(0.16f, 0.17f, 0.20f, 0.96f);
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            CreateText(rect, "Label", label, 16, FontStyles.Bold, TextAlignmentOptions.Center, Vector2.zero, size, Color.white, new Vector2(0.5f, 0.5f));
            return button;
        }

        private static RectTransform CreatePanel(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, Vector2 pivot)
        {
            RectTransform rect = CreateRect(parent, name);
            SetAnchored(rect, anchor, position, size, pivot);
            rect.gameObject.AddComponent<Image>().color = new Color(0.045f, 0.048f, 0.056f, 0.94f);
            return rect;
        }

        private static TMP_Text CreateText(Transform parent, string name, string text, float fontSize, FontStyles style, TextAlignmentOptions alignment, Vector2 position, Vector2 size, Color color, Vector2? anchor = null)
        {
            RectTransform rect = CreateRect(parent, name);
            SetAnchored(rect, anchor ?? new Vector2(0f, 1f), position, size);
            TMP_Text label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.alignment = alignment;
            label.color = color;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.Normal;
            return label;
        }

        private static RectTransform CreateRect(Transform parent, string name)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void SetStretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetTopLeft(RectTransform rect, Vector2 position, Vector2 size)
        {
            SetAnchored(rect, new Vector2(0f, 1f), position, size, new Vector2(0f, 1f));
        }

        private static void SetAnchored(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size, Vector2? pivot = null)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot ?? anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<EventSystem>() != null)
                return;

            GameObject eventSystem = new("EventSystem");
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<InputSystemUIInputModule>();
        }

        private static void ConfigureCamera()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject cameraObject = new("Main Camera");
                cameraObject.tag = "MainCamera";
                camera = cameraObject.AddComponent<Camera>();
            }

            camera.transform.position = new Vector3(0f, 1.55f, -4.8f);
            camera.transform.rotation = Quaternion.Euler(7f, 0f, 0f);
            camera.fieldOfView = 35f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.027f, 0.032f);
        }

        private static void ConfigureLight()
        {
            GameObject lightObject = new("Key Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(42f, -28f, 0f);
            light.color = new Color(1f, 0.86f, 0.72f);
            light.intensity = 1.6f;
        }
    }
}
