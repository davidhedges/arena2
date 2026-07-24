#nullable enable
using System.Collections.Generic;
using Arena.Presentation.Dice;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UIElements;

namespace Arena.Editor.Dice
{
    internal static class DiceAuthoringValidator
    {
        private const float VectorTolerance = 0.001f;
        private const float PoseTolerance = 0.9995f;

        [MenuItem("Arena/Dice/Validate Complete Dice Overlay Assets")]
        private static void ValidateFromMenu()
        {
            ValidateCompleteSet(logSuccess: true);
        }

        internal static bool ValidateD20Foundation(bool logSuccess)
        {
            return ValidateCompleteSet(logSuccess);
        }

        internal static bool ValidateCompleteSet(bool logSuccess)
        {
            List<string> errors = new();
            DiceSetCatalog catalog =
                AssetDatabase.LoadAssetAtPath<DiceSetCatalog>(DiceSetBuilder.CatalogPath);
            TMP_FontAsset fontAsset =
                AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(DiceSetBuilder.FontAssetPath);
            Material resinMaterial =
                AssetDatabase.LoadAssetAtPath<Material>(DiceSetBuilder.ResinMaterialPath);
            Material numeralMaterial =
                AssetDatabase.LoadAssetAtPath<Material>(DiceSetBuilder.NumeralMaterialPath);

            List<DiceDefinition> definitions = new(DiceSetBuilder.SupportedDieIds.Length);
            for (int i = 0; i < DiceSetBuilder.SupportedDieIds.Length; i++)
            {
                string dieId = DiceSetBuilder.SupportedDieIds[i];
                string path = DiceSetBuilder.DefinitionPathFor(dieId);
                DiceDefinition definition = AssetDatabase.LoadAssetAtPath<DiceDefinition>(path);
                if (definition == null)
                {
                    errors.Add($"Missing definition: {path}");
                    continue;
                }

                definitions.Add(definition);
                ValidateDefinition(definition, dieId, ExpectedSides(dieId), errors);
            }

            if (catalog == null)
                errors.Add($"Missing catalog: {DiceSetBuilder.CatalogPath}");
            if (fontAsset == null)
                errors.Add($"Missing numeric font: {DiceSetBuilder.FontAssetPath}");
            if (resinMaterial == null)
                errors.Add($"Missing resin material: {DiceSetBuilder.ResinMaterialPath}");
            if (numeralMaterial == null)
                errors.Add($"Missing numeral material: {DiceSetBuilder.NumeralMaterialPath}");

            if (catalog != null)
                ValidateCatalog(catalog, definitions, resinMaterial, numeralMaterial, errors);
            if (fontAsset != null)
                ValidateFont(fontAsset, errors);
            if (resinMaterial != null &&
                (resinMaterial.shader == null || resinMaterial.shader.name != "Arena/Dice/Resin"))
            {
                errors.Add("The resin material does not use Arena/Dice/Resin.");
            }
            ValidateOverlayResources(errors);

            for (int i = 0; i < errors.Count; i++)
                Debug.LogError($"[DiceAuthoringValidator] {errors[i]}");

            if (errors.Count == 0 && logSuccess)
            {
                Debug.Log(
                    "[DiceAuthoringValidator] Complete dice overlay assets are structurally valid: " +
                    "six unique all-result definitions, matching labels and prefabs, normalized upright poses, " +
                    "shared static Cinzel digits, three motion paths, overlay resources, layer, and catalog references.");
            }

            return errors.Count == 0;
        }

        private static int ExpectedSides(string dieId)
        {
            return dieId switch
            {
                "d4" => 4,
                "d6" => 6,
                "d8" => 8,
                "d10" => 10,
                "d12" => 12,
                "d20" => 20,
                _ => 0
            };
        }

        private static void ValidateDefinition(
            DiceDefinition definition,
            string expectedDieId,
            int expectedSides,
            List<string> errors)
        {
            if (definition.DieId != expectedDieId)
            {
                errors.Add(
                    $"{expectedDieId} definition die id is '{definition.DieId}'.");
            }
            if (definition.Sides != expectedSides)
            {
                errors.Add(
                    $"{expectedDieId} definition has {definition.Sides} sides, expected {expectedSides}.");
            }
            if (definition.Faces.Count != expectedSides)
            {
                errors.Add(
                    $"{expectedDieId} definition has {definition.Faces.Count} face entries, expected {expectedSides}.");
            }
            if (definition.VisualPrefab == null)
            {
                errors.Add($"{expectedDieId} definition has no visual prefab.");
                return;
            }

            bool[] seen = new bool[expectedSides + 1];
            for (int i = 0; i < definition.Faces.Count; i++)
            {
                DiceFace face = definition.Faces[i];
                if (face == null)
                {
                    errors.Add($"{expectedDieId} definition face entry {i} is null.");
                    continue;
                }

                if (face.Value < 1 || face.Value > expectedSides)
                {
                    errors.Add(
                        $"{expectedDieId} contains out-of-range face value {face.Value}.");
                }
                else if (seen[face.Value])
                {
                    errors.Add($"{expectedDieId} contains duplicate face value {face.Value}.");
                }
                else
                {
                    seen[face.Value] = true;
                }

                ValidateFaceVectors(expectedDieId, face, errors);
                ValidatePose(expectedDieId, face, errors);
            }

            for (int value = 1; value <= expectedSides; value++)
            {
                if (!seen[value])
                    errors.Add($"{expectedDieId} is missing face value {value}.");
            }

            ValidatePrefab(definition.VisualPrefab, definition, errors);
        }

        private static void ValidateFaceVectors(
            string dieId,
            DiceFace face,
            List<string> errors)
        {
            if (Mathf.Abs(face.OutwardNormal.magnitude - 1f) > VectorTolerance)
                errors.Add($"{dieId} face {face.Value} outward normal is not normalized.");
            if (Mathf.Abs(face.Upright.magnitude - 1f) > VectorTolerance)
                errors.Add($"{dieId} face {face.Value} upright vector is not normalized.");
            if (Mathf.Abs(Vector3.Dot(face.OutwardNormal, face.Upright)) > VectorTolerance)
                errors.Add($"{dieId} face {face.Value} normal and upright vectors are not orthogonal.");
        }

        private static void ValidatePose(
            string dieId,
            DiceFace face,
            List<string> errors)
        {
            Vector3 directionTowardCamera = new Vector3(0.24f, 0.18f, -1f).normalized;
            Vector3 cameraUp = new Vector3(0.05f, 1f, 0.08f).normalized;
            Quaternion rotation =
                DicePoseSolver.FaceTowardCamera(face, directionTowardCamera, cameraUp);

            Vector3 rotatedNormal = rotation * face.OutwardNormal;
            if (Vector3.Dot(rotatedNormal.normalized, directionTowardCamera) < PoseTolerance)
                errors.Add($"{dieId} face {face.Value} pose does not point at the inspection camera.");

            Vector3 expectedUp = Vector3.ProjectOnPlane(cameraUp, directionTowardCamera).normalized;
            Vector3 rotatedUp = Vector3.ProjectOnPlane(
                rotation * face.Upright,
                directionTowardCamera).normalized;
            if (Vector3.Dot(rotatedUp, expectedUp) < PoseTolerance)
                errors.Add($"{dieId} face {face.Value} pose does not finish upright.");
        }

        private static void ValidatePrefab(
            GameObject prefab,
            DiceDefinition definition,
            List<string> errors)
        {
            string dieId = definition.DieId;
            MeshFilter filter = prefab.GetComponent<MeshFilter>();
            MeshRenderer renderer = prefab.GetComponent<MeshRenderer>();
            if (filter == null || filter.sharedMesh == null)
                errors.Add($"{dieId} prefab is missing its generated mesh.");
            if (renderer == null || renderer.sharedMaterial == null)
                errors.Add($"{dieId} prefab is missing its resin material.");
            if (prefab.GetComponentInChildren<Collider>(true) != null)
                errors.Add($"{dieId} prefab must not contain a collider.");
            if (prefab.GetComponentInChildren<Rigidbody>(true) != null)
                errors.Add($"{dieId} prefab must not contain a rigidbody.");

            DiceFaceLabel[] labels = prefab.GetComponentsInChildren<DiceFaceLabel>(true);
            if (labels.Length != definition.Sides)
            {
                errors.Add(
                    $"{dieId} prefab has {labels.Length} face labels, expected {definition.Sides}.");
            }

            bool[] seen = new bool[definition.Sides + 1];
            for (int i = 0; i < labels.Length; i++)
            {
                DiceFaceLabel label = labels[i];
                if (label.Value < 1 || label.Value > definition.Sides)
                {
                    errors.Add($"{dieId} prefab contains out-of-range label value {label.Value}.");
                    continue;
                }

                if (seen[label.Value])
                    errors.Add($"{dieId} prefab contains duplicate label value {label.Value}.");
                seen[label.Value] = true;

                if (label.Text.text != label.Value.ToString())
                    errors.Add($"{dieId} label {label.Value} text is '{label.Text.text}'.");
                if (!definition.TryGetFace(label.Value, out DiceFace face))
                {
                    errors.Add($"{dieId} label {label.Value} has no matching definition face.");
                    continue;
                }

                if (Vector3.Dot(label.OutwardNormal, face.OutwardNormal) < PoseTolerance ||
                    Vector3.Dot(label.Upright, face.Upright) < PoseTolerance)
                {
                    errors.Add($"{dieId} label {label.Value} metadata differs from its definition pose.");
                }

                Vector3 renderedFront = label.transform.localRotation * Vector3.back;
                if (Vector3.Dot(renderedFront, face.OutwardNormal) < PoseTolerance)
                    errors.Add($"{dieId} label {label.Value} front does not match its outward normal.");
            }
        }

        private static void ValidateCatalog(
            DiceSetCatalog catalog,
            IReadOnlyList<DiceDefinition> definitions,
            Material? resinMaterial,
            Material? numeralMaterial,
            List<string> errors)
        {
            if (catalog.SetId != "default")
                errors.Add($"Catalog set id is '{catalog.SetId}', expected 'default'.");
            if (catalog.Definitions.Count != DiceSetBuilder.SupportedDieIds.Length)
            {
                errors.Add(
                    $"Catalog has {catalog.Definitions.Count} definitions, " +
                    $"expected {DiceSetBuilder.SupportedDieIds.Length}.");
            }

            for (int i = 0; i < definitions.Count; i++)
            {
                DiceDefinition definition = definitions[i];
                if (!catalog.TryGetDefinition(definition.DieId, out DiceDefinition catalogDefinition) ||
                    catalogDefinition != definition)
                {
                    errors.Add(
                        $"Catalog does not directly reference the generated {definition.DieId} definition.");
                }
            }

            if (catalog.ResinMaterial != resinMaterial)
                errors.Add("Catalog does not reference the generated resin material.");
            if (catalog.NumeralMaterial != numeralMaterial)
                errors.Add("Catalog does not reference the generated numeral material.");
            ValidateMotionProfiles(catalog, errors);
        }

        private static void ValidateMotionProfiles(
            DiceSetCatalog catalog,
            List<string> errors)
        {
            if (catalog.MotionProfiles.Count != DiceSetBuilder.MotionProfilePaths.Length)
            {
                errors.Add(
                    $"Catalog has {catalog.MotionProfiles.Count} motion profiles, " +
                    $"expected {DiceSetBuilder.MotionProfilePaths.Length}.");
                return;
            }

            HashSet<string> profileIds = new();
            for (int i = 0; i < DiceSetBuilder.MotionProfilePaths.Length; i++)
            {
                DiceMotionProfile expected =
                    AssetDatabase.LoadAssetAtPath<DiceMotionProfile>(DiceSetBuilder.MotionProfilePaths[i]);
                if (expected == null)
                {
                    errors.Add($"Missing motion profile: {DiceSetBuilder.MotionProfilePaths[i]}");
                    continue;
                }
                if (catalog.MotionProfiles[i] != expected)
                    errors.Add($"Catalog motion profile {i} does not reference {expected.name}.");
                if (!profileIds.Add(expected.ProfileId))
                    errors.Add($"Duplicate motion profile id '{expected.ProfileId}'.");
                if (expected.TotalDuration < 1f || expected.TotalDuration > 2.2f)
                    errors.Add($"Motion profile '{expected.ProfileId}' has an invalid duration.");
                if (expected.SettleStart <= 0f || expected.SettleStart >= 1f)
                    errors.Add($"Motion profile '{expected.ProfileId}' has an invalid settle start.");
                if (Mathf.Abs(expected.EvaluateHorizontal(1f)) > 0.0001f ||
                    Mathf.Abs(expected.EvaluateVertical(1f)) > 0.0001f ||
                    Mathf.Abs(expected.EvaluateDepth(1f)) > 0.0001f ||
                    Mathf.Abs(expected.EvaluateScale(1f) - 1f) > 0.0001f)
                {
                    errors.Add(
                        $"Motion profile '{expected.ProfileId}' does not finish at the canonical hold pose.");
                }
            }
        }

        private static void ValidateFont(TMP_FontAsset fontAsset, List<string> errors)
        {
            if (fontAsset.atlasPopulationMode != AtlasPopulationMode.Static)
                errors.Add("Cinzel dice font must be static after numeric glyph generation.");
            for (char digit = '0'; digit <= '9'; digit++)
            {
                if (!fontAsset.characterLookupTable.ContainsKey(digit))
                    errors.Add($"Cinzel dice font is missing digit '{digit}'.");
            }
        }

        private static void ValidateOverlayResources(List<string> errors)
        {
            if (LayerMask.NameToLayer("DiceOverlay3D") < 0)
                errors.Add("ProjectSettings/TagManager.asset is missing the DiceOverlay3D layer.");

            VisualTreeAsset overlay = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Assets/Arena/Resources/UI/Toolkit/DiceOverlay.uxml");
            if (overlay == null)
                errors.Add("Missing DiceOverlay.uxml.");
            StyleSheet style = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Assets/Arena/Resources/UI/Toolkit/DiceOverlay.uss");
            if (style == null)
                errors.Add("Missing DiceOverlay.uss.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(DiceSetBuilder.ReviewScenePath) == null)
                errors.Add($"Missing review scene: {DiceSetBuilder.ReviewScenePath}");
        }
    }
}
