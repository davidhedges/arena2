#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Arena.Debugging;
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

        internal static readonly string[] SupportedDieIds =
        {
            "d4", "d6", "d8", "d10", "d12", "d20"
        };

        internal static readonly string[] MotionProfilePaths =
        {
            "Assets/Arena/Content/Dice/Motion/D20_Crescent.asset",
            "Assets/Arena/Content/Dice/Motion/D20_Crosswind.asset",
            "Assets/Arena/Content/Dice/Motion/D20_Helix.asset"
        };

        private const string CinzelSourcePath = "Assets/Arena/Content/UI/Fonts/Cinzel.ttf";
        private const float BevelFraction = 0.115f;
        private const float RecessFraction = 0.19f;
        private const float RecessDepth = 0.026f;
        private const float LabelLift = 0.003f;

        // Stable d20 authoring order with opposite faces summing to 21.
        private static readonly int[] D20ValuesByFaceIndex =
        {
            11, 17, 5, 18, 8,
            2, 6, 7, 1, 9,
            3, 16, 4, 10, 13,
            20, 12, 19, 15, 14
        };

        private static readonly int[][] IcosahedronFaces =
        {
            new[] { 0, 11, 5 }, new[] { 0, 5, 1 }, new[] { 0, 1, 7 },
            new[] { 0, 7, 10 }, new[] { 0, 10, 11 }, new[] { 1, 5, 9 },
            new[] { 5, 11, 4 }, new[] { 11, 10, 2 }, new[] { 10, 7, 6 },
            new[] { 7, 1, 8 }, new[] { 3, 9, 4 }, new[] { 3, 4, 2 },
            new[] { 3, 2, 6 }, new[] { 3, 6, 8 }, new[] { 3, 8, 9 },
            new[] { 4, 9, 5 }, new[] { 2, 4, 11 }, new[] { 6, 2, 10 },
            new[] { 8, 6, 7 }, new[] { 9, 8, 1 }
        };

        [MenuItem("Arena/Dice/Rebuild and Open Complete Dice Overlay Lab")]
        private static void RebuildAndOpenCompleteDiceOverlayLab()
        {
            RebuildCompleteDiceOverlayLab(promptToSave: true);
        }

        internal static void RebuildCompleteDiceOverlayLab(bool promptToSave)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[DiceSetBuilder] Exit Play Mode before rebuilding dice assets.");
                return;
            }
            if (promptToSave && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            if (!promptToSave)
            {
                Scene currentScene = SceneManager.GetActiveScene();
                if (currentScene.isDirty &&
                    !string.Equals(
                        currentScene.path,
                        ReviewScenePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Refusing to replace dirty scene '{currentScene.path}' during automatic dice generation.");
                }
            }

            try
            {
                IReadOnlyList<DieBlueprint> blueprints = BuildBlueprints();
                EnsureFolders();
                AssetDatabase.ImportAsset(ResinShaderPath, ImportAssetOptions.ForceUpdate);
                DeleteGeneratedAssets(blueprints);

                TMP_FontAsset fontAsset = BuildNumeralFontAsset();
                Material resinMaterial = BuildResinMaterial();
                Material numeralMaterial = BuildNumeralMaterial(fontAsset);
                List<DiceDefinition> definitions = new(blueprints.Count);
                for (int i = 0; i < blueprints.Count; i++)
                {
                    DieBlueprint blueprint = blueprints[i];
                    Mesh mesh = BuildDieMesh(blueprint, out List<FaceGeometry> geometry);
                    AssetDatabase.CreateAsset(mesh, MeshPathFor(blueprint));
                    GameObject prefab = BuildPrefab(
                        blueprint,
                        mesh,
                        resinMaterial,
                        numeralMaterial,
                        fontAsset,
                        geometry);
                    definitions.Add(BuildDefinition(blueprint, prefab, geometry));
                }

                List<DiceMotionProfile> motionProfiles = BuildMotionProfiles();
                BuildCatalog(definitions, motionProfiles, resinMaterial, numeralMaterial);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                BuildReviewScene();
                AssetDatabase.SaveAssets();

                bool valid = DiceAuthoringValidator.ValidateCompleteSet(logSuccess: true);
                Debug.Log(valid
                    ? "[DiceSetBuilder] Rebuilt the complete d4-d20 overlay lab. Enter Play Mode to review it."
                    : "[DiceSetBuilder] Rebuilt the complete dice set, but authoring validation reported errors.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        internal static string DefinitionPathFor(string dieId)
        {
            return $"Assets/Arena/Content/Dice/Generated/Definitions/{dieId.ToUpperInvariant()}.asset";
        }

        private static string DefinitionPathFor(DieBlueprint blueprint)
        {
            return $"Assets/Arena/Content/Dice/Generated/Definitions/{blueprint.AssetName}.asset";
        }

        private static string MeshPathFor(DieBlueprint blueprint)
        {
            return $"Assets/Arena/Content/Dice/Generated/Meshes/{blueprint.AssetName}_Resin.asset";
        }

        private static string PrefabPathFor(DieBlueprint blueprint)
        {
            return $"Assets/Arena/Content/Dice/Generated/Prefabs/{blueprint.AssetName}_Resin.prefab";
        }

        private static void EnsureFolders()
        {
            EnsureFolder("Assets/Arena/Content/Dice/Generated/Definitions");
            EnsureFolder("Assets/Arena/Content/Dice/Generated/Materials");
            EnsureFolder("Assets/Arena/Content/Dice/Generated/Meshes");
            EnsureFolder("Assets/Arena/Content/Dice/Generated/Prefabs");
            EnsureFolder("Assets/Arena/Content/Dice/Generated/Typography");
            EnsureFolder("Assets/Arena/Content/Dice/Motion");
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

        private static void DeleteGeneratedAssets(IReadOnlyList<DieBlueprint> blueprints)
        {
            List<string> paths = new()
            {
                ReviewScenePath,
                CatalogPath,
                NumeralMaterialPath,
                ResinMaterialPath,
                FontAssetPath
            };
            paths.AddRange(MotionProfilePaths);
            for (int i = 0; i < blueprints.Count; i++)
            {
                paths.Add(DefinitionPathFor(blueprints[i]));
                paths.Add(MeshPathFor(blueprints[i]));
                paths.Add(PrefabPathFor(blueprints[i]));
            }

            for (int i = 0; i < paths.Count; i++)
            {
                if (AssetDatabase.LoadMainAssetAtPath(paths[i]) != null)
                    AssetDatabase.DeleteAsset(paths[i]);
            }
        }

        private static IReadOnlyList<DieBlueprint> BuildBlueprints()
        {
            Vector3[] icosahedronVertices = BuildIcosahedronVertices();
            Polyhedron dodecahedron = BuildDual(icosahedronVertices, IcosahedronFaces);
            Polyhedron trapezohedron = BuildPentagonalTrapezohedron();

            return new[]
            {
                new DieBlueprint(
                    "d4",
                    "D4",
                    4,
                    1.27f,
                    new Vector2(0.50f, 0.32f),
                    2.15f,
                    NormalizeToUnitRadius(new[]
                    {
                        new Vector3(1f, 1f, 1f),
                        new Vector3(-1f, -1f, 1f),
                        new Vector3(-1f, 1f, -1f),
                        new Vector3(1f, -1f, -1f)
                    }),
                    new[]
                    {
                        new[] { 0, 2, 1 },
                        new[] { 0, 1, 3 },
                        new[] { 0, 3, 2 },
                        new[] { 1, 2, 3 }
                    },
                    SequentialValues(4)),
                new DieBlueprint(
                    "d6",
                    "D6",
                    6,
                    1.18f,
                    new Vector2(0.72f, 0.48f),
                    2.35f,
                    NormalizeToUnitRadius(new[]
                    {
                        new Vector3(-1f, -1f, -1f),
                        new Vector3(1f, -1f, -1f),
                        new Vector3(1f, 1f, -1f),
                        new Vector3(-1f, 1f, -1f),
                        new Vector3(-1f, -1f, 1f),
                        new Vector3(1f, -1f, 1f),
                        new Vector3(1f, 1f, 1f),
                        new Vector3(-1f, 1f, 1f)
                    }),
                    new[]
                    {
                        new[] { 4, 5, 6, 7 },
                        new[] { 1, 0, 3, 2 },
                        new[] { 5, 1, 2, 6 },
                        new[] { 0, 4, 7, 3 },
                        new[] { 7, 6, 2, 3 },
                        new[] { 0, 1, 5, 4 }
                    },
                    new[] { 1, 6, 3, 4, 2, 5 }),
                new DieBlueprint(
                    "d8",
                    "D8",
                    8,
                    1.20f,
                    new Vector2(0.56f, 0.36f),
                    2.2f,
                    new[]
                    {
                        Vector3.right,
                        Vector3.left,
                        Vector3.up,
                        Vector3.down,
                        Vector3.forward,
                        Vector3.back
                    },
                    new[]
                    {
                        new[] { 2, 0, 4 },
                        new[] { 2, 4, 1 },
                        new[] { 2, 1, 5 },
                        new[] { 2, 5, 0 },
                        new[] { 3, 4, 0 },
                        new[] { 3, 1, 4 },
                        new[] { 3, 5, 1 },
                        new[] { 3, 0, 5 }
                    },
                    SequentialValues(8)),
                new DieBlueprint(
                    "d10",
                    "D10",
                    10,
                    1.18f,
                    new Vector2(0.51f, 0.33f),
                    2.05f,
                    trapezohedron.Vertices,
                    trapezohedron.Faces,
                    SequentialValues(10)),
                new DieBlueprint(
                    "d12",
                    "D12",
                    12,
                    1.16f,
                    new Vector2(0.58f, 0.38f),
                    2.15f,
                    dodecahedron.Vertices,
                    dodecahedron.Faces,
                    SequentialValues(12)),
                new DieBlueprint(
                    "d20",
                    "D20",
                    20,
                    1.15f,
                    new Vector2(0.54f, 0.34f),
                    2.2f,
                    icosahedronVertices,
                    CloneFaces(IcosahedronFaces),
                    (int[])D20ValuesByFaceIndex.Clone())
            };
        }

        private static Vector3[] BuildIcosahedronVertices()
        {
            float phi = (1f + Mathf.Sqrt(5f)) * 0.5f;
            return NormalizeToUnitRadius(new[]
            {
                new Vector3(-1f, phi, 0f), new Vector3(1f, phi, 0f),
                new Vector3(-1f, -phi, 0f), new Vector3(1f, -phi, 0f),
                new Vector3(0f, -1f, phi), new Vector3(0f, 1f, phi),
                new Vector3(0f, -1f, -phi), new Vector3(0f, 1f, -phi),
                new Vector3(phi, 0f, -1f), new Vector3(phi, 0f, 1f),
                new Vector3(-phi, 0f, -1f), new Vector3(-phi, 0f, 1f)
            });
        }

        private static Polyhedron BuildPentagonalTrapezohedron()
        {
            const int ringCount = 5;
            Vector3[] primalVertices = new Vector3[ringCount * 2];
            for (int i = 0; i < ringCount; i++)
            {
                float topAngle = i / (float)ringCount * Mathf.PI * 2f;
                float bottomAngle = topAngle + Mathf.PI / ringCount;
                primalVertices[i] = new Vector3(
                    Mathf.Cos(topAngle),
                0.88f,
                    Mathf.Sin(topAngle));
                primalVertices[ringCount + i] = new Vector3(
                    Mathf.Cos(bottomAngle),
                    -0.88f,
                    Mathf.Sin(bottomAngle));
            }

            List<int[]> primalFaces = new()
            {
                new[] { 0, 1, 2, 3, 4 },
                new[] { 9, 8, 7, 6, 5 }
            };
            for (int i = 0; i < ringCount; i++)
            {
                int next = (i + 1) % ringCount;
                primalFaces.Add(new[] { i, ringCount + i, next });
                primalFaces.Add(new[] { next, ringCount + i, ringCount + next });
            }

            return BuildDual(primalVertices, primalFaces);
        }

        private static Polyhedron BuildDual(
            IReadOnlyList<Vector3> primalVertices,
            IReadOnlyList<int[]> primalFaces)
        {
            Vector3[] dualVertices = new Vector3[primalFaces.Count];
            for (int faceIndex = 0; faceIndex < primalFaces.Count; faceIndex++)
            {
                int[] face = (int[])primalFaces[faceIndex].Clone();
                Vector3 centroid = CalculateCentroid(primalVertices, face);
                Vector3 normal = CalculatePolygonNormal(primalVertices, face);
                if (Vector3.Dot(normal, centroid) < 0f)
                    normal = -normal;
                float planeDistance = Vector3.Dot(normal, centroid);
                if (planeDistance <= 0.0001f)
                    throw new InvalidOperationException("Cannot construct a centered dual from a face through the origin.");
                dualVertices[faceIndex] = normal / planeDistance;
            }
            dualVertices = NormalizeToUnitRadius(dualVertices);

            int[][] dualFaces = new int[primalVertices.Count][];
            for (int vertexIndex = 0; vertexIndex < primalVertices.Count; vertexIndex++)
            {
                List<int> incidentFaces = new();
                for (int faceIndex = 0; faceIndex < primalFaces.Count; faceIndex++)
                {
                    int[] face = primalFaces[faceIndex];
                    for (int corner = 0; corner < face.Length; corner++)
                    {
                        if (face[corner] != vertexIndex)
                            continue;
                        incidentFaces.Add(faceIndex);
                        break;
                    }
                }

                if (incidentFaces.Count < 3)
                    throw new InvalidOperationException($"Dual vertex {vertexIndex} has only {incidentFaces.Count} faces.");

                Vector3 outward = primalVertices[vertexIndex].normalized;
                Vector3 center = Vector3.zero;
                for (int i = 0; i < incidentFaces.Count; i++)
                    center += dualVertices[incidentFaces[i]];
                center /= incidentFaces.Count;
                Vector3 tangent =
                    Vector3.ProjectOnPlane(dualVertices[incidentFaces[0]] - center, outward).normalized;
                if (tangent.sqrMagnitude < 0.001f)
                    tangent = Vector3.ProjectOnPlane(Vector3.up, outward).normalized;
                Vector3 bitangent = Vector3.Cross(outward, tangent);
                incidentFaces.Sort((left, right) =>
                {
                    Vector3 leftOffset = dualVertices[left] - center;
                    Vector3 rightOffset = dualVertices[right] - center;
                    float leftAngle = Mathf.Atan2(
                        Vector3.Dot(leftOffset, bitangent),
                        Vector3.Dot(leftOffset, tangent));
                    float rightAngle = Mathf.Atan2(
                        Vector3.Dot(rightOffset, bitangent),
                        Vector3.Dot(rightOffset, tangent));
                    return leftAngle.CompareTo(rightAngle);
                });
                dualFaces[vertexIndex] = incidentFaces.ToArray();
            }

            return new Polyhedron(dualVertices, dualFaces);
        }

        private static Vector3[] NormalizeToUnitRadius(IReadOnlyList<Vector3> source)
        {
            float maximumRadius = 0f;
            for (int i = 0; i < source.Count; i++)
                maximumRadius = Mathf.Max(maximumRadius, source[i].magnitude);
            if (maximumRadius <= 0.0001f)
                throw new InvalidOperationException("A die polyhedron cannot have zero radius.");

            Vector3[] normalized = new Vector3[source.Count];
            for (int i = 0; i < source.Count; i++)
                normalized[i] = source[i] / maximumRadius;
            return normalized;
        }

        private static int[][] CloneFaces(IReadOnlyList<int[]> source)
        {
            int[][] clone = new int[source.Count][];
            for (int i = 0; i < source.Count; i++)
                clone[i] = (int[])source[i].Clone();
            return clone;
        }

        private static int[] SequentialValues(int count)
        {
            int[] values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = i + 1;
            return values;
        }

        private static Mesh BuildDieMesh(
            DieBlueprint blueprint,
            out List<FaceGeometry> faces)
        {
            faces = BuildFaceGeometry(blueprint);
            List<Vector3> meshVertices = new();
            List<Vector3> meshNormals = new();
            List<int> triangles = new();

            for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
            {
                FaceGeometry face = faces[faceIndex];
                for (int edge = 0; edge < face.CornerCount; edge++)
                {
                    int next = (edge + 1) % face.CornerCount;
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

                for (int triangle = 1; triangle < face.CornerCount - 1; triangle++)
                {
                    AddTriangle(
                        meshVertices,
                        meshNormals,
                        triangles,
                        face.RecessFloor[0],
                        face.RecessFloor[triangle],
                        face.RecessFloor[triangle + 1],
                        face.Normal);
                }
            }

            AddEdgeChamfers(blueprint, faces, meshVertices, meshNormals, triangles);
            AddVertexCaps(blueprint, faces, meshVertices, meshNormals, triangles);

            Mesh mesh = new()
            {
                name = $"{blueprint.AssetName}_Resin_BeveledRecessed"
            };
            mesh.SetVertices(meshVertices);
            mesh.SetNormals(meshNormals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static List<FaceGeometry> BuildFaceGeometry(DieBlueprint blueprint)
        {
            IReadOnlyList<Vector3> vertices = blueprint.Vertices;
            IReadOnlyList<int[]> faceIndices = blueprint.FaceIndices;
            List<FaceGeometry> faces = new(faceIndices.Count);
            for (int faceIndex = 0; faceIndex < faceIndices.Count; faceIndex++)
            {
                int[] indices = (int[])faceIndices[faceIndex].Clone();
                Vector3 centroid = CalculateCentroid(vertices, indices);
                Vector3 normal = CalculatePolygonNormal(vertices, indices);
                if (Vector3.Dot(normal, centroid) < 0f)
                {
                    Array.Reverse(indices);
                    normal = CalculatePolygonNormal(vertices, indices);
                }

                Vector3[] polygon = new Vector3[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                    polygon[i] = vertices[indices[i]];
                centroid = CalculateCentroid(polygon);
                Vector3 uprightSource =
                    string.Equals(blueprint.DieId, "d6", StringComparison.Ordinal)
                        ? (polygon[0] + polygon[1]) * 0.5f - centroid
                        : polygon[0] - centroid;
                Vector3 upright = Vector3.ProjectOnPlane(uprightSource, normal).normalized;
                if (upright.sqrMagnitude < 0.001f)
                    upright = Vector3.ProjectOnPlane(Vector3.up, normal).normalized;
                faces.Add(new FaceGeometry(indices, polygon, centroid, normal, upright));
            }
            return faces;
        }

        private static Vector3 CalculateCentroid(
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<int> indices)
        {
            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < indices.Count; i++)
                centroid += vertices[indices[i]];
            return centroid / indices.Count;
        }

        private static Vector3 CalculateCentroid(IReadOnlyList<Vector3> vertices)
        {
            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < vertices.Count; i++)
                centroid += vertices[i];
            return centroid / vertices.Count;
        }

        private static Vector3 CalculatePolygonNormal(
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<int> indices)
        {
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < indices.Count; i++)
            {
                Vector3 current = vertices[indices[i]];
                Vector3 next = vertices[indices[(i + 1) % indices.Count]];
                normal.x += (current.y - next.y) * (current.z + next.z);
                normal.y += (current.z - next.z) * (current.x + next.x);
                normal.z += (current.x - next.x) * (current.y + next.y);
            }
            if (normal.sqrMagnitude < 0.000001f)
                throw new InvalidOperationException("A die face has zero area.");
            return normal.normalized;
        }

        private static void AddEdgeChamfers(
            DieBlueprint blueprint,
            IReadOnlyList<FaceGeometry> faces,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> triangles)
        {
            Dictionary<EdgeKey, List<FaceEdge>> edges = new();
            for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
            {
                FaceGeometry face = faces[faceIndex];
                for (int edgeIndex = 0; edgeIndex < face.CornerCount; edgeIndex++)
                {
                    int next = (edgeIndex + 1) % face.CornerCount;
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
                {
                    throw new InvalidOperationException(
                        $"{blueprint.AssetName} edge {pair.Key} is shared by {pair.Value.Count} faces.");
                }

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
            DieBlueprint blueprint,
            IReadOnlyList<FaceGeometry> faces,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> triangles)
        {
            for (int vertexIndex = 0; vertexIndex < blueprint.Vertices.Length; vertexIndex++)
            {
                List<Vector3> ring = new();
                for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
                {
                    FaceGeometry face = faces[faceIndex];
                    for (int corner = 0; corner < face.CornerCount; corner++)
                    {
                        if (face.VertexIndices[corner] == vertexIndex)
                            ring.Add(face.Cut[corner]);
                    }
                }

                if (ring.Count < 3)
                {
                    throw new InvalidOperationException(
                        $"{blueprint.AssetName} vertex {vertexIndex} has only {ring.Count} incident faces.");
                }

                Vector3 outward = blueprint.Vertices[vertexIndex].normalized;
                Vector3 center = CalculateCentroid(ring);
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
            {
                throw new InvalidOperationException(
                    $"Cinzel numeric font generation missed glyphs: {missingCharacters}");
            }

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

            Material material = new(shader)
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
            material.SetFloat("_ShimmerAmount", 0f);
            material.SetFloat("_ShimmerSpeed", 0.16f);
            AssetDatabase.CreateAsset(material, ResinMaterialPath);
            return material;
        }

        private static Material BuildNumeralMaterial(TMP_FontAsset fontAsset)
        {
            Material material = new(fontAsset.material)
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

        private static GameObject BuildPrefab(
            DieBlueprint blueprint,
            Mesh mesh,
            Material resinMaterial,
            Material numeralMaterial,
            TMP_FontAsset fontAsset,
            IReadOnlyList<FaceGeometry> faces)
        {
            GameObject root = new($"{blueprint.AssetName}_Resin");
            try
            {
                MeshFilter filter = root.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = root.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = resinMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;

                GameObject labelRoot = new("FaceLabels");
                labelRoot.transform.SetParent(root.transform, false);
                for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
                {
                    BuildFaceLabel(
                        labelRoot.transform,
                        faces[faceIndex],
                        blueprint.ValuesByFaceIndex[faceIndex],
                        blueprint,
                        fontAsset,
                        numeralMaterial);
                }

                string prefabPath = PrefabPathFor(blueprint);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (prefab == null)
                    throw new InvalidOperationException($"Could not save {blueprint.DieId} at {prefabPath}.");
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
            DieBlueprint blueprint,
            TMP_FontAsset fontAsset,
            Material numeralMaterial)
        {
            GameObject labelObject = new(
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
            rect.sizeDelta = blueprint.LabelSize;

            TextMeshPro text = labelObject.GetComponent<TextMeshPro>();
            text.text = value.ToString(CultureInfo.InvariantCulture);
            text.font = fontAsset;
            text.fontSharedMaterial = numeralMaterial;
            text.fontSize = blueprint.FontSizeMax;
            text.enableAutoSizing = true;
            text.fontSizeMin = blueprint.FontSizeMax * 0.38f;
            text.fontSizeMax = blueprint.FontSizeMax;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.margin = Vector4.zero;
            text.color = Color.white;
            text.renderer.sortingOrder = 2;

            DiceFaceLabel label = labelObject.GetComponent<DiceFaceLabel>();
            label.SetAuthoringData(value, face.Normal, face.Upright);
        }

        private static DiceDefinition BuildDefinition(
            DieBlueprint blueprint,
            GameObject prefab,
            IReadOnlyList<FaceGeometry> geometry)
        {
            DiceFace[] facesByResult = new DiceFace[blueprint.SideCount];
            for (int faceIndex = 0; faceIndex < geometry.Count; faceIndex++)
            {
                int value = blueprint.ValuesByFaceIndex[faceIndex];
                FaceGeometry face = geometry[faceIndex];
                facesByResult[value - 1] = new DiceFace(value, face.Normal, face.Upright);
            }

            DiceDefinition definition = ScriptableObject.CreateInstance<DiceDefinition>();
            definition.name = blueprint.AssetName;
            definition.SetAuthoringData(
                blueprint.DieId,
                blueprint.SideCount,
                prefab,
                blueprint.PresentationScale,
                facesByResult);
            AssetDatabase.CreateAsset(definition, DefinitionPathFor(blueprint));
            return definition;
        }

        private static List<DiceMotionProfile> BuildMotionProfiles()
        {
            return new List<DiceMotionProfile>(3)
            {
                CreateMotionProfile(
                    MotionProfilePaths[0],
                    "crescent",
                    "Crescent",
                    0.28f,
                    1.22f,
                    0.67f,
                    new Vector3(18f, -38f, 12f),
                    new Vector3(0.72f, 0.58f, 0.24f),
                    4.6f,
                    Curve((0f, -0.28f), (0.14f, -0.34f), (0.42f, 0.24f), (0.67f, -0.12f), (1f, 0f)),
                    Curve((0f, 0.12f), (0.16f, 0.20f), (0.46f, -0.18f), (0.73f, 0.11f), (1f, 0f)),
                    Curve((0f, 0.42f), (0.18f, 0.10f), (0.48f, -0.35f), (0.72f, 0.28f), (1f, 0f)),
                    Curve((0f, 0.70f), (0.16f, 0.82f), (0.48f, 1.07f), (0.78f, 0.96f), (1f, 1f))),
                CreateMotionProfile(
                    MotionProfilePaths[1],
                    "crosswind",
                    "Crosswind",
                    0.32f,
                    1.30f,
                    0.70f,
                    new Vector3(-24f, 32f, -18f),
                    new Vector3(-0.45f, 0.78f, 0.43f),
                    5.2f,
                    Curve((0f, 0.30f), (0.17f, 0.36f), (0.43f, -0.30f), (0.69f, 0.16f), (1f, 0f)),
                    Curve((0f, -0.13f), (0.18f, -0.20f), (0.45f, 0.24f), (0.72f, -0.08f), (1f, 0f)),
                    Curve((0f, 0.34f), (0.24f, -0.22f), (0.52f, 0.42f), (0.78f, -0.12f), (1f, 0f)),
                    Curve((0f, 0.73f), (0.18f, 0.88f), (0.52f, 1.10f), (0.80f, 0.95f), (1f, 1f))),
                CreateMotionProfile(
                    MotionProfilePaths[2],
                    "helix",
                    "Helix",
                    0.24f,
                    1.38f,
                    0.72f,
                    new Vector3(35f, 12f, 28f),
                    new Vector3(0.34f, -0.61f, 0.72f),
                    5.8f,
                    Curve((0f, -0.17f), (0.18f, -0.32f), (0.40f, 0.29f), (0.63f, -0.22f), (0.82f, 0.10f), (1f, 0f)),
                    Curve((0f, -0.25f), (0.19f, 0.19f), (0.43f, 0.27f), (0.66f, -0.16f), (0.84f, 0.07f), (1f, 0f)),
                    Curve((0f, 0.48f), (0.20f, -0.30f), (0.46f, 0.38f), (0.69f, -0.18f), (0.85f, 0.16f), (1f, 0f)),
                    Curve((0f, 0.66f), (0.17f, 0.90f), (0.45f, 1.12f), (0.72f, 0.92f), (0.86f, 1.04f), (1f, 1f)))
            };
        }

        private static DiceMotionProfile CreateMotionProfile(
            string path,
            string profileId,
            string displayName,
            float anticipationDuration,
            float movingDuration,
            float settleStart,
            Vector3 entryEuler,
            Vector3 spinAxis,
            float turnCount,
            AnimationCurve horizontal,
            AnimationCurve vertical,
            AnimationCurve depth,
            AnimationCurve scale)
        {
            DiceMotionProfile profile = ScriptableObject.CreateInstance<DiceMotionProfile>();
            profile.name = $"Dice_{displayName}";
            profile.SetAuthoringData(
                profileId,
                displayName,
                anticipationDuration,
                movingDuration,
                settleStart,
                entryEuler,
                spinAxis,
                turnCount,
                horizontal,
                vertical,
                depth,
                scale,
                Curve((0f, 0f), (0.18f, 0.08f), (0.72f, 0.86f), (1f, 1f)),
                Curve((0f, 0f), (0.34f, 0.08f), (0.72f, 0.58f), (1f, 1f)));
            AssetDatabase.CreateAsset(profile, path);
            return profile;
        }

        private static AnimationCurve Curve(params (float time, float value)[] authoredKeys)
        {
            Keyframe[] keys = new Keyframe[authoredKeys.Length];
            for (int i = 0; i < authoredKeys.Length; i++)
                keys[i] = new Keyframe(authoredKeys[i].time, authoredKeys[i].value);

            AnimationCurve curve = new(keys);
            for (int i = 0; i < keys.Length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
            }
            return curve;
        }

        private static void BuildCatalog(
            IReadOnlyList<DiceDefinition> definitions,
            IReadOnlyList<DiceMotionProfile> motionProfiles,
            Material resinMaterial,
            Material numeralMaterial)
        {
            DiceSetCatalog catalog = ScriptableObject.CreateInstance<DiceSetCatalog>();
            catalog.name = "DefaultDiceSet";
            catalog.SetAuthoringData(
                "default",
                definitions,
                motionProfiles,
                resinMaterial,
                numeralMaterial);
            AssetDatabase.CreateAsset(catalog, CatalogPath);
        }

        private static void BuildReviewScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.12f, 0.085f, 0.10f);
            RenderSettings.ambientEquatorColor = new Color(0.045f, 0.025f, 0.032f);
            RenderSettings.ambientGroundColor = new Color(0.008f, 0.006f, 0.012f);

            GameObject cameraObject = new("InspectionCamera");
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

            GameObject presenterObject = new("DiceOverlayPresenter");
            DiceOverlayPresenter presenter = presenterObject.AddComponent<DiceOverlayPresenter>();
            GameObject panelObject = new("DicePresentationDebugPanel");
            DicePresentationDebugPanel panel = panelObject.AddComponent<DicePresentationDebugPanel>();
            panel.SetAuthoringData(presenter, startVisible: true);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ReviewScenePath))
                throw new InvalidOperationException($"Could not save the review scene at {ReviewScenePath}.");
            Selection.activeGameObject = presenterObject;
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

        private sealed class DieBlueprint
        {
            public string DieId { get; }
            public string AssetName { get; }
            public int SideCount { get; }
            public float PresentationScale { get; }
            public Vector2 LabelSize { get; }
            public float FontSizeMax { get; }
            public Vector3[] Vertices { get; }
            public int[][] FaceIndices { get; }
            public int[] ValuesByFaceIndex { get; }

            public DieBlueprint(
                string dieId,
                string assetName,
                int sideCount,
                float presentationScale,
                Vector2 labelSize,
                float fontSizeMax,
                Vector3[] vertices,
                int[][] faceIndices,
                int[] valuesByFaceIndex)
            {
                if (faceIndices.Length != sideCount || valuesByFaceIndex.Length != sideCount)
                {
                    throw new ArgumentException(
                        $"{assetName} must have exactly {sideCount} faces and values.");
                }

                DieId = dieId;
                AssetName = assetName;
                SideCount = sideCount;
                PresentationScale = presentationScale;
                LabelSize = labelSize;
                FontSizeMax = fontSizeMax;
                Vertices = vertices;
                FaceIndices = faceIndices;
                ValuesByFaceIndex = valuesByFaceIndex;
            }
        }

        private sealed class Polyhedron
        {
            public Vector3[] Vertices { get; }
            public int[][] Faces { get; }

            public Polyhedron(Vector3[] vertices, int[][] faces)
            {
                Vertices = vertices;
                Faces = faces;
            }
        }

        private sealed class FaceGeometry
        {
            public int[] VertexIndices { get; }
            public int CornerCount => VertexIndices.Length;
            public Vector3 Centroid { get; }
            public Vector3 Normal { get; }
            public Vector3 Upright { get; }
            public Vector3[] Cut { get; }
            public Vector3[] RecessRim { get; }
            public Vector3[] RecessFloor { get; }

            public FaceGeometry(
                int[] vertexIndices,
                IReadOnlyList<Vector3> vertices,
                Vector3 centroid,
                Vector3 normal,
                Vector3 upright)
            {
                VertexIndices = vertexIndices;
                Centroid = centroid;
                Normal = normal;
                Upright = upright;
                Cut = new Vector3[vertices.Count];
                RecessRim = new Vector3[vertices.Count];
                RecessFloor = new Vector3[vertices.Count];
                for (int i = 0; i < vertices.Count; i++)
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
