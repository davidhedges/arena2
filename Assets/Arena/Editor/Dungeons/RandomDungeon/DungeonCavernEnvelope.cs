#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Presentation;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DungeonLab.Editor
{
    /// <summary>
    /// Surrounds a generated dungeon with the illusion of an enormous cavern:
    /// depth-banded rock silhouettes, a slowly parallaxing far band, and a lava
    /// underglow whose apparent distance closes as the descent deepens.
    /// </summary>
    /// <remarks>
    /// EVERYTHING HERE IS DOWNSTREAM OF THE PLAN, BY DESIGN.
    ///
    /// The envelope reads only the built scene — renderer bounds under the
    /// dungeon root — and never the plan, the route intent, or the prism
    /// ledger. It builds into its OWN scene root rather than under
    /// "Generated Dungeon", because three separate systems walk that root and
    /// would otherwise absorb scenery:
    ///
    ///   * `ComputeRenderDigest` hashes every renderer and collider under the
    ///     built root, so a child would rebaseline the whole seed corpus;
    ///   * `ExportLastNavigationSurfaces` projects navigation from it;
    ///   * `MarkDungeonCollision` stamps the GameplayCollision layer onto it.
    ///
    /// Envelope objects therefore carry no colliders and stay on the default
    /// layer. Collision export is layer-driven throughout
    /// `GameplayCollisionExporter`, so nothing here can reach the server
    /// payload — which is also what keeps the envelope compliant with the
    /// repository's query-geometry contract: it authors none, so it can never
    /// block line of sight or a projectile.
    ///
    /// Net effect: resultHash, catalogDigest, the seed reports, navigation
    /// surfaces and both collision payloads are untouched by this feature.
    /// </remarks>
    internal static class DungeonCavernEnvelope
    {
        internal const string RootName = "Cavern Envelope";
        private const string SilhouetteShaderName = "Arena/Environment/CavernSilhouette";

        /// <summary>Fraction of camera translation the far band copies.</summary>
        private const float FarBandFollow = 0.85f;

        /// <summary>
        /// One ring of silhouettes at a given depth from the dungeon.
        /// </summary>
        private readonly struct Band
        {
            internal Band(
                string name,
                int count,
                float innerDistance,
                float outerDistance,
                float minHeight,
                float maxHeight,
                float ceilingRise,
                float alpha,
                float tipFade,
                float hangingShare,
                float phase)
            {
                Name = name;
                Count = count;
                InnerDistance = innerDistance;
                OuterDistance = outerDistance;
                MinHeight = minHeight;
                MaxHeight = maxHeight;
                CeilingRise = ceilingRise;
                Alpha = alpha;
                TipFade = tipFade;
                HangingShare = hangingShare;
                Phase = phase;
            }

            internal string Name { get; }
            internal int Count { get; }
            internal float InnerDistance { get; }
            internal float OuterDistance { get; }
            internal float MinHeight { get; }
            internal float MaxHeight { get; }
            internal float CeilingRise { get; }
            internal float Alpha { get; }
            internal float TipFade { get; }

            /// <summary>Portion of the ring hung from above rather than rising from below.</summary>
            internal float HangingShare { get; }

            /// <summary>
            /// Angular offset. Bands carry DIFFERENT phases on purpose: depth is
            /// encoded by silhouettes overlapping each other, not by their size,
            /// so rings that lined up radially would read as one flat wall from
            /// most viewing angles.
            /// </summary>
            internal float Phase { get; }
        }

        private static readonly Band NearBand = new(
            name: "near",
            count: 26,
            innerDistance: 26f,
            outerDistance: 64f,
            minHeight: 16f,
            maxHeight: 44f,
            ceilingRise: 24f,
            alpha: 0.95f,
            tipFade: 0.55f,
            hangingShare: 0.35f,
            phase: 0f);

        private static readonly Band MidBand = new(
            name: "mid",
            count: 34,
            innerDistance: 92f,
            outerDistance: 228f,
            minHeight: 55f,
            maxHeight: 165f,
            ceilingRise: 62f,
            alpha: 0.72f,
            tipFade: 0.30f,
            hangingShare: 0.45f,
            phase: 0.37f);

        private static readonly Band FarBand = new(
            name: "far",
            count: 18,
            innerDistance: 300f,
            outerDistance: 400f,
            minHeight: 120f,
            maxHeight: 300f,
            ceilingRise: 130f,
            alpha: 0.50f,
            tipFade: 0.18f,
            hangingShare: 0.40f,
            phase: 0.81f);

        // The overhead field is what a camera that can look up actually sees.
        // It hangs inside the far band's radius and is camera-anchored with it,
        // so there is always something overhead at several depths.
        private const int OverheadCount = 22;
        private const float OverheadInnerRadius = 40f;
        private const float OverheadOuterRadius = 150f;
        private const float OverheadRise = 130f;
        private const float OverheadMinHeight = 45f;
        private const float OverheadMaxHeight = 130f;

        /// <summary>
        /// Wraps a built world in the cavern illusion.
        /// </summary>
        /// <param name="buildGlowPool">
        /// False suppresses the fake lava disc. Required whenever the caller
        /// owns REAL lava geometry: the disc is an unlit vertex-coloured
        /// gradient standing in for a surface, and drawn over one it z-fights
        /// and flattens it. The vertex-baked warmth on the rock and the
        /// underglow light are unaffected and still wanted.
        /// </param>
        /// <param name="backdropAssetPath">
        /// Where the generated panorama lands. Defaults to the dungeon's own
        /// asset; every other caller must claim its own path, or rebuilding one
        /// scene repaints the other. See <see cref="CavernBackdrop"/>.
        /// </param>
        /// <param name="underglowShadows">
        /// Shadow mode for the upward directional light. The generated dungeon
        /// keeps the established shadow-free treatment by default; callers with
        /// a continuous overhead occluder can opt into shadows explicitly.
        /// </param>
        /// <param name="generateBackdrop">
        /// False when the caller applies an authored panorama after building the envelope.
        /// </param>
        internal static void Build(
            Scene destination,
            GameObject dungeonRoot,
            int seed,
            CavernDepthProfile depth,
            bool buildGlowPool = true,
            string? backdropAssetPath = null,
            LightShadows underglowShadows = LightShadows.None,
            bool generateBackdrop = true)
        {
            if (dungeonRoot == null)
                throw new ArgumentNullException(nameof(dungeonRoot));

            Bounds extents = ComputeDungeonExtents(dungeonRoot);
            float hullRadius = Mathf.Max(extents.extents.x, extents.extents.z);
            float floorY = extents.min.y;
            float ceilingY = extents.max.y;
            Vector3 centre = new(extents.center.x, extents.center.y, extents.center.z);
            float glowY = floorY - depth.GlowDistance;
            float glowSpan = Mathf.Max(90f, depth.GlowDistance * 1.6f);

            Material silhouette = CreateSilhouetteMaterial();

            GameObject root = new(RootName);
            SceneManager.MoveGameObjectToScene(root, destination);
            root.transform.position = Vector3.zero;

            int silhouettes = 0;
            silhouettes += BuildBand(
                root.transform, NearBand, seed, depth, hullRadius, centre, floorY, ceilingY,
                glowY, glowSpan, silhouette);
            silhouettes += BuildBand(
                root.transform, MidBand, seed, depth, hullRadius, centre, floorY, ceilingY,
                glowY, glowSpan, silhouette);

            GameObject farVault = new("Far Vault");
            farVault.transform.SetParent(root.transform, worldPositionStays: false);
            farVault.transform.position = centre;
            farVault.AddComponent<CavernFarBandFollower>().Configure(centre, FarBandFollow);

            silhouettes += BuildBand(
                farVault.transform, FarBand, seed, depth, hullRadius, centre, floorY, ceilingY,
                glowY, glowSpan, silhouette);
            silhouettes += BuildOverheadField(
                farVault.transform, seed, depth, centre, ceilingY, glowY, glowSpan, silhouette);

            BuildUnderglow(
                root.transform,
                seed,
                depth,
                centre,
                hullRadius,
                glowY,
                silhouette,
                buildGlowPool,
                underglowShadows);

            // Behind every band: the painted layer the bands parallax against.
            if (generateBackdrop)
                CavernBackdrop.Apply(seed, depth, backdropAssetPath);

            NormalizeEnvelopeObjects(root);

            Debug.Log(
                "[CAVERN_ENVELOPE] " +
                $"seed={seed}; {depth.Summary}; " +
                $"hullRadius={hullRadius:0.#}m; floorY={floorY:0.#}; ceilingY={ceilingY:0.#}; " +
                $"glowY={glowY:0.#}; silhouettes={silhouettes}; " +
                $"farBandFollow={FarBandFollow:0.##} (parallax {1f - FarBandFollow:0.##})");
        }

        /// <summary>
        /// The dungeon's own visible extent, in final world space.
        /// </summary>
        /// <remarks>
        /// Must be called after `CenterDungeonSpawn`, which translates the root.
        /// Renderer bounds are enough for ring placement; the traced cell hull
        /// only becomes necessary for the near seam, which follows rock art.
        /// </remarks>
        private static Bounds ComputeDungeonExtents(GameObject dungeonRoot)
        {
            Renderer[] renderers = dungeonRoot.GetComponentsInChildren<Renderer>(includeInactive: false);
            if (renderers.Length == 0)
            {
                throw new InvalidOperationException(
                    "Generated dungeon has no renderers, so the cavern envelope has nothing to enclose.");
            }

            Bounds extents = renderers[0].bounds;
            for (int index = 1; index < renderers.Length; index++)
                extents.Encapsulate(renderers[index].bounds);

            return extents;
        }

        private static Material CreateSilhouetteMaterial()
        {
            Shader? shader = Shader.Find(SilhouetteShaderName);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    $"Required cavern envelope shader '{SilhouetteShaderName}' is missing.");
            }

            // Scene-embedded rather than a shared .mat asset: every colour in
            // the envelope is depth-driven, so a shared asset would either bake
            // one floor's palette for all of them or churn on every rebuild.
            Material material = new(shader) { name = "CavernSilhouette (Generated)" };
            // Explicit rather than Material.color: the shader declares only
            // _BaseColor, and the main-colour alias is not worth depending on.
            material.SetColor("_BaseColor", Color.white);
            return material;
        }

        private static int BuildBand(
            Transform parent,
            Band band,
            int seed,
            CavernDepthProfile depth,
            float hullRadius,
            Vector3 centre,
            float floorY,
            float ceilingY,
            float glowY,
            float glowSpan,
            Material silhouette)
        {
            GameObject bandRoot = new($"Band {band.Name}");
            bandRoot.transform.SetParent(parent, worldPositionStays: false);
            bandRoot.transform.position = Vector3.zero;

            Color shadow = SilhouetteShadow(depth);
            int built = 0;

            for (int index = 0; index < band.Count; index++)
            {
                // One stream per silhouette, keyed on band and index, so adding
                // or retuning one placement provably cannot perturb another.
                System.Random rng = Stream(seed, $"{band.Name}:{index}");

                float stratum = (index + 0.5f) * Mathf.PI * 2f / band.Count;
                float angle = stratum + band.Phase + ((float)rng.NextDouble() - 0.5f) * (Mathf.PI / band.Count);
                float radius = hullRadius + Mathf.Lerp(
                    band.InnerDistance,
                    band.OuterDistance,
                    (float)rng.NextDouble());
                float height = Mathf.Lerp(band.MinHeight, band.MaxHeight, (float)rng.NextDouble());
                float baseRadius = height * Mathf.Lerp(0.14f, 0.3f, (float)rng.NextDouble());
                bool hanging = rng.NextDouble() < band.HangingShare;

                float anchorY = hanging
                    ? ceilingY + band.CeilingRise + (float)rng.NextDouble() * band.CeilingRise
                    : floorY - (float)rng.NextDouble() * band.CeilingRise * 0.5f;

                Vector3 position = new(
                    centre.x + Mathf.Cos(angle) * radius,
                    anchorY,
                    centre.z + Mathf.Sin(angle) * radius);

                Mesh mesh = BuildSpireMesh(rng, height, baseRadius, hanging);
                mesh.name = $"CavernSpire_{band.Name}_{index}";
                ApplySilhouetteColours(
                    mesh, position.y, glowY, glowSpan, depth, shadow, band.Alpha, band.TipFade, height);

                CreateSilhouette(
                    bandRoot.transform,
                    $"Spire {band.Name} {index}",
                    mesh,
                    silhouette,
                    position,
                    (float)rng.NextDouble() * 360f);
                built++;
            }

            return built;
        }

        private static int BuildOverheadField(
            Transform parent,
            int seed,
            CavernDepthProfile depth,
            Vector3 centre,
            float ceilingY,
            float glowY,
            float glowSpan,
            Material silhouette)
        {
            GameObject fieldRoot = new("Overhead");
            fieldRoot.transform.SetParent(parent, worldPositionStays: false);
            fieldRoot.transform.position = Vector3.zero;

            Color shadow = SilhouetteShadow(depth);
            int built = 0;

            for (int index = 0; index < OverheadCount; index++)
            {
                System.Random rng = Stream(seed, $"overhead:{index}");

                float angle = (index + 0.5f) * Mathf.PI * 2f / OverheadCount
                    + ((float)rng.NextDouble() - 0.5f) * (Mathf.PI / OverheadCount)
                    + 1.21f;
                float radius = Mathf.Lerp(OverheadInnerRadius, OverheadOuterRadius, (float)rng.NextDouble());
                float height = Mathf.Lerp(OverheadMinHeight, OverheadMaxHeight, (float)rng.NextDouble());
                float baseRadius = height * Mathf.Lerp(0.12f, 0.26f, (float)rng.NextDouble());

                // Spread across several depths so the nearest are large enough
                // to be cut by the top of frame — an object leaving frame is the
                // strongest scale cue available.
                float rise = OverheadRise + (float)rng.NextDouble() * OverheadRise;

                Vector3 position = new(
                    centre.x + Mathf.Cos(angle) * radius,
                    ceilingY + rise,
                    centre.z + Mathf.Sin(angle) * radius);

                Mesh mesh = BuildSpireMesh(rng, height, baseRadius, pointsDown: true);
                mesh.name = $"CavernStalactite_{index}";
                ApplySilhouetteColours(
                    mesh, position.y, glowY, glowSpan, depth, shadow, 0.45f, 0.20f, height);

                CreateSilhouette(
                    fieldRoot.transform,
                    $"Stalactite {index}",
                    mesh,
                    silhouette,
                    position,
                    (float)rng.NextDouble() * 360f);
                built++;
            }

            return built;
        }

        private static void BuildUnderglow(
            Transform parent,
            int seed,
            CavernDepthProfile depth,
            Vector3 centre,
            float hullRadius,
            float glowY,
            Material silhouette,
            bool buildGlowPool,
            LightShadows shadows)
        {
            GameObject underglow = new("Underglow");
            underglow.transform.SetParent(parent, worldPositionStays: false);
            underglow.transform.position = new Vector3(centre.x, glowY, centre.z);

            // Directional pointing straight up. Light from below is rare enough
            // in nature that it reads as "there is a chasm down there" before
            // the player can articulate why, and it inverts shadow direction
            // across the descent for free.
            GameObject lightObject = new("Underglow Light");
            lightObject.transform.SetParent(underglow.transform, worldPositionStays: false);
            lightObject.transform.localPosition = Vector3.zero;
            lightObject.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);

            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = depth.GlowColor;
            light.intensity = depth.GlowIntensity;
            light.shadows = shadows;
            light.bounceIntensity = 0f;

            if (!buildGlowPool)
                return;

            System.Random rng = Stream(seed, "glow-pool");
            float radius = hullRadius * 2.2f + 60f;
            Mesh mesh = BuildGlowPoolMesh(rng, radius, depth);
            mesh.name = "CavernGlowPool";

            CreateSilhouette(
                underglow.transform,
                "Glow Pool",
                mesh,
                silhouette,
                new Vector3(centre.x, glowY, centre.z),
                0f);
        }

        /// <summary>
        /// A tapered, irregular column. Segment count is deliberately low: at
        /// these distances only the silhouette survives, and readable surface
        /// detail on distant rock is what exposes the scale trick.
        /// </summary>
        private static Mesh BuildSpireMesh(System.Random rng, float height, float baseRadius, bool pointsDown)
        {
            int sides = rng.Next(5, 9);
            const int segments = 5;

            // Frozen per side so the irregularity runs consistently up the
            // column rather than turning it into noise.
            float[] sideJitter = new float[sides];
            for (int side = 0; side < sides; side++)
                sideJitter[side] = 0.72f + (float)rng.NextDouble() * 0.56f;

            var vertices = new List<Vector3>(sides * (segments + 1));
            for (int segment = 0; segment <= segments; segment++)
            {
                float t = segment / (float)segments;
                float taper = Mathf.Max(0.05f, Mathf.Pow(1f - t, 1.5f));
                float ringJitter = 0.85f + (float)rng.NextDouble() * 0.3f;
                float y = height * t * (pointsDown ? -1f : 1f);

                for (int side = 0; side < sides; side++)
                {
                    float angle = side * Mathf.PI * 2f / sides;
                    float radius = baseRadius * taper * ringJitter * sideJitter[side];
                    vertices.Add(new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius));
                }
            }

            var triangles = new List<int>(sides * segments * 6 + (sides - 2) * 3);
            for (int segment = 0; segment < segments; segment++)
            {
                for (int side = 0; side < sides; side++)
                {
                    int a = segment * sides + side;
                    int b = segment * sides + (side + 1) % sides;
                    int c = a + sides;
                    int d = b + sides;

                    // Winding is irrelevant: the silhouette shader is two-sided.
                    triangles.Add(a);
                    triangles.Add(c);
                    triangles.Add(b);
                    triangles.Add(b);
                    triangles.Add(c);
                    triangles.Add(d);
                }
            }

            // Cap the wide end. Without it a stalactite viewed from anywhere
            // above its tip is an open cup, and the rim reads as a hard flat
            // ellipse — the single most obvious tell in the first render.
            for (int side = 1; side < sides - 1; side++)
            {
                triangles.Add(0);
                triangles.Add(side);
                triangles.Add(side + 1);
            }

            Mesh mesh = new();
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// The lava pool far below, as a radial gradient that dissolves at its
        /// rim so the player never sees where it ends.
        /// </summary>
        private static Mesh BuildGlowPoolMesh(System.Random rng, float radius, CavernDepthProfile depth)
        {
            const int rings = 6;
            const int sides = 40;

            // A distant pool, not a lit floor. At 0.95 this disc is 285m across
            // and nearly pure lava colour, so it filled the lower half of the
            // view on its own and everything in front of it went to silhouette.
            float core = Mathf.Lerp(0.05f, 0.38f, depth.Glow);
            var vertices = new List<Vector3>(rings * sides + 1) { Vector3.zero };
            var colours = new List<Color>(rings * sides + 1)
            {
                Color.Lerp(depth.Background, depth.GlowColor, core),
            };

            for (int ring = 1; ring <= rings; ring++)
            {
                float t = ring / (float)rings;
                float ringRadius = radius * t;
                float opacity = core * Mathf.Pow(1f - t, 1.8f);

                for (int side = 0; side < sides; side++)
                {
                    float angle = side * Mathf.PI * 2f / sides;
                    float wobble = 0.88f + (float)rng.NextDouble() * 0.24f;
                    vertices.Add(new Vector3(
                        Mathf.Cos(angle) * ringRadius * wobble,
                        0f,
                        Mathf.Sin(angle) * ringRadius * wobble));
                    colours.Add(Color.Lerp(depth.Background, depth.GlowColor, opacity));
                }
            }

            var triangles = new List<int>(rings * sides * 6);
            for (int side = 0; side < sides; side++)
            {
                triangles.Add(0);
                triangles.Add(1 + side);
                triangles.Add(1 + (side + 1) % sides);
            }

            for (int ring = 1; ring < rings; ring++)
            {
                int inner = 1 + (ring - 1) * sides;
                int outer = 1 + ring * sides;
                for (int side = 0; side < sides; side++)
                {
                    int next = (side + 1) % sides;
                    triangles.Add(inner + side);
                    triangles.Add(outer + side);
                    triangles.Add(inner + next);
                    triangles.Add(inner + next);
                    triangles.Add(outer + side);
                    triangles.Add(outer + next);
                }
            }

            Mesh mesh = new();
            mesh.SetVertices(vertices);
            mesh.SetColors(colours);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Bakes the depth read into vertex colour: bottoms carry the underglow,
        /// tips dissolve toward the void.
        /// </summary>
        private static void ApplySilhouetteColours(
            Mesh mesh,
            float objectWorldY,
            float glowY,
            float glowSpan,
            CavernDepthProfile depth,
            Color shadow,
            float alpha,
            float tipFade,
            float height)
        {
            Vector3[] vertices = mesh.vertices;
            var colours = new Color[vertices.Length];
            float safeHeight = Mathf.Max(0.001f, height);

            for (int index = 0; index < vertices.Length; index++)
            {
                float worldY = objectWorldY + vertices[index].y;
                float aboveGlow = Mathf.Clamp01((worldY - glowY) / glowSpan);
                float lit = Mathf.Pow(1f - aboveGlow, 2f) * depth.Glow;

                Color rock = new(
                    Mathf.Lerp(shadow.r, depth.GlowColor.r, lit),
                    Mathf.Lerp(shadow.g, depth.GlowColor.g, lit),
                    Mathf.Lerp(shadow.b, depth.GlowColor.b, lit),
                    1f);

                // The fade is a lerp toward the clear colour, not an alpha
                // ramp: identical over a uniform background, and it keeps the
                // geometry opaque so a spire cannot show its own interior.
                float verticalFraction = Mathf.Clamp01(Mathf.Abs(vertices[index].y) / safeHeight);
                float opacity = alpha * Mathf.Lerp(1f, tipFade, verticalFraction);
                colours[index] = Color.Lerp(depth.Background, rock, opacity);
                colours[index].a = 1f;
            }

            mesh.SetColors(colours);
        }

        /// <summary>
        /// Darker than the clear colour, so silhouettes still read as shapes on
        /// the top floor where there is almost no glow to light them.
        /// </summary>
        private static Color SilhouetteShadow(CavernDepthProfile depth)
        {
            Color background = depth.Background;
            return new Color(background.r * 0.35f, background.g * 0.35f, background.b * 0.35f, 1f);
        }

        private static void CreateSilhouette(
            Transform parent,
            string name,
            Mesh mesh,
            Material material,
            Vector3 position,
            float yawDegrees)
        {
            GameObject silhouette = new(name);
            silhouette.transform.SetParent(parent, worldPositionStays: false);
            silhouette.transform.position = position;
            silhouette.transform.rotation = Quaternion.Euler(0f, yawDegrees, 0f);

            silhouette.AddComponent<MeshFilter>().sharedMesh = mesh;

            MeshRenderer renderer = silhouette.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        /// <summary>
        /// Enforces the two properties that keep the envelope out of every
        /// export: default layer, and no colliders anywhere.
        /// </summary>
        private static void NormalizeEnvelopeObjects(GameObject root)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(includeInactive: true))
                transform.gameObject.layer = 0;

            Collider[] colliders = root.GetComponentsInChildren<Collider>(includeInactive: true);
            if (colliders.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Cavern envelope built {colliders.Length} collider(s). The envelope must author no " +
                    "collision: it would reach the exported gameplay and query payloads and start blocking " +
                    "movement, line of sight or projectiles.");
            }
        }

        private static System.Random Stream(int seed, string subject)
        {
            // Same derivation shape the generator uses for its own scoped
            // streams. A separate derivation rather than DungeonRandomScope
            // because the envelope shares no decision with the plan and runs
            // after it — it cannot perturb layout, and should not be able to.
            return new System.Random(seed ^ StairForge.StableHash($"cavern-envelope:{subject}"));
        }
    }
}
