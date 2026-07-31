using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    // The renderer's own output-neutrality instrument, added for Phase C of the
    // layered 3D topology design.
    //
    // WHY THIS EXISTS. Every gate the dungeon work has used so far compares the
    // PLAN — `hashes.canonical` from Batch Validate, which never builds a
    // GameObject. That is the right gate for a planner change and it is
    // completely blind to a renderer change: `BuildWallEdges` could emit every
    // face at the wrong height and the canonical hash would not move a bit.
    // Phase C rewrites the wall-edge construction, and §7.1's compatibility
    // lever is "every existing seed renders byte-identically" — a claim that
    // nothing in the project could measure. This measures it.
    //
    // WHAT IT HASHES. Every renderer and every collider under the built root,
    // as (mesh, world transform, collider shape), CANONICALLY SORTED. Sorting is
    // deliberate: the digest is a statement about GEOMETRY, not about the order
    // the renderer happened to instantiate things in. A refactor that emits the
    // same faces in a different sequence is neutral and should read as neutral;
    // one that moves a face by a quarter unit is not, and will not.
    //
    // Coordinates are rounded to four decimals. Everything here is an integer
    // cell or level multiplied by a constant, so four decimals is far inside the
    // noise floor while still catching a real displacement.
    internal sealed partial class DungeonLabGenerator
    {
        private const string RenderDigestReportPrefix = "render_digest";

        [MenuItem("Tools/Dungeon Lab/Render Digest (200 Fixed Seeds)")]
        public static void RenderDigest200Seeds()
        {
            RunRenderDigest(BaselineFirstSeed, BaselineSeedCount);
        }

        [MenuItem("Tools/Dungeon Lab/Render Digest (12 Fixed Seeds)")]
        public static void RenderDigest12Seeds()
        {
            RunRenderDigest(BaselineFirstSeed, 12);
        }

        private static string RunRenderDigest(int firstSeed, int seedCount)
        {
            Directory.CreateDirectory(BatchReportDirectory);
            int densityLevel = ResolveRequestedDensityLevel();
            var seeds = new JArray();
            var combined = new List<string>(seedCount);
            int built = 0;

            try
            {
                for (int index = 0; index < seedCount; index++)
                {
                    int seed = firstSeed + index;
                    if (!Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar(
                            "Dungeon Lab Render Digest",
                            $"Seed {seed} ({index + 1}/{seedCount})",
                            (float)index / seedCount))
                    {
                        break;
                    }

                    GameObject root = null;
                    try
                    {
                        root = BuildRenderedSeed(seed, out _, out _, out _);
                        string digest = ComputeRenderDigest(root, out int renderers, out int colliders);
                        built++;
                        combined.Add($"{seed}={digest}");
                        seeds.Add(new JObject
                        {
                            ["seed"] = seed,
                            ["digest"] = digest,
                            ["renderers"] = renderers,
                            ["colliders"] = colliders
                        });
                    }
                    catch (Exception failure)
                    {
                        // A seed that cannot build is reported, not swallowed:
                        // "the digest held" must never be reachable by way of
                        // "nothing was built".
                        combined.Add($"{seed}=FAILED");
                        seeds.Add(new JObject
                        {
                            ["seed"] = seed,
                            ["digest"] = "FAILED",
                            ["failure"] = failure.Message
                        });
                    }
                    finally
                    {
                        if (root != null)
                        {
                            DestroyImmediate(root);
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var report = new JObject
            {
                ["summaryVersion"] = ActiveDiagnosticSummaryVersion,
                ["generatorVersion"] = ActiveDiagnosticGeneratorVersion,
                ["densityLevel"] = densityLevel,
                ["firstSeed"] = firstSeed,
                ["seedCount"] = seedCount,
                ["built"] = built,
                ["renderDigest"] = ComputeSha256(string.Join("\n", combined)),
                ["seeds"] = seeds,
                ["measurement"] =
                    "per seed: SHA-256 over every renderer and collider under the built root as " +
                    "(mesh, world position, world rotation, lossy scale, collider shape), sorted " +
                    "ordinally. Sorted on purpose — the digest describes geometry, not instantiation " +
                    "order. renderDigest is SHA-256 over the per-seed lines."
            };
            AddGenerationSettingsIdentity(report);
            string path = Path.Combine(
                BatchReportDirectory,
                $"{RenderDigestReportPrefix}_d{densityLevel}_{firstSeed}_{firstSeed + seedCount - 1}.json");
            File.WriteAllText(path, report.ToString(Formatting.Indented));
            Debug.Log(
                $"Dungeon Lab RENDER_DIGEST density={densityLevel}; built={built}/{seedCount}; " +
                $"digest={report.Value<string>("renderDigest")}; report={path}");
            return path;
        }

        /// <summary>
        /// SHA-256 over the built root's visible and collidable geometry.
        /// </summary>
        private static string ComputeRenderDigest(GameObject root, out int rendererCount, out int colliderCount)
        {
            var lines = new List<string>();

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            foreach (Renderer renderer in renderers)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                string mesh = filter != null && filter.sharedMesh != null
                    ? filter.sharedMesh.name
                    : "<none>";
                lines.Add($"R|{mesh}|{DescribeTransform(renderer.transform)}");
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(includeInactive: true);
            foreach (Collider collider in colliders)
            {
                lines.Add($"C|{DescribeCollider(collider)}|{DescribeTransform(collider.transform)}");
            }

            rendererCount = renderers.Length;
            colliderCount = colliders.Length;
            lines.Sort(StringComparer.Ordinal);
            return ComputeSha256(string.Join("\n", lines));
        }

        private static string DescribeCollider(Collider collider)
        {
            switch (collider)
            {
                case MeshCollider mesh:
                    return $"mesh:{(mesh.sharedMesh != null ? mesh.sharedMesh.name : "<none>")}:" +
                        $"{(mesh.convex ? "convex" : "concave")}";
                case BoxCollider box:
                    return $"box:{Round(box.center)}:{Round(box.size)}";
                case CapsuleCollider capsule:
                    return $"capsule:{Round(capsule.center)}:{Fixed(capsule.radius)}:{Fixed(capsule.height)}";
                case SphereCollider sphere:
                    return $"sphere:{Round(sphere.center)}:{Fixed(sphere.radius)}";
                default:
                    return collider.GetType().Name;
            }
        }

        private static string DescribeTransform(Transform transform)
        {
            return $"{Round(transform.position)}|{Round(transform.rotation.eulerAngles)}|{Round(transform.lossyScale)}";
        }

        private static string Round(Vector3 value)
        {
            return $"{Fixed(value.x)},{Fixed(value.y)},{Fixed(value.z)}";
        }

        private static string Fixed(float value)
        {
            // Negative zero and positive zero must render the same, or a sign
            // flip with no geometric meaning reads as a moved face.
            float rounded = (float)Math.Round(value, 4);
            if (rounded == 0f)
            {
                rounded = 0f;
            }

            return rounded.ToString("F4", CultureInfo.InvariantCulture);
        }
    }
}
