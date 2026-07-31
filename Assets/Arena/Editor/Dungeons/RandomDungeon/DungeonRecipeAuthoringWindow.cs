using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.Plastic.Newtonsoft.Json;
using Unity.Plastic.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DungeonLab.Editor
{
    internal sealed class DungeonRecipeAuthoringWindow : EditorWindow
    {
        private const int PreviewSeed = 2026072100;
        private static readonly GUIContent RecipeSourceHeading = new GUIContent(
            "Recipe source of truth",
            "Select the DungeonRecipeAsset whose availability, validation result, and contract overlay this window displays.");
        private static readonly GUIContent RecipeField = new GUIContent(
            "Recipe",
            "The DungeonRecipeAsset to inspect. Detailed zones, ports, motifs, transitions, symmetry pairs, and variations are edited in the asset's normal Inspector.");
        private static readonly GUIContent CreateDisabledHeading = new GUIContent(
            "Create disabled recipe",
            "Creates a new empty recipe asset that is disabled for ordinary dungeon generation until you explicitly enable it.");
        private static readonly GUIContent StableIdField = new GUIContent(
            "Stable ID",
            "The permanent unique recipe ID and asset filename. Use only lowercase letters, digits, and underscores, for example connector_my_room_01.");
        private static readonly GUIContent KindField = new GUIContent(
            "Kind",
            "Connector creates a traversal-focused recipe under Recipes/Rooms. Episode creates an atomic architectural composition under Recipes/Episodes.");
        private static readonly GUIContent CreateButton = new GUIContent(
            "Create explicit disabled asset",
            "Creates the empty recipe asset with schema version 1, content version 1, and Disabled For Generation enabled. It does not add ports, zones, or catalog membership.");
        private static readonly GUIContent SchemaContentField = new GUIContent(
            "Schema / content",
            "The serialized recipe schema version followed by its owner-maintained content version. Schema must be 1 and content must be positive.");
        private static readonly GUIContent DigestField = new GUIContent(
            "Digest",
            "The computed SHA-256 identity of the recipe's current authored content. It is diagnostic evidence, not an approval or availability state.");
        private static readonly GUIContent DisabledField = new GUIContent(
            "Disabled for generation",
            "When checked, ordinary dungeon generation excludes this recipe even if it belongs to the catalog. Valid disabled recipes can still be previewed.");
        private static readonly GUIContent ValidateButton = new GUIContent(
            "Validate",
            "Runs the current schema, structure, variation, and generic-neighbor contract checks without changing the recipe.");
        private static readonly GUIContent GalleryButton = new GUIContent(
            "Build deterministic gallery",
            $"Forces this valid recipe into one compatible existing route slot using fixed seed {PreviewSeed}, runs full-dungeon evidence, and writes diagnostic overlays plus a manifest under DungeonLabReports/Recipes/<recipe-id>/.");
        private static readonly GUIContent ContractOverlayHeading = new GUIContent(
            "Contract overlay",
            "A schematic local-grid view of the recipe's zones, ports, transition reservations, headroom, and protected axis. It is not a rendered room screenshot.");
        private static readonly GUIContent OutputField = new GUIContent(
            "Validation / gallery output",
            "The most recent validation result or deterministic-gallery path and result produced by this window.");

        private DungeonRecipeAsset recipe;
        private string draftId = "connector_new_01";
        private DungeonRecipeKind draftKind = DungeonRecipeKind.Connector;
        private Vector2 scroll;
        private string output = string.Empty;

        [MenuItem("Arena/Dungeons/Recipes/Create Recipe", false, 10)]
        private static void OpenCreateRecipe()
        {
            GetWindow<DungeonRecipeAuthoringWindow>("Dungeon Recipe Authoring").Show();
        }

        // Public so -executeMethod can reach it, for the same reason
        // `ValidateTopologies` is: which recipe a catalog rejects, and why, is
        // evidence a headless run has to be able to produce. A catalog that
        // fails to build takes every seed with it, and the batch report can only
        // say the seed was rejected.
        [MenuItem("Arena/Dungeons/Recipes/Validate Catalog", false, 20)]
        public static void ValidateCatalogMenu()
        {
            Debug.Log(DungeonRecipeAuthoringService.ValidateCatalog());
        }

        [MenuItem("Arena/Dungeons/Recipes/Validate Current Recipe", false, 30)]
        private static void ValidateCurrentMenu()
        {
            DungeonRecipeAsset selected = Selection.activeObject as DungeonRecipeAsset;
            Debug.Log(DungeonRecipeAuthoringService.FormatValidation(
                DungeonRecipeValidator.ValidateContract(selected)));
        }

        [MenuItem("Arena/Dungeons/Recipes/Build Preview Gallery", false, 40)]
        private static void BuildGalleryMenu()
        {
            DungeonRecipeAsset selected = Selection.activeObject as DungeonRecipeAsset;
            if (!DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                    selected,
                    PreviewSeed,
                    out string manifestPath,
                    out string message))
            {
                Debug.LogError(message);
                return;
            }

            Debug.Log($"Dungeon recipe gallery: {manifestPath}\n{message}");
        }

        private void OnSelectionChange()
        {
            if (Selection.activeObject is DungeonRecipeAsset selected)
            {
                recipe = selected;
                Repaint();
            }
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField(RecipeSourceHeading, EditorStyles.boldLabel);
            recipe = (DungeonRecipeAsset)EditorGUILayout.ObjectField(
                RecipeField,
                recipe,
                typeof(DungeonRecipeAsset),
                allowSceneObjects: false);

            if (recipe == null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(CreateDisabledHeading, EditorStyles.boldLabel);
                draftId = EditorGUILayout.TextField(StableIdField, draftId);
                draftKind = (DungeonRecipeKind)EditorGUILayout.EnumPopup(KindField, draftKind);
                if (GUILayout.Button(CreateButton))
                {
                    recipe = DungeonRecipeAuthoringService.CreateDisabled(draftId, draftKind);
                    Selection.activeObject = recipe;
                }

                EditorGUILayout.EndScrollView();
                return;
            }

            string digest = DungeonRecipeValidator.ComputeContentDigest(recipe);
            EditorGUILayout.LabelField(
                SchemaContentField,
                new GUIContent($"{recipe.schemaVersion} / {recipe.contentVersion}"));
            EditorGUILayout.LabelField(DigestField, new GUIContent(digest));
            EditorGUI.BeginChangeCheck();
            bool disabled = EditorGUILayout.Toggle(DisabledField, recipe.disabledForGeneration);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(recipe, "Change recipe availability");
                recipe.disabledForGeneration = disabled;
                EditorUtility.SetDirty(recipe);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(ValidateButton))
                {
                    output = DungeonRecipeAuthoringService.FormatValidation(
                        DungeonRecipeValidator.ValidateContract(recipe));
                }

                if (GUILayout.Button(GalleryButton))
                {
                    DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                        recipe,
                        PreviewSeed,
                        out string path,
                        out string message);
                    output = path + "\n" + message;
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ContractOverlayHeading, EditorStyles.boldLabel);
            Rect previewRect = GUILayoutUtility.GetRect(320f, 320f, GUILayout.ExpandWidth(false));
            DrawContractOverlay(previewRect, recipe);
            EditorGUILayout.HelpBox(
                "Blue: walkable, orange: elevated, green: protected circulation, magenta: protected focal, white: labeled ports, red: transition footprints/landings, cyan: protected route or focal axis. H labels show reserved headroom; exact numeric data remains in the asset inspector and validation report.",
                MessageType.Info);
            EditorGUILayout.LabelField(OutputField);
            EditorGUILayout.TextArea(output ?? string.Empty, GUILayout.MinHeight(100f));
            EditorGUILayout.EndScrollView();
        }

        private static void DrawContractOverlay(Rect rect, DungeonRecipeAsset asset)
        {
            EditorGUI.DrawRect(rect, new Color(0.07f, 0.07f, 0.08f));
            if (!TryGetBounds(asset, out RectInt bounds))
            {
                return;
            }

            float cell = Mathf.Min(rect.width / (bounds.width + 2), rect.height / (bounds.height + 2));
            Vector2 origin = new Vector2(rect.x + cell, rect.yMax - cell);
            foreach (DungeonRecipeZone zone in asset.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                Color color = ZoneColor(zone.kind);
                foreach (Vector2Int point in ZoneCells(zone))
                {
                    DrawCell(rect, origin, cell, bounds, point, color);
                }
            }

            foreach (DungeonRecipeTransition transition in asset.transitions ?? Array.Empty<DungeonRecipeTransition>())
            {
                foreach (Vector2Int point in transition.footprintCells ?? Array.Empty<Vector2Int>())
                    DrawCell(rect, origin, cell, bounds, point, new Color(0.95f, 0.25f, 0.2f));
                foreach (Vector2Int point in transition.lowerLandingCells ?? Array.Empty<Vector2Int>())
                    DrawCell(rect, origin, cell, bounds, point, new Color(0.75f, 0.15f, 0.1f));
                foreach (Vector2Int point in transition.upperLandingCells ?? Array.Empty<Vector2Int>())
                    DrawCell(rect, origin, cell, bounds, point, new Color(1f, 0.55f, 0.25f));
            }

            DrawEditorProtectedAxis(asset, origin, cell, bounds);
            var labelStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 8,
                normal = { textColor = Color.black }
            };
            foreach (DungeonRecipePort port in asset.ports ?? Array.Empty<DungeonRecipePort>())
            {
                DrawCell(rect, origin, cell, bounds, port.cell, Color.white);
                Vector2 center = EditorCellCenter(origin, cell, bounds, port.cell);
                GUI.Label(
                    new Rect(center.x - cell * 0.48f, center.y - cell * 0.42f, cell * 0.96f, cell * 0.84f),
                    $"{port.id}\nH{port.headroomLevels}",
                    labelStyle);
            }

            foreach (DungeonRecipeTransition transition in asset.transitions ?? Array.Empty<DungeonRecipeTransition>())
            {
                Vector2 center = EditorCellCenter(origin, cell, bounds, transition.lowerTransitionCell);
                GUI.Label(
                    new Rect(center.x - cell * 0.45f, center.y - cell * 0.3f, cell * 0.9f, cell * 0.6f),
                    $"H{transition.headroomLevels}",
                    labelStyle);
            }
        }

        private static void DrawEditorProtectedAxis(
            DungeonRecipeAsset asset,
            Vector2 origin,
            float cell,
            RectInt bounds)
        {
            DungeonRecipePort first = null;
            DungeonRecipePort second = null;
            foreach (DungeonRecipePort port in asset.ports ?? Array.Empty<DungeonRecipePort>())
            {
                if (!port.mandatory && !asset.UsesIncidentCardinalSockets) continue;
                if (first == null) first = port;
                else
                {
                    second = port;
                    break;
                }
            }

            if (first == null || second == null) return;
            Handles.BeginGUI();
            Handles.color = Color.cyan;
            Handles.DrawAAPolyLine(
                3f,
                EditorCellCenter(origin, cell, bounds, first.cell),
                EditorCellCenter(origin, cell, bounds, second.cell));
            Handles.EndGUI();
        }

        private static Vector2 EditorCellCenter(
            Vector2 origin,
            float cell,
            RectInt bounds,
            Vector2Int point)
        {
            return new Vector2(
                origin.x + (point.x - bounds.xMin + 0.5f) * cell,
                origin.y - (point.y - bounds.yMin + 0.5f) * cell);
        }

        private static void DrawCell(
            Rect previewRect,
            Vector2 origin,
            float size,
            RectInt bounds,
            Vector2Int point,
            Color color)
        {
            float x = origin.x + (point.x - bounds.xMin) * size;
            float y = origin.y - (point.y - bounds.yMin + 1) * size;
            var cellRect = new Rect(x + 1f, y + 1f, size - 2f, size - 2f);
            if (previewRect.Overlaps(cellRect))
            {
                EditorGUI.DrawRect(cellRect, color);
            }
        }

        private static Color ZoneColor(DungeonRecipeZoneKind kind)
        {
            switch (kind)
            {
                case DungeonRecipeZoneKind.Elevated:
                    return new Color(0.95f, 0.55f, 0.15f);
                case DungeonRecipeZoneKind.ProtectedCirculation:
                    return new Color(0.2f, 0.75f, 0.35f);
                case DungeonRecipeZoneKind.ProtectedFocal:
                    return new Color(0.8f, 0.25f, 0.75f);
                default:
                    return new Color(0.2f, 0.45f, 0.8f);
            }
        }

        internal static bool TryGetBounds(DungeonRecipeAsset asset, out RectInt bounds)
        {
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = int.MinValue;
            int maxY = int.MinValue;
            foreach (DungeonRecipeZone zone in asset?.zones ?? Array.Empty<DungeonRecipeZone>())
            {
                minX = Mathf.Min(minX, zone.offset.x);
                minY = Mathf.Min(minY, zone.offset.y);
                maxX = Mathf.Max(maxX, zone.offset.x + zone.size.x - 1);
                maxY = Mathf.Max(maxY, zone.offset.y + zone.size.y - 1);
            }

            if (minX == int.MaxValue)
            {
                bounds = default;
                return false;
            }

            bounds = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return true;
        }

        internal static IEnumerable<Vector2Int> ZoneCells(DungeonRecipeZone zone)
        {
            for (int x = zone.offset.x; x < zone.offset.x + zone.size.x; x++)
            for (int y = zone.offset.y; y < zone.offset.y + zone.size.y; y++)
                yield return new Vector2Int(x, y);
        }
    }

    public static class DungeonRecipeBatchPreview
    {
        private const string RecipeAssetEnvironmentVariable =
            "ARENA_DUNGEON_RECIPE_PREVIEW_ASSET";
        private const string PreviewSeedEnvironmentVariable =
            "ARENA_DUNGEON_RECIPE_PREVIEW_SEED";
        private const int DefaultPreviewSeed = 2026072100;
        private const string QueuedRequestPath =
            "Temp/DungeonRecipePreviewRequest.txt";
        private const string QueuedResultPath =
            "Temp/DungeonRecipePreviewResult.txt";

        [InitializeOnLoadMethod]
        private static void ScheduleQueuedPreview()
        {
            EditorApplication.delayCall += ProcessQueuedPreview;
        }

        [MenuItem("Arena/Dungeons/Recipes/Process Queued Preview")]
        private static void ProcessQueuedPreviewFromMenu()
        {
            ProcessQueuedPreview();
        }

        private static void ProcessQueuedPreview()
        {
            if (!File.Exists(QueuedRequestPath))
            {
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += ProcessQueuedPreview;
                return;
            }

            string assetPath = File.ReadAllText(QueuedRequestPath).Trim();
            File.Delete(QueuedRequestPath);
            try
            {
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                DungeonRecipeAsset recipe =
                    AssetDatabase.LoadAssetAtPath<DungeonRecipeAsset>(assetPath);
                if (recipe == null)
                {
                    throw new InvalidOperationException(
                        $"No DungeonRecipeAsset exists at '{assetPath}'.");
                }

                if (!DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                        recipe,
                        DefaultPreviewSeed,
                        out string manifestPath,
                        out string message))
                {
                    throw new InvalidOperationException(message);
                }

                using (IDisposable preview =
                       DungeonRecipeCatalogService.BeginAuthoringPreview(
                           recipe,
                           out string previewError))
                {
                    if (preview == null)
                    {
                        throw new InvalidOperationException(previewError);
                    }

                    DungeonLabGenerator.GenerateWithSeed(DefaultPreviewSeed);
                }

                string result =
                    $"PASS\nrecipe={recipe.recipeId}\nmanifest={manifestPath}\n" +
                    $"scenePreview=Generated Dungeon\n{message}";
                File.WriteAllText(QueuedResultPath, result);
                Debug.Log($"Dungeon recipe queued preview passed:\n{result}");
            }
            catch (Exception exception)
            {
                string result = $"FAIL\nasset={assetPath}\n{exception}";
                File.WriteAllText(QueuedResultPath, result);
                Debug.LogError($"Dungeon recipe queued preview failed:\n{result}");
            }
        }

        public static void BuildFromEnvironment()
        {
            string assetPath =
                Environment.GetEnvironmentVariable(RecipeAssetEnvironmentVariable) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new InvalidOperationException(
                    $"{RecipeAssetEnvironmentVariable} must name a DungeonRecipeAsset under Assets/.");
            }

            assetPath = assetPath.Trim().Replace('\\', '/');
            DungeonRecipeAsset recipe = AssetDatabase.LoadAssetAtPath<DungeonRecipeAsset>(assetPath);
            if (recipe == null)
            {
                throw new InvalidOperationException(
                    $"No DungeonRecipeAsset exists at '{assetPath}'.");
            }

            int seed = DefaultPreviewSeed;
            string configuredSeed =
                Environment.GetEnvironmentVariable(PreviewSeedEnvironmentVariable) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(configuredSeed) &&
                !int.TryParse(configuredSeed, out seed))
            {
                throw new InvalidOperationException(
                    $"{PreviewSeedEnvironmentVariable} must be an integer when provided.");
            }

            if (!DungeonRecipeAuthoringService.TryBuildPreviewGallery(
                    recipe,
                    seed,
                    out string manifestPath,
                    out string message))
            {
                throw new InvalidOperationException(
                    $"Dungeon recipe batch preview failed for '{recipe.recipeId}'.\n{message}");
            }

            Debug.Log(
                $"Dungeon recipe batch preview passed for '{recipe.recipeId}': {manifestPath}\n{message}");
        }
    }

    internal static class DungeonRecipeAuthoringService
    {
        private const string ReportRoot = "DungeonLabReports/Recipes";

        internal static DungeonRecipeAsset CreateDisabled(string recipeId, DungeonRecipeKind kind)
        {
            if (string.IsNullOrWhiteSpace(recipeId))
                throw new ArgumentException("A stable recipe ID is required.", nameof(recipeId));

            string folder = kind == DungeonRecipeKind.Episode ? "Episodes" : "Rooms";
            string path =
                $"Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/{folder}/{recipeId}.asset";
            if (AssetDatabase.LoadAssetAtPath<DungeonRecipeAsset>(path) != null)
                throw new InvalidOperationException($"Recipe asset already exists at {path}.");

            var recipe = ScriptableObject.CreateInstance<DungeonRecipeAsset>();
            recipe.recipeId = recipeId.Trim();
            recipe.displayName = recipe.recipeId;
            recipe.kind = kind;
            recipe.schemaVersion = DungeonRecipeAsset.CurrentSchemaVersion;
            recipe.contentVersion = 1;
            recipe.disabledForGeneration = true;
            AssetDatabase.CreateAsset(recipe, path);
            AssetDatabase.SaveAssets();
            return recipe;
        }

        internal static string ValidateCatalog()
        {
            DungeonRecipeCatalog source = AssetDatabase.LoadAssetAtPath<DungeonRecipeCatalog>(
                DungeonRecipeCatalogService.CatalogPath);
            if (source == null)
                return $"FAIL: missing catalog at {DungeonRecipeCatalogService.CatalogPath}";

            int disabled = 0;
            int enabled = 0;
            var invalid = new List<string>();
            foreach (DungeonRecipeAsset recipe in source.recipes ?? Array.Empty<DungeonRecipeAsset>())
            {
                DungeonRecipeValidationResult validation = DungeonRecipeValidator.ValidateContract(recipe);
                if (!validation.Passed) invalid.Add(recipe?.recipeId ?? "<null>");
                if (recipe?.disabledForGeneration == true) disabled++;
                else if (recipe != null) enabled++;
            }

            bool active = DungeonRecipeCatalogService.TryLoadActiveCatalog(
                out ActiveDungeonRecipeCatalog catalog,
                out string rejectionReason);
            return
                $"schema={source.schemaVersion}; planner={DungeonLabGenerator.ActiveRecipePlannerVersion}; " +
                $"digest={(active ? catalog.digest : "<invalid>")}; cataloged={source.recipes?.Length ?? 0}; " +
                $"enabled={enabled}; disabled={disabled}; invalid=[{string.Join(",", invalid)}]; " +
                $"status={(active ? "PASS" : rejectionReason)}";
        }

        internal static string FormatValidation(DungeonRecipeValidationResult validation)
        {
            if (validation == null)
                return "FAIL: validation result was null";

            var lines = new List<string>
            {
                $"passed={validation.Passed}",
                $"schema={validation.LayerPassed(DungeonRecipeValidationLayer.Schema)}",
                $"structure={validation.LayerPassed(DungeonRecipeValidationLayer.Structure)}",
                $"variation={validation.LayerPassed(DungeonRecipeValidationLayer.Variation)}",
                $"neighbor={validation.LayerPassed(DungeonRecipeValidationLayer.Neighbor)}",
                $"fullDungeon={validation.LayerPassed(DungeonRecipeValidationLayer.FullDungeon)}"
            };
            foreach (DungeonRecipeValidationFinding finding in validation.Findings)
            {
                lines.Add($"{finding.layer}:{finding.code}:{finding.message}");
            }

            return string.Join("\n", lines);
        }

        internal static bool TryBuildPreviewGallery(
            DungeonRecipeAsset recipe,
            int seed,
            out string manifestPath,
            out string message)
        {
            manifestPath = string.Empty;
            message = string.Empty;
            DungeonRecipeValidationResult contract = DungeonRecipeValidator.ValidateContract(recipe);
            if (!contract.Passed)
            {
                message = FormatValidation(contract);
                return false;
            }

            using (IDisposable preview = DungeonRecipeCatalogService.BeginAuthoringPreview(
                       recipe,
                       out string previewError))
            {
                if (preview == null)
                {
                    message = previewError;
                    return false;
                }

                if (!DungeonLabGenerator.TryBuildRecipeFullDungeonEvidence(
                        recipe.recipeId,
                        seed,
                        out DungeonRecipeFullDungeonEvidence evidence,
                        out message))
                {
                    return false;
                }

                DungeonRecipeValidationResult all =
                    DungeonRecipeValidator.ValidateWithFullDungeonEvidence(recipe, evidence);
                if (!all.Passed)
                {
                    message = FormatValidation(all);
                    return false;
                }

                manifestPath = BuildGalleryFiles(recipe, seed, evidence);
                message = FormatValidation(all);
                return true;
            }
        }

        private static string BuildGalleryFiles(
            DungeonRecipeAsset recipe,
            int seed,
            DungeonRecipeFullDungeonEvidence evidence)
        {
            string directory = Path.Combine(ReportRoot, recipe.recipeId);
            Directory.CreateDirectory(directory);
            foreach (string stalePreview in Directory.GetFiles(directory, "*.png"))
            {
                File.Delete(stalePreview);
            }
            var entries = new JArray();
            var imageHashes = new List<string>();
            string[] viewKinds = { "contract", "top_down", "player_height", "below_floor" };
            string[] variationIds = recipe.variations.Length == 0
                ? new[] { "structural" }
                : Array.ConvertAll(recipe.variations, variation => variation.id);
            bool[] mirrorStates = recipe.allowMirror
                ? new[] { false, true }
                : new[] { false };
            foreach (int turn in recipe.legalQuarterTurns)
            foreach (bool mirrored in mirrorStates)
            foreach (string variationId in variationIds)
            foreach (string viewKind in viewKinds)
            {
                string fileName = $"{viewKind}_r{turn}_m{(mirrored ? 1 : 0)}_{variationId}.png";
                byte[] png = RenderOverlayPng(recipe, turn, mirrored, variationId, viewKind);
                File.WriteAllBytes(Path.Combine(directory, fileName), png);
                string imageHash = Sha256(png);
                imageHashes.Add(imageHash);
                entries.Add(new JObject
                {
                    ["kind"] = viewKind,
                    ["quarterTurns"] = turn,
                    ["mirrored"] = mirrored,
                    ["variation"] = variationId,
                    ["path"] = fileName,
                    ["sha256"] = imageHash,
                    ["overlays"] = new JArray(
                        "grid", "zones", "ports", "landings", "headroom", "protected-axis")
                });
            }

            foreach (DungeonRecipePort port in recipe.ports)
            {
                entries.Add(new JObject
                {
                    ["kind"] = "neighbor",
                    ["portId"] = port.id,
                    ["neighbor"] = "generic-corridor",
                    ["minimumElevationContext"] = port.relativeLevel,
                    ["maximumElevationContext"] = port.relativeLevel,
                    ["passed"] = true
                });
            }

            var manifest = new JObject
            {
                ["recipeId"] = recipe.recipeId,
                ["schemaVersion"] = recipe.schemaVersion,
                ["contentVersion"] = recipe.contentVersion,
                ["contentDigest"] = DungeonRecipeValidator.ComputeContentDigest(recipe),
                ["previewSeed"] = seed,
                ["previewContext"] = new JObject
                {
                    ["forced"] = evidence.forcedAuthoringPreview,
                    ["forcedRecipeId"] = recipe.recipeId,
                    ["topologyId"] = evidence.previewTopologyId,
                    ["recipeSlotId"] = evidence.previewRecipeSlotId,
                    ["routeNodeId"] = evidence.previewRouteNodeId
                },
                ["entries"] = entries,
                ["fullDungeon"] = new JObject
                {
                    ["placedAtomically"] = evidence.placedAtomically,
                    ["boundMandatoryPorts"] = evidence.boundMandatoryPortCount,
                    ["resolvedTransitions"] = evidence.resolvedTransitionCount,
                    ["canonicalPlan"] = evidence.canonicalPlanValid,
                    ["renderer"] = evidence.rendererValid,
                    ["abyssSupport"] = evidence.abyssSupportValid,
                    ["collision"] = evidence.collisionValid
                },
                ["galleryHash"] = Sha256(Encoding.UTF8.GetBytes(string.Join("\n", imageHashes)))
            };
            string path = Path.Combine(directory, "gallery_manifest.json");
            File.WriteAllText(path, manifest.ToString(Formatting.Indented));
            return path;
        }

        private static byte[] RenderOverlayPng(
            DungeonRecipeAsset recipe,
            int quarterTurns,
            bool mirrored,
            string variationId,
            string viewKind)
        {
            const int Size = 384;
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, mipChain: false, linear: true)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point
            };
            Color background = viewKind == "below_floor"
                ? new Color(0.025f, 0.025f, 0.04f, 1f)
                : new Color(0.06f, 0.06f, 0.07f, 1f);
            Color[] pixels = new Color[Size * Size];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = background;
            texture.SetPixels(pixels);

            if (DungeonRecipeAuthoringWindow.TryGetBounds(recipe, out RectInt bounds))
            {
                int cellSize = Mathf.Max(8, Mathf.Min(48, 300 / Mathf.Max(bounds.width, bounds.height)));
                Vector2Int center = new Vector2Int(Size / 2, Size / 2);
                foreach (DungeonRecipeZone zone in recipe.zones)
                {
                    Color color = GalleryZoneColor(zone.kind, variationId, viewKind);
                    foreach (Vector2Int local in DungeonRecipeAuthoringWindow.ZoneCells(zone))
                    {
                        Vector2Int rotated = TransformPreviewCell(local, quarterTurns, mirrored);
                        FillCell(texture, rotated, center, cellSize, color);
                        if (viewKind == "player_height" && zone.relativeLevel > 0)
                            DrawVerticalRiser(texture, rotated, center, cellSize, zone.relativeLevel);
                        if (viewKind == "below_floor" && zone.relativeLevel > 0)
                            DrawSupportMarker(texture, rotated, center, cellSize);
                    }
                }

                foreach (DungeonRecipeTransition transition in recipe.transitions)
                {
                    foreach (Vector2Int local in transition.footprintCells)
                        FillCell(texture, TransformPreviewCell(local, quarterTurns, mirrored), center, cellSize, Color.red);
                    foreach (Vector2Int local in transition.lowerLandingCells)
                        FillInset(texture, TransformPreviewCell(local, quarterTurns, mirrored), center, cellSize, new Color(0.65f, 0.1f, 0.1f));
                    foreach (Vector2Int local in transition.upperLandingCells)
                        FillInset(texture, TransformPreviewCell(local, quarterTurns, mirrored), center, cellSize, new Color(1f, 0.6f, 0.2f));
                    DrawHeadroomTicks(
                        texture,
                        TransformPreviewCell(transition.lowerTransitionCell, quarterTurns, mirrored),
                        center,
                        cellSize,
                        transition.headroomLevels);
                }

                DrawProtectedAxis(texture, recipe, quarterTurns, mirrored, center, cellSize);
                foreach (DungeonRecipePort port in recipe.ports)
                {
                    Vector2Int cell = TransformPreviewCell(port.cell, quarterTurns, mirrored);
                    Vector2Int outward = TransformPreviewCell(port.outwardDirection, quarterTurns, mirrored);
                    FillInset(texture, cell, center, cellSize, Color.white);
                    DrawPortArrow(texture, cell, outward, center, cellSize);
                    DrawHeadroomTicks(texture, cell, center, cellSize, port.headroomLevels);
                }

                DrawVariationGlyph(texture, recipe, variationId, quarterTurns, mirrored, center, cellSize);
            }

            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            return png;
        }

        private static Color GalleryZoneColor(
            DungeonRecipeZoneKind kind,
            string variationId,
            string viewKind)
        {
            float variationBias = (StableHash(variationId) & 7) * 0.015f;
            float viewBias = (StableHash(viewKind) & 3) * 0.02f;
            switch (kind)
            {
                case DungeonRecipeZoneKind.Elevated:
                    return new Color(0.9f, 0.45f + variationBias, 0.1f + viewBias, 1f);
                case DungeonRecipeZoneKind.ProtectedCirculation:
                    return new Color(0.1f, 0.7f, 0.25f + variationBias, 1f);
                case DungeonRecipeZoneKind.ProtectedFocal:
                    return new Color(0.75f + variationBias, 0.15f, 0.7f, 1f);
                default:
                    return new Color(0.1f + viewBias, 0.35f, 0.75f, 1f);
            }
        }

        private static Vector2Int Rotate(Vector2Int cell, int quarterTurns)
        {
            Vector2Int result = cell;
            for (int turn = 0; turn < quarterTurns; turn++)
                result = new Vector2Int(result.y, -result.x);
            return result;
        }

        private static void FillCell(
            Texture2D texture,
            Vector2Int cell,
            Vector2Int center,
            int size,
            Color color)
        {
            int xMin = center.x + cell.x * size - size / 2;
            int yMin = center.y + cell.y * size - size / 2;
            for (int y = yMin + 1; y < yMin + size - 1; y++)
            for (int x = xMin + 1; x < xMin + size - 1; x++)
                if (x >= 0 && y >= 0 && x < texture.width && y < texture.height)
                    texture.SetPixel(x, y, color);
        }

        private static void FillInset(
            Texture2D texture,
            Vector2Int cell,
            Vector2Int center,
            int size,
            Color color)
        {
            int inset = Mathf.Max(2, size / 4);
            int xMin = center.x + cell.x * size - size / 2 + inset;
            int yMin = center.y + cell.y * size - size / 2 + inset;
            for (int y = yMin; y < yMin + size - inset * 2; y++)
            for (int x = xMin; x < xMin + size - inset * 2; x++)
                if (x >= 0 && y >= 0 && x < texture.width && y < texture.height)
                    texture.SetPixel(x, y, color);
        }

        private static void DrawProtectedAxis(
            Texture2D texture,
            DungeonRecipeAsset recipe,
            int quarterTurns,
            bool mirrored,
            Vector2Int center,
            int cellSize)
        {
            DungeonRecipePort first = null;
            DungeonRecipePort second = null;
            foreach (DungeonRecipePort port in recipe.ports ?? Array.Empty<DungeonRecipePort>())
            {
                if (!port.mandatory && !recipe.UsesIncidentCardinalSockets) continue;
                if (first == null) first = port;
                else
                {
                    second = port;
                    break;
                }
            }

            if (first == null || second == null) return;
            Vector2Int from = CellPixel(TransformPreviewCell(first.cell, quarterTurns, mirrored), center, cellSize);
            Vector2Int to = CellPixel(TransformPreviewCell(second.cell, quarterTurns, mirrored), center, cellSize);
            DrawLine(texture, from, to, new Color(0.1f, 0.95f, 0.95f, 1f), 1);
        }

        private static void DrawPortArrow(
            Texture2D texture,
            Vector2Int cell,
            Vector2Int outward,
            Vector2Int center,
            int cellSize)
        {
            Vector2Int start = CellPixel(cell, center, cellSize);
            Vector2Int end = start + outward * Mathf.Max(5, cellSize / 2);
            DrawLine(texture, start, end, Color.white, 2);
        }

        private static void DrawHeadroomTicks(
            Texture2D texture,
            Vector2Int cell,
            Vector2Int center,
            int cellSize,
            int headroomLevels)
        {
            Vector2Int origin = CellPixel(cell, center, cellSize) +
                new Vector2Int(-cellSize / 3, cellSize / 3);
            int ticks = Mathf.Clamp(headroomLevels, 1, 6);
            for (int index = 0; index < ticks; index++)
            {
                Vector2Int tick = origin + new Vector2Int(index * 3, 0);
                DrawLine(texture, tick, tick + Vector2Int.up * 4, Color.cyan, 1);
            }
        }

        private static void DrawVerticalRiser(
            Texture2D texture,
            Vector2Int cell,
            Vector2Int center,
            int cellSize,
            int relativeLevel)
        {
            Vector2Int origin = CellPixel(cell, center, cellSize) -
                new Vector2Int(cellSize / 3, cellSize / 3);
            DrawLine(
                texture,
                origin,
                origin + Vector2Int.down * Mathf.Clamp(relativeLevel * 7, 4, 28),
                new Color(1f, 0.8f, 0.25f, 1f),
                2);
        }

        private static void DrawSupportMarker(
            Texture2D texture,
            Vector2Int cell,
            Vector2Int center,
            int cellSize)
        {
            Vector2Int origin = CellPixel(cell, center, cellSize);
            int radius = Mathf.Max(3, cellSize / 5);
            Color support = new Color(0.35f, 0.8f, 1f, 1f);
            DrawLine(texture, origin + new Vector2Int(-radius, -radius), origin + new Vector2Int(radius, radius), support, 2);
            DrawLine(texture, origin + new Vector2Int(-radius, radius), origin + new Vector2Int(radius, -radius), support, 2);
        }

        private static void DrawVariationGlyph(
            Texture2D texture,
            DungeonRecipeAsset recipe,
            string variationId,
            int quarterTurns,
            bool mirrored,
            Vector2Int center,
            int cellSize)
        {
            if (recipe.variations == null || recipe.variations.Length == 0) return;
            int variationIndex = Array.FindIndex(
                recipe.variations,
                variation => string.Equals(variation.id, variationId, StringComparison.Ordinal));
            if (variationIndex < 0) return;

            DungeonRecipeZone focal = Array.Find(
                recipe.zones,
                zone => zone.kind == DungeonRecipeZoneKind.ProtectedFocal);
            if (focal == null) return;
            Vector2Int local = focal.offset + new Vector2Int(
                (focal.size.x - 1) / 2,
                (focal.size.y - 1) / 2);
            Vector2Int origin = CellPixel(TransformPreviewCell(local, quarterTurns, mirrored), center, cellSize);
            int radius = Mathf.Max(5, cellSize / 3);
            Color color = new Color(1f, 0.95f, 0.25f, 1f);
            if ((variationIndex & 1) == 0)
            {
                DrawLine(texture, origin + Vector2Int.up * radius, origin + Vector2Int.right * radius, color, 3);
                DrawLine(texture, origin + Vector2Int.right * radius, origin + Vector2Int.down * radius, color, 3);
                DrawLine(texture, origin + Vector2Int.down * radius, origin + Vector2Int.left * radius, color, 3);
                DrawLine(texture, origin + Vector2Int.left * radius, origin + Vector2Int.up * radius, color, 3);
            }
            else
            {
                const int Segments = 20;
                Vector2Int previous = origin + Vector2Int.right * radius;
                for (int segment = 1; segment <= Segments; segment++)
                {
                    float radians = segment * Mathf.PI * 2f / Segments;
                    var next = origin + new Vector2Int(
                        Mathf.RoundToInt(Mathf.Cos(radians) * radius),
                        Mathf.RoundToInt(Mathf.Sin(radians) * radius));
                    DrawLine(texture, previous, next, color, 3);
                    previous = next;
                }
            }
        }

        private static Vector2Int TransformPreviewCell(Vector2Int cell, int quarterTurns, bool mirrored)
        {
            if (mirrored) cell.y = -cell.y;
            return Rotate(cell, quarterTurns);
        }

        private static Vector2Int CellPixel(Vector2Int cell, Vector2Int center, int cellSize)
        {
            return center + cell * cellSize;
        }

        private static void DrawLine(
            Texture2D texture,
            Vector2Int from,
            Vector2Int to,
            Color color,
            int thickness)
        {
            int dx = Mathf.Abs(to.x - from.x);
            int dy = Mathf.Abs(to.y - from.y);
            int sx = from.x < to.x ? 1 : -1;
            int sy = from.y < to.y ? 1 : -1;
            int error = dx - dy;
            while (true)
            {
                int radius = Mathf.Max(0, thickness - 1);
                for (int y = from.y - radius; y <= from.y + radius; y++)
                for (int x = from.x - radius; x <= from.x + radius; x++)
                    if (x >= 0 && y >= 0 && x < texture.width && y < texture.height)
                        texture.SetPixel(x, y, color);

                if (from == to) break;
                int doubled = error * 2;
                if (doubled > -dy)
                {
                    error -= dy;
                    from.x += sx;
                }
                if (doubled < dx)
                {
                    error += dx;
                    from.y += sy;
                }
            }
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 17;
                foreach (char character in value ?? string.Empty) hash = hash * 31 + character;
                return hash;
            }
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes ?? Array.Empty<byte>());
                var result = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) result.Append(value.ToString("x2"));
                return result.ToString();
            }
        }
    }
}
