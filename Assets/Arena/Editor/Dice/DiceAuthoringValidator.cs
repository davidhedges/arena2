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

        [MenuItem("Arena/Dice/Validate D20 Overlay Assets")]
        private static void ValidateFromMenu()
        {
            ValidateD20Foundation(logSuccess: true);
        }

        internal static bool ValidateD20Foundation(bool logSuccess)
        {
            List<string> errors = new();
            DiceDefinition definition =
                AssetDatabase.LoadAssetAtPath<DiceDefinition>(DiceSetBuilder.DefinitionPath);
            DiceSetCatalog catalog =
                AssetDatabase.LoadAssetAtPath<DiceSetCatalog>(DiceSetBuilder.CatalogPath);
            TMP_FontAsset fontAsset =
                AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(DiceSetBuilder.FontAssetPath);
            Material resinMaterial =
                AssetDatabase.LoadAssetAtPath<Material>(DiceSetBuilder.ResinMaterialPath);
            Material numeralMaterial =
                AssetDatabase.LoadAssetAtPath<Material>(DiceSetBuilder.NumeralMaterialPath);

            if (definition == null)
                errors.Add($"Missing definition: {DiceSetBuilder.DefinitionPath}");
            if (catalog == null)
                errors.Add($"Missing catalog: {DiceSetBuilder.CatalogPath}");
            if (fontAsset == null)
                errors.Add($"Missing numeric font: {DiceSetBuilder.FontAssetPath}");
            if (resinMaterial == null)
                errors.Add($"Missing resin material: {DiceSetBuilder.ResinMaterialPath}");
            if (numeralMaterial == null)
                errors.Add($"Missing numeral material: {DiceSetBuilder.NumeralMaterialPath}");

            if (definition != null)
                ValidateDefinition(definition, errors);
            if (catalog != null)
                ValidateCatalog(catalog, definition, resinMaterial, numeralMaterial, errors);
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
                    "[DiceAuthoringValidator] D20 overlay assets are structurally valid: " +
                    "20 unique faces, matching labels, normalized pose data, prefab references, " +
                    "static Cinzel digits, three motion paths, overlay resources, layer, and catalog references.");
            }

            return errors.Count == 0;
        }

        private static void ValidateDefinition(DiceDefinition definition, List<string> errors)
        {
            if (definition.DieId != "d20")
                errors.Add($"Definition die id is '{definition.DieId}', expected 'd20'.");
            if (definition.Sides != 20)
                errors.Add($"Definition has {definition.Sides} sides, expected 20.");
            if (definition.Faces.Count != 20)
                errors.Add($"Definition has {definition.Faces.Count} face entries, expected 20.");
            if (definition.VisualPrefab == null)
            {
                errors.Add("Definition has no visual prefab.");
                return;
            }

            bool[] seen = new bool[21];
            for (int i = 0; i < definition.Faces.Count; i++)
            {
                DiceFace face = definition.Faces[i];
                if (face == null)
                {
                    errors.Add($"Definition face entry {i} is null.");
                    continue;
                }

                if (face.Value < 1 || face.Value > 20)
                    errors.Add($"Definition contains out-of-range face value {face.Value}.");
                else if (seen[face.Value])
                    errors.Add($"Definition contains duplicate face value {face.Value}.");
                else
                    seen[face.Value] = true;

                ValidateFaceVectors(face, errors);
                ValidatePose(face, errors);
            }

            for (int value = 1; value <= 20; value++)
            {
                if (!seen[value])
                    errors.Add($"Definition is missing face value {value}.");
            }

            ValidatePrefab(definition.VisualPrefab, definition, errors);
        }

        private static void ValidateFaceVectors(DiceFace face, List<string> errors)
        {
            if (Mathf.Abs(face.OutwardNormal.magnitude - 1f) > VectorTolerance)
                errors.Add($"Face {face.Value} outward normal is not normalized.");
            if (Mathf.Abs(face.Upright.magnitude - 1f) > VectorTolerance)
                errors.Add($"Face {face.Value} upright vector is not normalized.");
            if (Mathf.Abs(Vector3.Dot(face.OutwardNormal, face.Upright)) > VectorTolerance)
                errors.Add($"Face {face.Value} normal and upright vectors are not orthogonal.");
        }

        private static void ValidatePose(DiceFace face, List<string> errors)
        {
            Vector3 directionTowardCamera = new Vector3(0.24f, 0.18f, -1f).normalized;
            Vector3 cameraUp = new Vector3(0.05f, 1f, 0.08f).normalized;
            Quaternion rotation =
                DicePoseSolver.FaceTowardCamera(face, directionTowardCamera, cameraUp);

            Vector3 rotatedNormal = rotation * face.OutwardNormal;
            if (Vector3.Dot(rotatedNormal.normalized, directionTowardCamera) < PoseTolerance)
                errors.Add($"Face {face.Value} pose does not point at the inspection camera.");

            Vector3 expectedUp = Vector3.ProjectOnPlane(cameraUp, directionTowardCamera).normalized;
            Vector3 rotatedUp = Vector3.ProjectOnPlane(
                rotation * face.Upright,
                directionTowardCamera).normalized;
            if (Vector3.Dot(rotatedUp, expectedUp) < PoseTolerance)
                errors.Add($"Face {face.Value} pose does not finish upright.");
        }

        private static void ValidatePrefab(
            GameObject prefab,
            DiceDefinition definition,
            List<string> errors)
        {
            MeshFilter filter = prefab.GetComponent<MeshFilter>();
            MeshRenderer renderer = prefab.GetComponent<MeshRenderer>();
            if (filter == null || filter.sharedMesh == null)
                errors.Add("D20 prefab is missing its generated mesh.");
            if (renderer == null || renderer.sharedMaterial == null)
                errors.Add("D20 prefab is missing its resin material.");
            if (prefab.GetComponentInChildren<Collider>(true) != null)
                errors.Add("D20 prefab must not contain a collider.");
            if (prefab.GetComponentInChildren<Rigidbody>(true) != null)
                errors.Add("D20 prefab must not contain a rigidbody.");

            DiceFaceLabel[] labels = prefab.GetComponentsInChildren<DiceFaceLabel>(true);
            if (labels.Length != 20)
                errors.Add($"D20 prefab has {labels.Length} face labels, expected 20.");

            bool[] seen = new bool[21];
            for (int i = 0; i < labels.Length; i++)
            {
                DiceFaceLabel label = labels[i];
                if (label.Value < 1 || label.Value > 20)
                {
                    errors.Add($"Prefab contains out-of-range label value {label.Value}.");
                    continue;
                }

                if (seen[label.Value])
                    errors.Add($"Prefab contains duplicate label value {label.Value}.");
                seen[label.Value] = true;

                if (label.Text.text != label.Value.ToString())
                    errors.Add($"Label {label.Value} text is '{label.Text.text}'.");
                if (!definition.TryGetFace(label.Value, out DiceFace face))
                {
                    errors.Add($"Label {label.Value} has no matching definition face.");
                    continue;
                }

                if (Vector3.Dot(label.OutwardNormal, face.OutwardNormal) < PoseTolerance ||
                    Vector3.Dot(label.Upright, face.Upright) < PoseTolerance)
                {
                    errors.Add($"Label {label.Value} metadata differs from its definition pose.");
                }

                Vector3 renderedFront = label.transform.localRotation * Vector3.back;
                if (Vector3.Dot(renderedFront, face.OutwardNormal) < PoseTolerance)
                    errors.Add($"Label {label.Value} rendered front does not match its outward normal.");
            }
        }

        private static void ValidateCatalog(
            DiceSetCatalog catalog,
            DiceDefinition? definition,
            Material? resinMaterial,
            Material? numeralMaterial,
            List<string> errors)
        {
            if (catalog.SetId != "default")
                errors.Add($"Catalog set id is '{catalog.SetId}', expected 'default'.");
            if (catalog.Definitions.Count != 1)
                errors.Add($"Phase 1 catalog has {catalog.Definitions.Count} definitions, expected exactly one.");
            if (definition != null &&
                (!catalog.TryGetDefinition("d20", out DiceDefinition catalogDefinition) ||
                 catalogDefinition != definition))
            {
                errors.Add("Catalog does not directly reference the generated d20 definition.");
            }
            if (catalog.ResinMaterial != resinMaterial)
                errors.Add("Catalog does not reference the generated resin material.");
            if (catalog.NumeralMaterial != numeralMaterial)
                errors.Add("Catalog does not reference the generated numeral material.");

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
                    errors.Add($"Motion profile '{expected.ProfileId}' does not finish at the canonical hold pose.");
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
