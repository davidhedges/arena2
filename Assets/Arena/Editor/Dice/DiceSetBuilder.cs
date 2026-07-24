#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Arena.Presentation.Dice;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace Arena.Editor.Dice
{
    internal static class DiceSetBuilder
    {
        internal const string DefinitionPath =
            "Assets/Arena/Content/Dice/Generated/Definitions/D20.asset";
        internal const string FontAssetPath =
            "Assets/Arena/Content/Dice/Generated/Typography/Cinzel_DiceNumerals.asset";
        internal const string MeshPath =
            "Assets/Arena/Content/Dice/Generated/Meshes/D20_Resin.asset";
        internal const string NumeralMaterialPath =
            "Assets/Arena/Content/Dice/Generated/Materials/M_DiceNumeral_Ivory.mat";
        internal const string PrefabPath =
            "Assets/Arena/Content/Dice/Generated/Prefabs/D20_Resin.prefab";
        internal const string ResinMaterialPath =
            "Assets/Arena/Content/Dice/Generated/Materials/M_DiceResin_DarkRed.mat";
        internal const string CatalogPath =
            "Assets/Arena/Resources/Dice/DefaultDiceSet.asset";
        internal const string ReviewScenePath =
            "Assets/Arena/Content/Scenes/Authoring/DiceOverlayLab.unity";
        internal const string ResinShaderPath =
            "Assets/Arena/Content/Shaders/Dice/DiceResin.shader";

        private const string CinzelSourcePath = "Assets/Arena/Content/UI/Fonts/Cinzel.ttf";
        private const float BevelFraction = 0.115f;
        private const float RecessFraction = 0.19f;
        private const float RecessDepth = 0.026f;
        private const float LabelLift = 0.003f;
        private const float PresentationScale = 1.15f;

        // Values are distributed across the canonical face order with opposite
        // faces summing to 21. This is stable authoring data, not roll logic.
        private static readonly int[] ValuesByFaceIndex =
        {
            11, 17, 5, 18, 8,
            2, 6, 7, 1, 9,
            3, 16, 4, 10, 13,
            20, 12, 19, 15, 14
        };

        private static readonly int[,] CanonicalFaceIndices =
        {
            { 0, 11, 5 }, { 0, 5, 1 }, { 0, 1, 7 }, { 0, 7, 10 }, { 0, 10, 11 },
            { 1, 5, 9 }, { 5, 11, 4 }, { 11, 10, 2 }, { 10, 7, 6 }, { 7, 1, 8 },
            { 3, 9, 4 }, { 3, 4, 2 }, { 3, 2, 6 }, { 3, 6, 8 }, { 3, 8, 9 },
            { 4, 9, 5 }, { 2, 4, 11 }, { 6, 2, 10 }, { 8, 6, 7 }, { 9, 8, 1 }
        };

        [MenuItem("Arena/Dice/Rebuild and Open D20 Foundation")]
        private static void RebuildAndOpenD20Foundation()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            try
            {
                EnsureFolders();
                AssetDatabase.ImportAsset(ResinShaderPath, ImportAssetOptions.ForceUpdate);

                DeleteGeneratedAssets();

                Mesh mesh = BuildD20Mesh(out List<FaceGeometry> faces);
                AssetDatabase.CreateAsset(mesh, MeshPath);

                TMP_FontAsset fontAsset = BuildNumeralFontAsset();
                Material resinMaterial = BuildResinMaterial();
                Material numeralMaterial = BuildNumeralMaterial(fontAsset);
                GameObject prefab = BuildD20Prefab(mesh, resinMaterial, numeralMaterial, fontAsset, faces);
                DiceDefinition definition = BuildDefinition(prefab, faces);
                BuildCatalog(definition, resinMaterial, numeralMaterial);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                BuildReviewScene(prefab, definition);
                AssetDatabase.SaveAssets();

                bool valid = DiceAuthoringValidator.ValidateD20Foundation(logSuccess: true);
                Debug.Log(valid
                    ? "[DiceSetBuilder] Rebuilt the Phase 1 d20 foundation. The review scene is ready."
                    : "[DiceSetBuilder] Rebuilt the d20 assets, but authoring validation reported errors.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private static void EnsureFolders()
        {
            EnsureFolder("Assets/Arena/Content/Dice/Generated/Definitions");
            EnsureFolder("Assets/Arena/Content/Dice/Generated/Materials");
            EnsureFolder("Assets/Arena/Content/Dice/Generated/Meshes");
            EnsureFolder("Assets/Arena/Content/Dice/Generated/Prefabs");
            EnsureFolder("Assets/Arena/Content/Dice/Generated/Typography");
            EnsureFolder("Assets/Arena/Resources/Dice");
            EnsureFolder("Assets/Arena/Content/Scenes/Authoring");
        }

        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static void DeleteGeneratedAssets()
        {
            string[] paths =
            {
                ReviewScenePath,
                CatalogPath,
                DefinitionPath,
                PrefabPath,
                NumeralMaterialPath,
                ResinMaterialPath,
                FontAssetPath,
                MeshPath
            };

            for (int i = 0; i < paths.Length; i++)
            {
                if (AssetDatabase.LoadMainAssetAtPath(paths[i]) != null)
                    AssetDatabase.DeleteAsset(paths[i]);
            }
        }

        private static Mesh BuildD20Mesh(out List<FaceGeometry> faces)
        {
            Vector3[] vertices = BuildCanonicalVertices();
            faces = BuildFaceGeometry(vertices);

            List<Vector3> meshVertices = new();
            List<Vector3> meshNormals = new();
            List<int> triangles = new();

            for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
            {
                FaceGeometry face = faces[faceIndex];
                for (int edge = 0; edge < 3; edge++)
                {
                    int next = (edge + 1) % 3;
                    AddQuad(
                        meshVertices,
                        meshNormals,
                        triangles,
                        face.Cut[edge],
                        face.Cut[next],
                        face.RecessRim[next],
                        face.RecessRim[edge],
                        face.Normal);

                    Vector3 wallHint =
                        face.Centroid - (face.RecessRim[edge] + face.RecessRim[next]) * 0.5f;
                    AddQuad(
                        meshVertices,
                        meshNormals,
                        triangles,
                        face.RecessRim[edge],
                        face.RecessRim[next],
                        face.RecessFloor[next],
                        face.RecessFloor[edge],
                        wallHint);
                }

                AddTriangle(
                    meshVertices,
                    meshNormals,
                    triangles,
                    face.RecessFloor[0],
                    face.RecessFloor[1],
                    face.RecessFloor[2],
                    face.Normal);
            }

            AddEdgeChamfers(faces, meshVertices, meshNormals, triangles);
            AddVertexCaps(vertices, faces, meshVertices, meshNormals, triangles);

            Mesh mesh = new Mesh
            {
                name = "D20_Resin_BeveledRecessed"
            };
            mesh.SetVertices(meshVertices);
            mesh.SetNormals(meshNormals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3[] BuildCanonicalVertices()
        {
            float phi = (1f + Mathf.Sqrt(5f)) * 0.5f;
            Vector3[] vertices =
            {
                new(-1f, phi, 0f), new(1f, phi, 0f),
                new(-1f, -phi, 0f), new(1f, -phi, 0f),
                new(0f, -1f, phi), new(0f, 1f, phi),
                new(0f, -1f, -phi), new(0f, 1f, -phi),
                new(phi, 0f, -1f), new(phi, 0f, 1f),
                new(-phi, 0f, -1f), new(-phi, 0f, 1f)
            };

            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = vertices[i].normalized;
            return vertices;
        }

        private static List<FaceGeometry> BuildFaceGeometry(Vector3[] vertices)
        {
            List<FaceGeometry> faces = new(CanonicalFaceIndices.GetLength(0));
            for (int faceIndex = 0; faceIndex < CanonicalFaceIndices.GetLength(0); faceIndex++)
            {
                int a = CanonicalFaceIndices[faceIndex, 0];
                int b = CanonicalFaceIndices[faceIndex, 1];
                int c = CanonicalFaceIndices[faceIndex, 2];
                Vector3 centroid = (vertices[a] + vertices[b] + vertices[c]) / 3f;
                Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                if (Vector3.Dot(normal, centroid) < 0f)
                    (b, c) = (c, b);

                centroid = (vertices[a] + vertices[b] + vertices[c]) / 3f;
                normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized;
                Vector3 upright = Vector3.ProjectOnPlane(vertices[a] - centroid, normal).normalized;
                faces.Add(new FaceGeometry(
                    new[] { a, b, c },
                    new[] { vertices[a], vertices[b], vertices[c] },
                    centroid,
                    normal,
                    upright));
            }

            return faces;
        }

        private static void AddEdgeChamfers(
            IReadOnlyList<FaceGeometry> faces,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> triangles)
        {
            Dictionary<EdgeKey, List<FaceEdge>> edges = new();
            for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
            {
                FaceGeometry face = faces[faceIndex];
                for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
                {
                    int next = (edgeIndex + 1) % 3;
                    EdgeKey key = new(face.VertexIndices[edgeIndex], face.VertexIndices[next]);
                    if (!edges.TryGetValue(key, out List<FaceEdge>? entries))
                    {
                        entries = new List<FaceEdge>(2);
                        edges.Add(key, entries);
                    }

                    entries.Add(new FaceEdge(face, edgeIndex, next));
                }
            }

            foreach (KeyValuePair<EdgeKey, List<FaceEdge>> pair in edges)
            {
                if (pair.Value.Count != 2)
                    throw new InvalidOperationException($"D20 edge {pair.Key} is not shared by exactly two faces.");

                FaceEdge first = pair.Value[0];
                FaceEdge second = pair.Value[1];
                Vector3 firstAtA = first.PointForVertex(pair.Key.A);
                Vector3 firstAtB = first.PointForVertex(pair.Key.B);
                Vector3 secondAtA = second.PointForVertex(pair.Key.A);
                Vector3 secondAtB = second.PointForVertex(pair.Key.B);
                Vector3 outwardHint =
                    (firstAtA + firstAtB + secondAtA + secondAtB).normalized;

                AddQuad(
                    vertices,
                    normals,
                    triangles,
                    firstAtA,
                    firstAtB,
                    secondAtB,
                    secondAtA,
                    outwardHint);
            }
        }

        private static void AddVertexCaps(
            IReadOnlyList<Vector3> canonicalVertices,
            IReadOnlyList<FaceGeometry> faces,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> triangles)
        {
            for (int vertexIndex = 0; vertexIndex < canonicalVertices.Count; vertexIndex++)
            {
                List<Vector3> ring = new(5);
                for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
                {
                    FaceGeometry face = faces[faceIndex];
                    for (int corner = 0; corner < 3; corner++)
                    {
                        if (face.VertexIndices[corner] == vertexIndex)
                            ring.Add(face.Cut[corner]);
                    }
                }

                if (ring.Count != 5)
                    throw new InvalidOperationException($"D20 vertex {vertexIndex} has {ring.Count} incident faces.");

                Vector3 outward = canonicalVertices[vertexIndex].normalized;
                Vector3 center = Vector3.zero;
                for (int i = 0; i < ring.Count; i++)
                    center += ring[i];
                center /= ring.Count;

                Vector3 tangent = Vector3.ProjectOnPlane(ring[0] - center, outward).normalized;
                Vector3 bitangent = Vector3.Cross(outward, tangent);
                ring.Sort((left, right) =>
                {
                    float leftAngle = Mathf.Atan2(
                        Vector3.Dot(left - center, bitangent),
                        Vector3.Dot(left - center, tangent));
                    float rightAngle = Mathf.Atan2(
                        Vector3.Dot(right - center, bitangent),
                        Vector3.Dot(right - center, tangent));
                    return leftAngle.CompareTo(rightAngle);
                });

                for (int i = 0; i < ring.Count; i++)
                {
                    AddTriangle(
                        vertices,
                        normals,
                        triangles,
                        center,
                        ring[i],
                        ring[(i + 1) % ring.Count],
                        outward);
                }
            }
        }

        private static void AddQuad(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> triangles,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 d,
            Vector3 facingHint)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            if (Vector3.Dot(normal, facingHint) < 0f)
            {
                (b, d) = (d, b);
                normal = Vector3.Cross(b - a, c - a).normalized;
            }

            int start = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            for (int i = 0; i < 4; i++)
                normals.Add(normal);
            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }

        private static void AddTriangle(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> triangles,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 facingHint)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            if (Vector3.Dot(normal, facingHint) < 0f)
            {
                (b, c) = (c, b);
                normal = -normal;
            }

            int start = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
        }

        private static TMP_FontAsset BuildNumeralFontAsset()
        {
            Font sourceFont = AssetDatabase.LoadAssetAtPath<Font>(CinzelSourcePath);
            if (sourceFont == null)
                throw new InvalidOperationException($"Cinzel font was not found at {CinzelSourcePath}.");

            TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(
                sourceFont,
                90,
                9,
                GlyphRenderMode.SDFAA,
                512,
                512,
                AtlasPopulationMode.Dynamic,
                enableMultiAtlasSupport: false);
            if (fontAsset == null)
                throw new InvalidOperationException("TextMeshPro could not create the Cinzel numeric font asset.");

            fontAsset.name = "Cinzel_DiceNumerals";
            AssetDatabase.CreateAsset(fontAsset, FontAssetPath);

            Texture2D atlas = fontAsset.atlasTextures[0];
            atlas.name = "Cinzel_DiceNumerals Atlas";
            AssetDatabase.AddObjectToAsset(atlas, fontAsset);

            Material fontMaterial = fontAsset.material;
            fontMaterial.name = "Cinzel_DiceNumerals Atlas Material";
            AssetDatabase.AddObjectToAsset(fontMaterial, fontAsset);

            if (!fontAsset.TryAddCharacters("0123456789", out string missingCharacters))
                throw new InvalidOperationException(
                    $"Cinzel numeric font generation missed glyphs: {missingCharacters}");

            fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
            EditorUtility.SetDirty(fontAsset);
            EditorUtility.SetDirty(atlas);
            EditorUtility.SetDirty(fontMaterial);
            AssetDatabase.SaveAssetIfDirty(fontAsset);
            return fontAsset;
        }

        private static Material BuildResinMaterial()
        {
            Shader shader = Shader.Find("Arena/Dice/Resin");
            if (shader == null)
                throw new InvalidOperationException("Arena/Dice/Resin did not import successfully.");

            Material material = new Material(shader)
            {
                name = "M_DiceResin_DarkRed",
                renderQueue = 3000
            };
            material.SetColor("_BaseColor", new Color(0.28f, 0.008f, 0.012f, 0.94f));
            material.SetFloat("_Smoothness", 0.9f);
            material.SetFloat("_Metallic", 0.035f);
            material.SetColor("_FresnelColor", new Color(1.25f, 0.11f, 0.025f, 1f));
            material.SetFloat("_FresnelPower", 3.25f);
            material.SetFloat("_FresnelStrength", 0.58f);
            material.SetFloat("_EdgeOpacity", 0.05f);
            material.SetFloat("_VariationStrength", 0.07f);
            material.SetFloat("_VariationScale", 3.15f);
            material.SetFloat("_ShimmerAmount", 0.01f);
            material.SetFloat("_ShimmerSpeed", 0.16f);
            AssetDatabase.CreateAsset(material, ResinMaterialPath);
            return material;
        }

        private static Material BuildNumeralMaterial(TMP_FontAsset fontAsset)
        {
            Material material = new Material(fontAsset.material)
            {
                name = "M_DiceNumeral_Ivory",
                renderQueue = 3001
            };
            SetColorIfPresent(material, "_FaceColor", new Color(1f, 0.79f, 0.43f, 1f));
            SetColorIfPresent(material, "_OutlineColor", new Color(0.32f, 0.055f, 0.018f, 1f));
            SetFloatIfPresent(material, "_FaceDilate", 0.03f);
            SetFloatIfPresent(material, "_OutlineWidth", 0.1f);
            SetFloatIfPresent(material, "_OutlineSoftness", 0.03f);
            SetFloatIfPresent(material, "_Bevel", 0.2f);
            SetFloatIfPresent(material, "_BevelRoundness", 0.25f);
            SetFloatIfPresent(material, "_CullMode", 2f);
            material.EnableKeyword("OUTLINE_ON");
            AssetDatabase.CreateAsset(material, NumeralMaterialPath);
            return material;
        }

        private static GameObject BuildD20Prefab(
            Mesh mesh,
            Material resinMaterial,
            Material numeralMaterial,
            TMP_FontAsset fontAsset,
            IReadOnlyList<FaceGeometry> faces)
        {
            GameObject root = new GameObject("D20_Resin");
            try
            {
                MeshFilter filter = root.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = root.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = resinMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;

                GameObject labelRoot = new GameObject("FaceLabels");
                labelRoot.transform.SetParent(root.transform, false);

                for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
                {
                    FaceGeometry face = faces[faceIndex];
                    int value = ValuesByFaceIndex[faceIndex];
                    BuildFaceLabel(labelRoot.transform, face, value, fontAsset, numeralMaterial);
                }

                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                if (prefab == null)
                    throw new InvalidOperationException($"Could not save the d20 prefab at {PrefabPath}.");
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void BuildFaceLabel(
            Transform parent,
            FaceGeometry face,
            int value,
            TMP_FontAsset fontAsset,
            Material numeralMaterial)
        {
            GameObject labelObject = new GameObject(
                $"Face_{value:00}",
                typeof(RectTransform),
                typeof(TextMeshPro),
                typeof(DiceFaceLabel));
            RectTransform rect = (RectTransform)labelObject.transform;
            rect.SetParent(parent, false);
            rect.localPosition = face.Centroid - face.Normal * (RecessDepth - LabelLift);
            // World-space TextMeshPro renders its front toward local -Z.
            rect.localRotation = Quaternion.LookRotation(-face.Normal, face.Upright);
            rect.localScale = Vector3.one;
            rect.sizeDelta = new Vector2(0.54f, 0.34f);

            TextMeshPro text = labelObject.GetComponent<TextMeshPro>();
            text.text = value.ToString(CultureInfo.InvariantCulture);
            text.font = fontAsset;
            text.fontSharedMaterial = numeralMaterial;
            text.fontSize = 2.2f;
            text.enableAutoSizing = true;
            text.fontSizeMin = 0.8f;
            text.fontSizeMax = 2.2f;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.margin = Vector4.zero;
            text.color = Color.white;
            text.renderer.sortingOrder = 2;

            DiceFaceLabel label = labelObject.GetComponent<DiceFaceLabel>();
            label.SetAuthoringData(value, face.Normal, face.Upright);
        }

        private static DiceDefinition BuildDefinition(
            GameObject prefab,
            IReadOnlyList<FaceGeometry> geometry)
        {
            DiceFace[] facesByResult = new DiceFace[ValuesByFaceIndex.Length];
            for (int faceIndex = 0; faceIndex < geometry.Count; faceIndex++)
            {
                int value = ValuesByFaceIndex[faceIndex];
                FaceGeometry face = geometry[faceIndex];
                facesByResult[value - 1] = new DiceFace(value, face.Normal, face.Upright);
            }

            DiceDefinition definition = ScriptableObject.CreateInstance<DiceDefinition>();
            definition.name = "D20";
            definition.SetAuthoringData("d20", 20, prefab, PresentationScale, facesByResult);
            AssetDatabase.CreateAsset(definition, DefinitionPath);
            return definition;
        }

        private static void BuildCatalog(
            DiceDefinition definition,
            Material resinMaterial,
            Material numeralMaterial)
        {
            DiceSetCatalog catalog = ScriptableObject.CreateInstance<DiceSetCatalog>();
            catalog.name = "DefaultDiceSet";
            catalog.SetAuthoringData(
                "default",
                new[] { definition },
                resinMaterial,
                numeralMaterial);
            AssetDatabase.CreateAsset(catalog, CatalogPath);
        }

        private static void BuildReviewScene(GameObject prefab, DiceDefinition definition)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.12f, 0.085f, 0.10f);
            RenderSettings.ambientEquatorColor = new Color(0.045f, 0.025f, 0.032f);
            RenderSettings.ambientGroundColor = new Color(0.008f, 0.006f, 0.012f);

            GameObject cameraObject = new GameObject("InspectionCamera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 0f, -4.6f);
            cameraObject.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.007f, 0.004f, 0.009f, 1f);
            camera.fieldOfView = 30f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 30f;
            camera.allowHDR = true;
            camera.allowMSAA = true;

            GameObject dice = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            dice.name = "D20_InspectionTarget";
            dice.transform.position = Vector3.zero;
            dice.transform.localScale = Vector3.one * definition.PresentationScale;

            CreateSpotLight(
                "WarmKey",
                new Vector3(-2.6f, 2.4f, -3.4f),
                new Color(1f, 0.72f, 0.54f),
                12f,
                12f,
                48f);
            CreateSpotLight(
                "CoolFill",
                new Vector3(2.8f, 0.5f, -2.5f),
                new Color(0.40f, 0.56f, 1f),
                5.5f,
                12f,
                52f);
            CreateSpotLight(
                "EmberRim",
                new Vector3(0.6f, 2.2f, 3.2f),
                new Color(1f, 0.12f, 0.025f),
                10f,
                12f,
                46f);

            GameObject reviewObject = new GameObject("D20FoundationReview");
            DiceFoundationReviewController review =
                reviewObject.AddComponent<DiceFoundationReviewController>();
            review.SetAuthoringData(definition, dice.transform, camera, 20);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ReviewScenePath))
                throw new InvalidOperationException($"Could not save the review scene at {ReviewScenePath}.");
            Selection.activeGameObject = dice;
        }

        private static void CreateSpotLight(
            string name,
            Vector3 position,
            Color color,
            float intensity,
            float range,
            float spotAngle)
        {
            GameObject lightObject = new GameObject(name);
            lightObject.transform.position = position;
            lightObject.transform.rotation =
                Quaternion.LookRotation(-position.normalized, Vector3.up);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.spotAngle = spotAngle;
            light.innerSpotAngle = spotAngle * 0.58f;
            light.shadows = LightShadows.Soft;
        }

        private static void SetColorIfPresent(Material material, string property, Color value)
        {
            if (material.HasProperty(property))
                material.SetColor(property, value);
        }

        private static void SetFloatIfPresent(Material material, string property, float value)
        {
            if (material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private sealed class FaceGeometry
        {
            public int[] VertexIndices { get; }
            public Vector3 Centroid { get; }
            public Vector3 Normal { get; }
            public Vector3 Upright { get; }
            public Vector3[] Cut { get; }
            public Vector3[] RecessRim { get; }
            public Vector3[] RecessFloor { get; }

            public FaceGeometry(
                int[] vertexIndices,
                Vector3[] vertices,
                Vector3 centroid,
                Vector3 normal,
                Vector3 upright)
            {
                VertexIndices = vertexIndices;
                Centroid = centroid;
                Normal = normal;
                Upright = upright;
                Cut = new Vector3[3];
                RecessRim = new Vector3[3];
                RecessFloor = new Vector3[3];
                for (int i = 0; i < 3; i++)
                {
                    Cut[i] = Vector3.Lerp(vertices[i], centroid, BevelFraction);
                    RecessRim[i] = Vector3.Lerp(Cut[i], centroid, RecessFraction);
                    RecessFloor[i] = RecessRim[i] - normal * RecessDepth;
                }
            }
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            public int A { get; }
            public int B { get; }

            public EdgeKey(int first, int second)
            {
                A = Mathf.Min(first, second);
                B = Mathf.Max(first, second);
            }

            public bool Equals(EdgeKey other)
            {
                return A == other.A && B == other.B;
            }

            public override bool Equals(object? obj)
            {
                return obj is EdgeKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(A, B);
            }

            public override string ToString()
            {
                return $"{A}-{B}";
            }
        }

        private readonly struct FaceEdge
        {
            private readonly FaceGeometry _face;
            private readonly int _firstCorner;
            private readonly int _secondCorner;

            public FaceEdge(FaceGeometry face, int firstCorner, int secondCorner)
            {
                _face = face;
                _firstCorner = firstCorner;
                _secondCorner = secondCorner;
            }

            public Vector3 PointForVertex(int vertexIndex)
            {
                if (_face.VertexIndices[_firstCorner] == vertexIndex)
                    return _face.Cut[_firstCorner];
                if (_face.VertexIndices[_secondCorner] == vertexIndex)
                    return _face.Cut[_secondCorner];
                throw new InvalidOperationException($"Face edge does not contain vertex {vertexIndex}.");
            }
        }
    }
}
