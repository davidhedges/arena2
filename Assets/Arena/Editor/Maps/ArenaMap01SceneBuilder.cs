#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DungeonLab.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Arena.Editor.Maps
{
    /// <summary>
    /// Builds Arena_Map_01: one flat deck floating in the cavern,
    /// over an open lava sea.
    /// </summary>
    /// <remarks>
    /// UNRELATED TO THE DUNGEON GENERATOR, deliberately. This is a fixed,
    /// authored multiplayer map rather than a generated layout, topology, or
    /// recipe. Matchmaking and game modes may select it without owning it.
    ///
    /// What it DOES reuse is the cavern envelope, because the envelope is
    /// already downstream of whatever it encloses: it reads renderer bounds and
    /// nothing else, so a hand-built deck travels the same path a generated
    /// dungeon does.
    ///
    /// THE SERVER OWNS THE PLAYABLE SURFACE, NOT THIS SCENE. Every height here
    /// is chosen to agree with the authoritative flat y = 0 surface; the
    /// geometry is presentation only and authors no colliders at all.
    /// </remarks>
    public static class ArenaMap01SceneBuilder
    {
        internal const string SceneName = "Arena_Map_01";
        internal const string ScenePath = "Assets/Arena/Content/Scenes/Arena_Map_01.unity";
        internal const string DataKey = "arena_map_01";
        private const string ServerLayoutPath = "server/src/map_data/arena_map_01.layout.shared.json";
        private const string BundledCollisionPath =
            "Assets/Arena/Resources/SharedData/Maps/arena_map_01.collision.shared.json";
        private const string BundledQueryCollisionPath =
            "Assets/Arena/Resources/SharedData/Maps/arena_map_01.query_collision.shared.json";
        private const string CollisionRevisionNamePrefix = "Collision Revision ";

        [Serializable]
        private sealed class ServerLayoutFile
        {
            public string map_id = string.Empty;
            public ServerSpawnPointFile[] edge_spawn_points = Array.Empty<ServerSpawnPointFile>();
        }

        [Serializable]
        private sealed class CollisionRevisionLayout
        {
            public string source_revision = string.Empty;
        }

        [Serializable]
        private sealed class ServerSpawnPointFile
        {
            public float x = 0f;
            public float z = 0f;
        }

        /// <summary>
        /// The checked-in map whose edited camera rig and UI are preserved.
        /// </summary>
        /// <remarks>
        /// The builder starts in a new unsaved scene, opens the previous
        /// checked-in Arena_Map_01 additively, clones the four edited rig roots,
        /// closes that source, and only then overwrites the map. This preserves
        /// the 500m far clip and URP camera data without depending on an old
        /// example gameplay scene.
        /// </remarks>
        private const string RigTemplateScenePath = ScenePath;

        private static readonly string[] RigObjectNames =
        {
            "MainCamera",
            "PlayerFollowCamera",
            "UI_EventSystem",
            "UI_Canvas_StarterAssetsInputs_Joysticks",
        };

        private const string VolumeProfilePath =
            "Assets/Arena/Content/Settings/Rendering/OpenWorldProfiles/Arena_RandomDungeon_Profile.asset";

        private const string LavaMaterialPath =
            "Assets/Arena/Content/Art/Environment/Lava/M_Lava_FireShoreMagma.mat";

        // Twenty percent fewer repeats per axis makes each lava feature 25% larger.
        private const float LavaTilingMultiplier = 0.8f;

        /// <summary>
        /// Deck and skirt share one material, so the platform reads as a single
        /// slab of rock torn out of somewhere rather than a floor resting on a
        /// different stone.
        /// </summary>
        /// <remarks>
        /// Swappable by editing this one path — which is the whole reason the
        /// UV handling below reads the material instead of assuming a
        /// convention. Two different contracts are in play across the candidates:
        /// an Arena surface shader projects world XZ itself and ignores mesh
        /// UVs, while a plain URP Lit material tiles 1:1 off whatever the mesh
        /// carries. Guess wrong and the platform is either smeared or tiled at
        /// the wrong density, with nothing to indicate which.
        ///
        /// Used unmodified in either case. These materials are shared — the
        /// FireShore set with the surface demo scenes, the bundle set with the
        /// rest of the pack — so tiling is the mesh's job, never theirs.
        /// </remarks>
        private const string RockMaterialPath =
            "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/materials/M_MOD_Floor_01_v3.mat";

        /// <summary>
        /// The arena's descending walls. The pack's wall material, because that
        /// is what the entrances' own 109 wall pieces use — the deck's rim runs
        /// straight into them.
        /// </summary>
        private const string WallMaterialPath =
            "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/materials/M_MOD_Wall_01_v3.mat";

        /// <summary>
        /// The dungeon entrance cloned onto all four sides.
        /// </summary>
        /// <remarks>
        /// Taken from the pack's own demo scene rather than rebuilt out of kit
        /// pieces: it is 374 placed prefabs across walls, floors, stairs,
        /// columns, railings, props and FX, and any hand-rebuild would be a
        /// worse copy of something that already exists.
        /// </remarks>
        private const string EntranceScenePath =
            "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/scenes/" +
            "demoscene_dungeon_level_2_entrance.unity";

        private const string EntranceRootName = "level_entrance";

        /// <summary>
        /// Child group whose lowest point the lava is levelled with.
        /// </summary>
        private const string EntranceWallsGroupName = "walls";

        /// <summary>
        /// Depth, in metres, that the arena's walls continue below the lava
        /// surface, so they read as descending INTO it rather than resting on
        /// a skin at exactly its height.
        /// </summary>
        private const float WallSubmersion = 2f;

        /// <summary>
        /// Yaw per side. The entrance's HIGH end faces the deck.
        /// </summary>
        /// <remarks>
        /// These are the original yaws turned 180°, which swaps which end of the
        /// structure meets the arena: the landing at the top of the stairs now
        /// abuts the deck and the low mouth points outward over the lava, so the
        /// player leaves the arena and descends.
        ///
        /// The structure is anchored by MEASUREMENT rather than by a constant
        /// offset — it is 60m long and its two ends sit at different heights, so
        /// a number that suits one end is wrong for the other. The deck-facing
        /// extremity and the walkable height at THAT end are both read off the
        /// clone's floor renderers, then aligned to the deck edge and to y=0.
        /// </remarks>
        private static readonly (string Name, float Yaw)[] EntranceSides =
        {
            ("North", 180f),
            ("East", 270f),
            ("South", 0f),
            ("West", 90f),
        };

        /// <summary>Child group whose renderers define the walkable surface.</summary>
        private const string EntranceFloorsGroupName = "floors";

        /// <summary>
        /// How deep a slice at the deck-facing end counts as "the end", when
        /// reading the walkable height there. One kit cell.
        /// </summary>
        private const float EntranceEndSliceDepth = 4f;

        /// <summary>
        /// UV units per world metre to author when the rock material tiles off
        /// MESH UVs, so the texture repeats every 8m.
        /// </summary>
        /// <remarks>
        /// Unused when the material projects world XZ itself — then the repeat
        /// is the material's own <c>_BaseMap</c> tiling and the mesh cannot
        /// change it. See <see cref="RockMeshUvScale"/>.
        ///
        /// 4m, which is the kit's grid cell — one texture repeat per floor tile.
        /// The deck has to agree with the entrance floors it now runs into, and
        /// a brick pattern that changed size at the threshold would announce
        /// the join more loudly than any tiling does.
        /// </remarks>
        private const float RockUvScale = 0.25f;

        /// <summary>
        /// This map's authored volcanic panorama. Rebuilds retain the artwork;
        /// the procedural dungeon still generates its own independent backdrop.
        /// </summary>
        private const string BackdropAssetPath =
            "Assets/Arena/Content/Art/Generated/SurvivalCavernBackdrop.png";

        private const string SeedEnvironmentVariable = "ARENA_MAP_01_SEED";

        /// <summary>Envelope seed. Fixed, because this scene is checked in.</summary>
        private const int DefaultSeed = 5150;

        // ------------------------------------------------------------ geometry

        /// <summary>Half-width of the deck; 30 makes the requested 60x60.</summary>
        /// <remarks>
        /// The only place the arena's size is written down. The wall perimeter,
        /// the four entrance anchors and the footprint assertion all derive from
        /// it, so widening the arena is this one number.
        /// </remarks>
        private const float DeckHalfExtent = 30f;

        /// <summary>
        /// The walkable surface. Zero is not a style choice — it is
        /// `TrainingGroundMovementEnvironment.GroundY` and the server's flat
        /// layout height. Move it and characters float or sink.
        /// </summary>
        private const float DeckY = 0f;

        /// <summary>
        /// Keeps an NPC's centre safely on the deck while still presenting it
        /// inside the entrance threshold. This must agree with
        /// arena_map_01_layout.shared.json.
        /// </summary>
        private const float NpcSpawnInset = 2f;

        /// <summary>
        /// Deck tessellation. Linear fog is interpolated from the vertices, so
        /// this tracks the deck's size rather than being a fixed count — held at
        /// one cell per 2m, the density the 40x40 deck had.
        /// </summary>
        private static int DeckCells => Mathf.RoundToInt(DeckHalfExtent * 2f / 2f);

        /// <summary>
        /// Fallback drop to the lava, in dungeon elevations, used only if the
        /// entrances cannot be measured.
        /// </summary>
        /// <remarks>
        /// The lava height is normally DERIVED: its surface is levelled with the
        /// bottom of the entrances' descending walls, so the four structures sit
        /// in it rather than above or through it. That supersedes the original
        /// fixed ten-elevation drop, which no longer has anything to agree with.
        /// One elevation is <see cref="StairForge.LevelHeight"/>, so this is
        /// also metres.
        /// </remarks>
        private const int FallbackLavaDropElevations = 10;

        private static float FallbackLavaY => DeckY - FallbackLavaDropElevations * StairForge.LevelHeight;

        /// <summary>
        /// How far the lava reaches. Fog resolves it to the void long before
        /// this, so the number only has to outrun the fade, never be seen to
        /// end.
        /// </summary>
        private const float LavaExtent = 600f;

        private const int LavaCells = 60;

        /// <summary>
        /// Height of one wall course, matching the kit's `med` wall so the
        /// arena's own wall is split like the architecture it abuts.
        /// </summary>
        private const float WallCourseHeight = 4f;

        /// <summary>Perimeter samples per side of the deck.</summary>
        private const int WallColumnsPerSide = 32;

        // ------------------------------------------------------------ lighting

        /// <summary>
        /// The arena sits at the bottom of the descent: real lava is in frame,
        /// so the palette is the deep endpoint of the one curve rather than a
        /// second set of colours authored beside it.
        /// </summary>
        private const float ArenaDepth01 = 1f;

        /// <summary>
        /// Fog range, in metres. DELIBERATELY NOT the descent curve's values
        /// (7..27m at this depth): those are tuned for dungeon corridors, and a
        /// 40x40 arena is wider than the whole range — the far half of the deck
        /// would be solid fog and the mode is a fight across it. Starting past
        /// the deck's far corner keeps combat legible while still dissolving the
        /// lava sea into the void, which is the only thing fog is doing here.
        /// The cavern silhouettes are unaffected either way: their shader
        /// ignores RenderSettings.fog and bakes its own distance fade.
        /// </summary>
        private const float FogStart = 60f;

        private const float FogEnd = 220f;

        /// <summary>
        /// Broad, slightly cool reflected light from the vault. Combat needs a
        /// neutral value range above the deck, even with the lava light occluded
        /// underneath it. This reuses the existing shadow-free directional fill.
        /// </summary>
        private static readonly Color FillColor = new(0.82f, 0.87f, 1f);

        private const float FillIntensity = 0.7f;

        [MenuItem("Arena/Maps/Rebuild Arena Map 01", false, 100)]
        private static void RebuildFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Rebuild();
        }

        /// <summary>Entry point for `-executeMethod`.</summary>
        public static void RebuildArenaMap01Batch() => Rebuild();

        private static void Rebuild()
        {
            ValidateServerLayoutContract();
            int seed = ResolveSeed();
            CavernDepthProfile depth = CavernDepthProfile.Evaluate(ArenaDepth01);

            Scene destination = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject arena = new("Arena Map 01");
            SceneManager.MoveGameObjectToScene(arena, destination);
            arena.transform.position = Vector3.zero;

            // Everything the cavern has to enclose goes under one root, and the
            // lava stays OUT of it, BECAUSE the envelope sizes the whole cavern
            // from the renderer bounds of what it is handed. Given the lava sea
            // too it measured a 300m hull instead of a 20m one and pushed every
            // band out by 15x — the far band landed past the camera's far clip
            // and simply was not drawn. The lava is scenery inside the cavern,
            // never a thing the cavern is built around.
            GameObject structures = new("Structures");
            structures.transform.SetParent(arena.transform, worldPositionStays: false);
            structures.transform.localPosition = Vector3.zero;

            // FIRST, because everything below is measured off it: the lava
            // levels with the underside of these walls, and the arena's own
            // walls have to descend past that.
            float lavaY = BuildEntrances(destination, structures.transform);

            GameObject platform = new("Platform");
            platform.transform.SetParent(structures.transform, worldPositionStays: false);
            platform.transform.localPosition = Vector3.zero;

            BuildDeck(platform.transform);
            BuildArenaWalls(platform.transform, lavaY - WallSubmersion);
            AssertPlatformFootprint(platform);

            BuildLavaSea(arena.transform, lavaY);

            // Must run after the structures exist, for the bounds above. It
            // builds its OWN scene root and authors no colliders; nothing here
            // is a child of it or it of this.
            CreateVolcanicEnvelope(destination, structures, seed, depth);

            CreateSpawnMarker(destination);
            CloneGameplayRig(destination, depth);
            CreateLighting(depth);
            AssertNoColliders(destination);
            if (!SceneManager.SetActiveScene(destination))
                throw new InvalidOperationException("Failed to activate the authored arena scene for collision export.");
            GameplayCollisionExporter.PreparedSharedCollisionBake collisionBake =
                GameplayCollisionExporter.PrepareSceneSharedCollisionBake(destination);
            SetCollisionRevisionMetadata(destination, collisionBake.Revision);

            EditorSceneManager.MarkSceneDirty(destination);
            if (!EditorSceneManager.SaveScene(destination, ScenePath))
                throw new InvalidOperationException($"Failed to save arena map scene '{ScenePath}'.");

            AddSceneToBuildSettings(ScenePath);
            GameplayCollisionExporter.ExportPreparedActiveSceneArenaMapCollisionData(
                DataKey,
                collisionBake);
            AssetDatabase.SaveAssets();

            Debug.Log(
                "[ArenaMap01SceneBuilder] Built " + SceneName + ". " +
                $"seed={seed}; deck={DeckHalfExtent * 2f:0.#}x{DeckHalfExtent * 2f:0.#}m at y={DeckY:0.##}; " +
                $"entrances={EntranceSides.Length} (upper landings flush to the deck edge at y={DeckY:0.##}); " +
                $"lava at y={lavaY:0.##}, levelled with the entrance walls' underside; " +
                $"fog {FogStart:0.#}..{FogEnd:0.#}m; {depth.Summary}");
        }

        private static int ResolveSeed()
        {
            string? configured = Environment.GetEnvironmentVariable(SeedEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured))
                return DefaultSeed;

            if (!int.TryParse(configured.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                throw new InvalidOperationException(
                    $"[ArenaMap01SceneBuilder] {SeedEnvironmentVariable}='{configured}' is not an integer.");
            }

            return parsed;
        }

        private static void ValidateServerLayoutContract()
        {
            if (!File.Exists(ServerLayoutPath))
                throw new InvalidOperationException($"Arena map layout '{ServerLayoutPath}' is missing.");

            ServerLayoutFile? layout = JsonUtility.FromJson<ServerLayoutFile>(File.ReadAllText(ServerLayoutPath));
            if (layout == null)
                throw new InvalidOperationException($"Arena map layout '{ServerLayoutPath}' is invalid JSON.");
            if (!string.Equals(layout.map_id, "ARENA_MAP_01", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Arena map layout identity is '{layout.map_id}', expected 'ARENA_MAP_01'.");
            }
            if (layout.edge_spawn_points.Length != EntranceSides.Length)
            {
                throw new InvalidOperationException(
                    $"Arena map layout has {layout.edge_spawn_points.Length} spawn points; " +
                    $"the scene has {EntranceSides.Length} entrances.");
            }

            for (int index = 0; index < EntranceSides.Length; index++)
            {
                (string name, float yaw) = EntranceSides[index];
                Vector3 outward = Quaternion.Euler(0f, yaw, 0f) * Vector3.back;
                Vector3 expected = outward * (DeckHalfExtent - NpcSpawnInset);
                ServerSpawnPointFile actual = layout.edge_spawn_points[index];
                if (Mathf.Abs(actual.x - expected.x) > 0.001f
                    || Mathf.Abs(actual.z - expected.z) > 0.001f)
                {
                    throw new InvalidOperationException(
                        $"Arena map {name} entrance expects server spawn " +
                        $"({expected.x:0.###}, {expected.z:0.###}) but the layout contains " +
                        $"({actual.x:0.###}, {actual.z:0.###}).");
                }
            }
        }

        // ------------------------------------------------------------ the deck

        private static void BuildDeck(Transform parent)
        {
            Material rock = LoadMaterial(RockMaterialPath);
            float uvScale = RockMeshUvScale(rock);

            var vertices = new List<Vector3>((DeckCells + 1) * (DeckCells + 1));
            var uvs = new List<Vector2>(vertices.Capacity);
            var triangles = new List<int>(DeckCells * DeckCells * 6);

            for (int row = 0; row <= DeckCells; row++)
            {
                float z = Mathf.Lerp(-DeckHalfExtent, DeckHalfExtent, row / (float)DeckCells);
                for (int column = 0; column <= DeckCells; column++)
                {
                    float x = Mathf.Lerp(-DeckHalfExtent, DeckHalfExtent, column / (float)DeckCells);
                    vertices.Add(new Vector3(x, DeckY, z));
                    // A world-XZ projection, scaled to the platform's shared
                    // texel density. Written even when the shader projects for
                    // itself and ignores these: RecalculateTangents still reads
                    // them, and axis-aligned UVs put the tangent basis on the
                    // axes the normal map is sampled along.
                    uvs.Add(new Vector2(x, z) * uvScale);
                }
            }

            int stride = DeckCells + 1;
            for (int row = 0; row < DeckCells; row++)
            {
                for (int column = 0; column < DeckCells; column++)
                {
                    int origin = row * stride + column;
                    triangles.Add(origin);
                    triangles.Add(origin + stride);
                    triangles.Add(origin + 1);
                    triangles.Add(origin + 1);
                    triangles.Add(origin + stride);
                    triangles.Add(origin + stride + 1);
                }
            }

            Mesh mesh = BuildMesh("ArenaMap01Deck", vertices, uvs, triangles);
            CreateRenderer(
                parent,
                "Arena Deck",
                mesh,
                PlatformRock(rock, wantsMeshUv: false, "ArenaMap01Deck (Breakup)"),
                // This is a zero-thickness plane viewed by the underglow from
                // below, so its shadow caster must render both faces.
                shadowCastingMode: ShadowCastingMode.TwoSided);
        }

        /// <summary>
        /// The wall enclosing the arena, dropping from the deck edge straight
        /// down past the lava surface.
        /// </summary>
        /// <remarks>
        /// This replaces the tapered rock skirt the deck used to stand on. That
        /// shape said "torn-off chunk of ground floating in a void", which is
        /// the wrong sentence now that four pieces of built architecture join
        /// the deck: the arena is a structure, so its edge is a wall, and it
        /// descends INTO the lava rather than stopping short and hovering.
        ///
        /// Vertical and unjittered on purpose — it abuts kit walls that are
        /// themselves straight, and a wobbling rim beside them would read as a
        /// mistake rather than as texture.
        ///
        /// The faces cannot take the deck's XZ projection: that projection is
        /// constant along a vertical edge, so every side would smear one row of
        /// texels down its whole height. This mesh unwraps properly instead —
        /// perimeter arc length across, world height down — at the same scale
        /// the deck uses.
        /// </remarks>
        private static void BuildArenaWalls(Transform parent, float bottomY)
        {
            Material wall = LoadMaterial(WallMaterialPath);
            float uvScale = RockMeshUvScale(wall);

            Vector3[] perimeter = BuildPerimeter();
            int columns = perimeter.Length;

            var arcLength = new float[columns + 1];
            for (int column = 0; column < columns; column++)
            {
                arcLength[column + 1] = arcLength[column] +
                    Vector3.Distance(perimeter[column], perimeter[(column + 1) % columns]);
            }

            float[] courses = BuildWallCourseHeights(bottomY);
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            for (int course = 0; course < courses.Length - 1; course++)
            {
                float top = courses[course];
                float bottom = courses[course + 1];

                for (int column = 0; column < columns; column++)
                {
                    int next = (column + 1) % columns;
                    Vector3 near = perimeter[column];
                    Vector3 far = perimeter[next];

                    AppendQuad(
                        vertices, uvs, triangles,
                        new Vector3(near.x, top, near.z),
                        new Vector3(far.x, top, far.z),
                        new Vector3(far.x, bottom, far.z),
                        new Vector3(near.x, bottom, near.z),
                        new Vector2(arcLength[column], top) * uvScale,
                        new Vector2(arcLength[column + 1], top) * uvScale,
                        new Vector2(arcLength[column + 1], bottom) * uvScale,
                        new Vector2(arcLength[column], bottom) * uvScale);
                }
            }

            AppendWallUnderside(vertices, uvs, triangles, perimeter, bottomY, uvScale);

            Mesh mesh = BuildMesh("ArenaMap01Walls", vertices, uvs, triangles);
            CreateRenderer(
                parent,
                "Arena Walls",
                mesh,
                PlatformRock(wall, wantsMeshUv: true, "ArenaMap01Walls (Mesh UV)"));
        }

        /// <summary>
        /// Heights the wall is split at, deck downward. Split on the kit's own
        /// course height so the wall tessellates like the architecture it runs
        /// into, and so linear fog — interpolated from the vertices — has
        /// something to interpolate across on a wall this tall.
        /// </summary>
        private static float[] BuildWallCourseHeights(float bottomY)
        {
            var heights = new List<float> { DeckY };
            for (float y = DeckY - WallCourseHeight; y > bottomY + 0.01f; y -= WallCourseHeight)
                heights.Add(y);

            heights.Add(bottomY);
            return heights.ToArray();
        }

        /// <summary>
        /// Closes the bottom. The player never gets under the arena, but an open
        /// shell shows its own interior the moment the camera clips through the
        /// wall — the same tell the envelope's capped spires exist to avoid.
        /// </summary>
        private static void AppendWallUnderside(
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> triangles,
            Vector3[] perimeter,
            float bottomY,
            float uvScale)
        {
            int centre = vertices.Count;
            vertices.Add(new Vector3(0f, bottomY, 0f));
            uvs.Add(Vector2.zero);

            foreach (Vector3 rim in perimeter)
            {
                vertices.Add(new Vector3(rim.x, bottomY, rim.z));
                uvs.Add(new Vector2(rim.x, rim.z) * uvScale);
            }

            for (int column = 0; column < perimeter.Length; column++)
            {
                int next = (column + 1) % perimeter.Length;
                triangles.Add(centre);
                triangles.Add(centre + 1 + column);
                triangles.Add(centre + 1 + next);
            }
        }

        /// <summary>The deck outline, walked once counter-clockwise.</summary>
        private static Vector3[] BuildPerimeter()
        {
            var perimeter = new List<Vector3>(WallColumnsPerSide * 4);
            (float X, float Z)[] corners =
            {
                (-DeckHalfExtent, -DeckHalfExtent),
                (DeckHalfExtent, -DeckHalfExtent),
                (DeckHalfExtent, DeckHalfExtent),
                (-DeckHalfExtent, DeckHalfExtent),
            };

            for (int side = 0; side < corners.Length; side++)
            {
                (float X, float Z) from = corners[side];
                (float X, float Z) to = corners[(side + 1) % corners.Length];
                for (int step = 0; step < WallColumnsPerSide; step++)
                {
                    float t = step / (float)WallColumnsPerSide;
                    perimeter.Add(new Vector3(
                        Mathf.Lerp(from.X, to.X, t),
                        DeckY,
                        Mathf.Lerp(from.Z, to.Z, t)));
                }
            }

            return perimeter.ToArray();
        }

        // ------------------------------------------------------- the entrances

        /// <summary>
        /// Clones the pack's level entrance onto all four sides of the deck and
        /// returns the height its descending walls bottom out at.
        /// </summary>
        /// <remarks>
        /// Each copy is placed by putting its measured mouth
        /// (<see cref="EntranceMouthLocalZ"/>) exactly on the deck edge, so the
        /// entrance floor continues the deck rather than overlapping or
        /// floating off it. No vertical offset is applied or wanted: the
        /// entrance's lower walkable surface is already y=0, which is what the
        /// server's flat layout says the deck is.
        /// </remarks>
        private static float BuildEntrances(Scene destination, Transform parent)
        {
            Scene template = EditorSceneManager.OpenScene(EntranceScenePath, OpenSceneMode.Additive);
            float wallsBottom = float.PositiveInfinity;
            try
            {
                GameObject? source = template
                    .GetRootGameObjects()
                    .FirstOrDefault(root => string.Equals(root.name, EntranceRootName, StringComparison.Ordinal));
                if (source == null)
                {
                    throw new InvalidOperationException(
                        $"Entrance scene '{EntranceScenePath}' has no root object named '{EntranceRootName}'.");
                }

                foreach ((string name, float yaw) in EntranceSides)
                {
                    GameObject clone = UnityEngine.Object.Instantiate(source);
                    clone.name = $"Level Entrance {name}";
                    SceneManager.MoveGameObjectToScene(clone, destination);
                    clone.transform.SetParent(parent, worldPositionStays: false);
                    StripDemoSceneRig(clone);

                    // Measured while the clone is still at identity, so renderer
                    // bounds ARE its local bounds and no inverse transform is
                    // needed to reason about them.
                    Bounds floors = MeasureGroupBounds(clone, EntranceFloorsGroupName);
                    float deckFacingZ = floors.max.z;
                    float surfaceY = MeasureWalkableHeightAtEnd(clone, deckFacingZ);

                    Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
                    // Local +Z is turned to face INWARD by these yaws, so the
                    // structure's own back is the side that reaches the arena.
                    Vector3 outward = rotation * Vector3.back;
                    clone.transform.rotation = rotation;

                    // Land the deck-facing floor corner exactly on the deck edge
                    // at deck height: flush in plan and in section at once.
                    Vector3 edgeCentre = outward * DeckHalfExtent + Vector3.up * DeckY;
                    clone.transform.position =
                        edgeCentre - rotation * new Vector3(0f, surfaceY, deckFacingZ);

                    CreateNpcSpawnMarker(parent, name, outward);

                    wallsBottom = Mathf.Min(wallsBottom, MeasureWallsBottom(clone));
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(template, removeScene: true);
                SceneManager.SetActiveScene(destination);
            }

            if (float.IsInfinity(wallsBottom))
                return FallbackLavaY;

            return wallsBottom;
        }

        private static void CreateNpcSpawnMarker(Transform parent, string entranceName, Vector3 outward)
        {
            // Presentation-only marker for the server-authored point in
            // arena_map_01_layout.shared.json. Looking inward gives every mode
            // a stable orientation for actors entering through this socket.
            GameObject marker = new($"Arena Map 01 Entry {entranceName} (server-authoritative)");
            marker.transform.SetParent(parent, worldPositionStays: false);
            marker.transform.localPosition = outward * (DeckHalfExtent - NpcSpawnInset)
                                             + Vector3.up * DeckY;
            marker.transform.localRotation = Quaternion.LookRotation(-outward, Vector3.up);
        }

        /// <summary>
        /// Removes what belongs to the demo scene rather than to the entrance.
        /// </summary>
        /// <remarks>
        /// DIRECTIONAL LIGHTS, because the entrance ships with the demo's own
        /// key light and four copies of it would each light the entire cavern
        /// from its own angle — parallel shadows from four directions, in a
        /// scene whose whole lighting premise is one underglow from below.
        /// Point lights stay: they are local, and read as the entrance being lit
        /// from within. Their real-time shadows do not stay. A point-light
        /// shadow renders six cube faces, so the entrance's sixty local lights
        /// otherwise overwhelm URP's additional-light shadow atlas.
        ///
        /// COLLIDERS, for the reason the whole scene authors none — this map
        /// movement is the server's flat layout mirrored by
        /// `TrainingGroundMovementEnvironment`, so scene collision is never
        /// consulted, and anything left here could only reach a future export
        /// and start blocking sight or projectiles across an arena whose
        /// premise is that it is open.
        /// </remarks>
        private static void StripDemoSceneRig(GameObject entrance)
        {
            foreach (Light light in entrance.GetComponentsInChildren<Light>(includeInactive: true))
            {
                if (light.type == LightType.Directional)
                {
                    UnityEngine.Object.DestroyImmediate(light.gameObject);
                    continue;
                }

                // Runtime graphics settings may opt one curated hero light per
                // entrance back into shadows. Never restore all point shadows.
                if (light.type == LightType.Point)
                    light.shadows = LightShadows.None;
            }

            // ArenaMap01LightingBudget replaces these identical always-running
            // Animator graphs with one shared 15 Hz light-intensity sample.
            foreach (Animator animator in entrance.GetComponentsInChildren<Animator>(includeInactive: true))
                if (animator.GetComponent<Light>() != null)
                    animator.enabled = false;

            foreach (Collider collider in entrance.GetComponentsInChildren<Collider>(includeInactive: true))
                UnityEngine.Object.DestroyImmediate(collider);
        }

        private static float MeasureWallsBottom(GameObject entrance) =>
            MeasureGroupBounds(entrance, EntranceWallsGroupName).min.y;

        private static Renderer[] GroupRenderers(GameObject entrance, string groupName)
        {
            Transform? group = entrance
                .GetComponentsInChildren<Transform>(includeInactive: true)
                .FirstOrDefault(child => string.Equals(child.name, groupName, StringComparison.Ordinal));
            if (group == null)
            {
                throw new InvalidOperationException(
                    $"Entrance '{entrance.name}' has no '{groupName}' group, so it cannot be placed or " +
                    "levelled against.");
            }

            Renderer[] renderers = group.GetComponentsInChildren<Renderer>(includeInactive: false);
            if (renderers.Length == 0)
                throw new InvalidOperationException($"Entrance '{entrance.name}' group '{groupName}' has no renderers.");

            return renderers;
        }

        private static Bounds MeasureGroupBounds(GameObject entrance, string groupName)
        {
            Renderer[] renderers = GroupRenderers(entrance, groupName);
            Bounds bounds = renderers[0].bounds;
            for (int index = 1; index < renderers.Length; index++)
                bounds.Encapsulate(renderers[index].bounds);

            return bounds;
        }

        /// <summary>
        /// The height you would stand at on the deck-facing end of the entrance.
        /// </summary>
        /// <remarks>
        /// Read from a slice at that end rather than from the whole floors
        /// group, whose range spans the entire staircase — the group's extremes
        /// describe the bottom of the run and the top of the landing at once,
        /// and neither is the height of the end being joined.
        /// </remarks>
        private static float MeasureWalkableHeightAtEnd(GameObject entrance, float deckFacingZ)
        {
            float surface = float.NegativeInfinity;
            foreach (Renderer renderer in GroupRenderers(entrance, EntranceFloorsGroupName))
            {
                if (renderer.bounds.max.z < deckFacingZ - EntranceEndSliceDepth)
                    continue;

                surface = Mathf.Max(surface, renderer.bounds.max.y);
            }

            if (float.IsNegativeInfinity(surface))
            {
                throw new InvalidOperationException(
                    $"Entrance '{entrance.name}' has no floor within {EntranceEndSliceDepth}m of its " +
                    "deck-facing end, so its walkable height there cannot be measured.");
            }

            return surface;
        }

        // ------------------------------------------------------------ the lava

        private static void BuildLavaSea(Transform parent, float lavaY)
        {
            float half = LavaExtent * 0.5f;
            var vertices = new List<Vector3>((LavaCells + 1) * (LavaCells + 1));
            var uvs = new List<Vector2>(vertices.Capacity);
            var triangles = new List<int>(LavaCells * LavaCells * 6);

            for (int row = 0; row <= LavaCells; row++)
            {
                float z = Mathf.Lerp(-half, half, row / (float)LavaCells);
                for (int column = 0; column <= LavaCells; column++)
                {
                    float x = Mathf.Lerp(-half, half, column / (float)LavaCells);
                    vertices.Add(new Vector3(x, lavaY, z));
                    uvs.Add(new Vector2(x, z));
                }
            }

            int stride = LavaCells + 1;
            for (int row = 0; row < LavaCells; row++)
            {
                for (int column = 0; column < LavaCells; column++)
                {
                    int origin = row * stride + column;
                    triangles.Add(origin);
                    triangles.Add(origin + stride);
                    triangles.Add(origin + 1);
                    triangles.Add(origin + 1);
                    triangles.Add(origin + stride);
                    triangles.Add(origin + stride + 1);
                }
            }

            Mesh mesh = BuildMesh("ArenaMap01LavaSea", vertices, uvs, triangles);
            // A scene-owned copy keeps this arena's scale independent of the
            // shared surface-demo material. All lava layers share _BaseMap UVs.
            Material lava = new(LoadMaterial(LavaMaterialPath)) { name = "Arena Map 01 Lava" };
            lava.SetTextureScale("_BaseMap", lava.GetTextureScale("_BaseMap") * LavaTilingMultiplier);
            lava.SetFloat("_TilingBreakup", 1f);
            CreateRenderer(parent, "Lava Sea", mesh, lava);
        }

        // ------------------------------------------------------------ plumbing

        private static void AppendQuad(
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> triangles,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 d,
            Vector2 uvA,
            Vector2 uvB,
            Vector2 uvC,
            Vector2 uvD)
        {
            int origin = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            uvs.Add(uvA);
            uvs.Add(uvB);
            uvs.Add(uvC);
            uvs.Add(uvD);

            // a-b-c-d is given clockwise seen from OUTSIDE the deck, which is
            // exactly Unity's front-facing winding, so the fans go in order.
            // Reversed, every skirt face points into the rock and the whole
            // underside renders as backfaces.
            triangles.Add(origin);
            triangles.Add(origin + 1);
            triangles.Add(origin + 2);
            triangles.Add(origin);
            triangles.Add(origin + 2);
            triangles.Add(origin + 3);
        }

        private static Mesh BuildMesh(
            string name,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> triangles)
        {
            Mesh mesh = new() { name = name };
            mesh.indexFormat = vertices.Count > ushort.MaxValue
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            // After normals and with UVs present: the surface shaders sample a
            // normal map, and a mesh with no tangents lights as if it had none.
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// True when the material projects world XZ in the shader and ignores
        /// whatever UVs the mesh carries.
        /// </summary>
        private static bool RockIsWorldProjected(Material rock) =>
            rock.HasProperty("_WorldSpaceUV") && rock.GetFloat("_WorldSpaceUV") > 0.5f;

        /// <summary>
        /// UV units per world metre this mesh should author.
        /// </summary>
        /// <remarks>
        /// One for a world-projecting material: its own <c>_BaseMap</c> tiling
        /// is the whole scale, so UVs in raw metres put the skirt at exactly the
        /// density the deck gets for free. <see cref="RockUvScale"/> otherwise,
        /// where the mesh is the only thing that decides.
        /// </remarks>
        private static float RockMeshUvScale(Material rock) =>
            RockIsWorldProjected(rock) ? 1f : RockUvScale;

        /// <summary>
        /// The material for one platform surface: the shared rock asset, copied
        /// only where this scene has to change something on it.
        /// </summary>
        /// <remarks>
        /// Scene-embedded rather than checked-in .mat variants. Both changes are
        /// properties of THIS platform — how big it is and how its faces are
        /// unwrapped — not of the rock, and the source is shared with the
        /// surface demo scenes.
        ///
        /// Tiling breakup is on because the deck is 40m across at a ~2.9m
        /// repeat: fourteen copies of a distinctive crack web, which reads as a
        /// grid immediately. It costs five extra samples and is a shader
        /// feature, so nothing else that uses this material pays for it.
        /// </remarks>
        private static Material PlatformRock(Material source, bool wantsMeshUv, string name)
        {
            bool needsMeshUv = wantsMeshUv && RockIsWorldProjected(source);
            bool canBreakUp = source.HasProperty("_TilingBreakup");
            if (!needsMeshUv && !canBreakUp)
                return source;

            Material material = new(source) { name = name };
            if (needsMeshUv)
                material.SetFloat("_WorldSpaceUV", 0f);

            if (canBreakUp)
            {
                material.SetFloat("_TilingBreakup", 1f);
                // [Toggle] drives a shader_feature keyword, and a material
                // built in code does not get one from the float. Without this
                // the cheap variant compiles and the breakup silently does
                // nothing at all.
                material.EnableKeyword("_TILINGBREAKUP_ON");
            }

            return material;
        }

        private static Material LoadMaterial(string path)
        {
            Material? material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
                throw new InvalidOperationException($"Required arena map material '{path}' is missing.");

            return material;
        }

        /// <summary>
        /// No collider, on any of it. The authoritative movement surface is the
        /// server's flat layout mirrored by
        /// `TrainingGroundMovementEnvironment`, so scene collision would not be
        /// consulted for movement — but it WOULD be swept into a collision or
        /// query export if this map gets one, and query geometry blocks
        /// line of sight and projectiles. An arena whose premise is "flat and
        /// open" authors none.
        /// </summary>
        private static void CreateRenderer(
            Transform parent,
            string name,
            Mesh mesh,
            Material material,
            ShadowCastingMode shadowCastingMode = ShadowCastingMode.Off)
        {
            GameObject surface = new(name);
            surface.transform.SetParent(parent, worldPositionStays: false);
            surface.transform.localPosition = Vector3.zero;
            surface.transform.localRotation = Quaternion.identity;

            surface.AddComponent<MeshFilter>().sharedMesh = mesh;

            MeshRenderer renderer = surface.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = shadowCastingMode;
            renderer.receiveShadows = false;
        }

        private static void CreateSpawnMarker(Scene destination)
        {
            // Named for the value it has to agree with. The server spawns the
            // player from `arena_map_01_layout.shared.json`, not from this
            // object; it exists so the agreement is visible in the hierarchy
            // instead of only in two files that never mention each other.
            GameObject marker = new("Arena Map 01 Player Spawn (server-authoritative)");
            SceneManager.MoveGameObjectToScene(marker, destination);
            marker.transform.position = new Vector3(0f, DeckY, 0f);
        }

        private static void CloneGameplayRig(Scene destination, CavernDepthProfile depth)
        {
            Scene template = EditorSceneManager.OpenScene(RigTemplateScenePath, OpenSceneMode.Additive);
            try
            {
                GameObject[] roots = template.GetRootGameObjects();
                foreach (string name in RigObjectNames)
                {
                    GameObject? source = roots.FirstOrDefault(
                        root => string.Equals(root.name, name, StringComparison.Ordinal));
                    if (source == null)
                    {
                        throw new InvalidOperationException(
                            $"Rig template '{RigTemplateScenePath}' has no root object named '{name}'.");
                    }

                    GameObject clone = UnityEngine.Object.Instantiate(source);
                    clone.name = source.name;
                    SceneManager.MoveGameObjectToScene(clone, destination);

                    if (string.Equals(name, "MainCamera", StringComparison.Ordinal))
                        ConfigureCamera(clone, depth);
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(template, removeScene: true);
                SceneManager.SetActiveScene(destination);
            }
        }

        private static void ConfigureCamera(GameObject mainCamera, CavernDepthProfile depth)
        {
            Camera camera = mainCamera.GetComponent<Camera>()
                ?? throw new InvalidOperationException("The cloned map MainCamera has no Camera component.");
            camera.clearFlags = CameraClearFlags.Skybox;
            // Equal to the fog colour by contract. A clear colour darker than
            // the fog makes distant geometry fade UP into a brighter value,
            // which is the signature of outdoor haze and reads as sky.
            camera.backgroundColor = depth.Background;
            camera.allowHDR = true;
            camera.allowMSAA = true;

            UniversalAdditionalCameraData cameraData = mainCamera.GetComponent<UniversalAdditionalCameraData>()
                ?? throw new InvalidOperationException("The cloned map MainCamera has no URP camera data.");
            // The map needs post-processing because the lava's emission is
            // HDR and reads as flat orange paint without bloom to spend it on.
            cameraData.renderPostProcessing = true;
            cameraData.antialiasing = AntialiasingMode.None;

            // Keep the dungeon's source profile intact. The arena's scene-owned
            // grade retains lava bloom but avoids crushed combat silhouettes,
            // blue-shifted team colours and a heavily darkened screen perimeter.
            VolumeProfile? profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            if (profile == null)
                throw new InvalidOperationException($"Required Volume profile '{VolumeProfilePath}' is missing.");

            Volume volume = mainCamera.GetComponent<Volume>() ?? mainCamera.AddComponent<Volume>();
            volume.enabled = true;
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.blendDistance = 0f;
            volume.weight = 1f;
            VolumeProfile combatProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            combatProfile.name = "Arena Map 01 Combat Grade";
            foreach (VolumeComponent component in profile.components)
                combatProfile.components.Add(UnityEngine.Object.Instantiate(component));
            if (combatProfile.TryGet(out Bloom bloom))
            {
                bloom.threshold.Override(0.9f);
                bloom.intensity.Override(0.85f);
            }
            if (combatProfile.TryGet(out Vignette vignette))
            {
                vignette.intensity.Override(0.18f);
                vignette.smoothness.Override(0.35f);
            }
            if (combatProfile.TryGet(out LiftGammaGain grade))
            {
                grade.lift.Override(new Vector4(1f, 1f, 1f, 0f));
                grade.gamma.Override(new Vector4(1f, 1f, 1f, 0f));
                grade.gain.Override(new Vector4(1f, 1f, 1f, 0f));
            }
            volume.sharedProfile = combatProfile;
        }

        private static void CreateVolcanicEnvelope(
            Scene destination, GameObject structures, int seed, CavernDepthProfile depth)
        {
            DungeonCavernEnvelope.Build(
                destination, structures, seed, depth,
                buildGlowPool: false,
                backdropAssetPath: BackdropAssetPath,
                underglowShadows: LightShadows.Hard,
                generateBackdrop: false);
            CavernBackdrop.ApplyAuthored(BackdropAssetPath);
            RenderSettings.skybox.SetFloat("_Exposure", 0.32f);
            RefineVolcanicSpireSurfaces(destination);
        }

        private static void RefineVolcanicSpireSurfaces(Scene scene)
        {
            const string cliffFolder = "Assets/ThirdParty/AssetStore/Environments/" +
                "StylizedMaterialsBundle/Textures/FireShore/FireShore_CrackedCliff/";
            Texture2D rock = AssetDatabase.LoadAssetAtPath<Texture2D>(cliffFolder + "T_FireShore_CrackedCliff_D.tga")
                ?? throw new InvalidOperationException("The volcanic cliff albedo is missing.");
            Texture2D ember = AssetDatabase.LoadAssetAtPath<Texture2D>(cliffFolder + "T_FireShore_CrackedCliff_E.tga")
                ?? throw new InvalidOperationException("The volcanic cliff emission is missing.");
            GameObject envelope = scene.GetRootGameObjects().Single(root => root.name == DungeonCavernEnvelope.RootName);
            foreach (MeshFilter filter in envelope.GetComponentsInChildren<MeshFilter>())
            {
                Mesh source = filter.sharedMesh;
                if (!source.name.StartsWith("CavernSpire_", StringComparison.Ordinal)
                    && !source.name.StartsWith("CavernStalactite_", StringComparison.Ordinal))
                    continue;
                filter.sharedMesh = BuildBasaltSpire(source);
                Material material = filter.GetComponent<MeshRenderer>().sharedMaterial;
                material.SetFloat("_RockDetail", 1f);
                material.SetTexture("_RockMap", rock);
                material.SetTexture("_EmberMap", ember);
                material.SetFloat("_RockTiling", 0.025f);
            }
        }

        private static Mesh BuildBasaltSpire(Mesh source)
        {
            // Retain each formation's anchor, height and depth band. The more
            // irregular shoulders, fluted sides and leaning tip break the old
            // straight cone outline; all of this stays outside gameplay geometry.
            int seed = 17;
            foreach (char character in source.name)
                seed = unchecked(seed * 31 + character);
            System.Random rng = new(seed);
            bool hanging = source.bounds.center.y < 0f;
            float sign = hanging ? -1f : 1f;
            float height = source.bounds.size.y;
            float radius = Mathf.Max(source.bounds.extents.x, source.bounds.extents.z);
            const int sides = 12;
            const int rings = 13;
            var vertices = new Vector3[sides * rings];
            var colours = new Color[vertices.Length];
            var triangles = new List<int>(sides * (rings - 1) * 6 + (sides - 2) * 6);
            var flutes = new float[sides];
            for (int side = 0; side < sides; side++)
                flutes[side] = Mathf.Lerp(0.62f, 1.15f, (float)rng.NextDouble());
            Vector2 lean = new((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f);
            Color rootColour = source.colors[0];
            Color tipColour = source.colors[source.vertexCount - 1];
            // The old upper vertices were almost black. A faint ash-grey bounce
            // reveals stone planes against the detailed ceiling without emission.
            Color ash = new(0.028f, 0.025f, 0.026f);
            for (int ring = 0; ring < rings; ring++)
            {
                float t = ring / (float)(rings - 1);
                float taper = Mathf.Max(0.018f, Mathf.Pow(1f - t, 0.78f));
                float shelf = (ring % 3 == 1 ? 1.12f : 0.93f) * Mathf.Lerp(0.88f, 1.12f, (float)rng.NextDouble());
                // Extend the broad hanging roots upward into the vault; a flat
                // cap suspended in empty space was a particularly visible tell.
                float y = sign * height * t + (hanging ? height * 0.65f * Mathf.Pow(1f - t, 4f) : 0f);
                for (int side = 0; side < sides; side++)
                {
                    float angle = side * Mathf.PI * 2f / sides + Mathf.Sin(t * 5f) * 0.09f;
                    float r = radius * taper * shelf * flutes[side];
                    vertices[ring * sides + side] = new Vector3(
                        Mathf.Cos(angle) * r + lean.x * radius * t * t,
                        y + Mathf.Sin(side * 2.4f + ring) * height * 0.009f * Mathf.Sin(t * Mathf.PI),
                        Mathf.Sin(angle) * r + lean.y * radius * t * t);
                    Color colour = Color.Lerp(rootColour, tipColour, t);
                    colour = Color.Lerp(colour, ash, hanging ? 0.64f : 0.85f);
                    colours[ring * sides + side] = colour;
                }
            }
            for (int ring = 0; ring < rings - 1; ring++)
            for (int side = 0; side < sides; side++)
            {
                int a = ring * sides + side;
                int b = ring * sides + (side + 1) % sides;
                int c = a + sides;
                int d = b + sides;
                triangles.AddRange(new[] { a, c, b, b, c, d });
            }
            for (int side = 1; side < sides - 1; side++)
            {
                triangles.AddRange(new[] { 0, side, side + 1 });
                int top = (rings - 1) * sides;
                triangles.AddRange(new[] { top, top + side + 1, top + side });
            }
            if (hanging)
                for (int index = 0; index < triangles.Count; index += 3)
                    (triangles[index + 1], triangles[index + 2]) = (triangles[index + 2], triangles[index + 1]);
            Mesh mesh = new() { name = source.name + "_Basalt" };
            mesh.vertices = vertices;
            mesh.colors = colours;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void CreateLighting(CavernDepthProfile depth)
        {
            GameObject fill = SceneManager.GetActiveScene().GetRootGameObjects()
                .FirstOrDefault(root => root.name == "Cavern Fill Light") ?? new GameObject("Cavern Fill Light");
            fill.transform.position = new Vector3(0f, 12f, 0f);
            fill.transform.rotation = Quaternion.Euler(64f, -34f, 0f);

            Light light = fill.GetComponent<Light>() ?? fill.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = FillColor;
            light.intensity = FillIntensity;
            light.shadows = LightShadows.None;
            light.bounceIntensity = 0f;

            // RenderSettings.skybox is NOT touched: CavernBackdrop owns it and
            // has already run. Assigning here — even null — wipes the generated
            // panorama and drops the camera back to the flat clear colour.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.28f, 0.31f, 0.35f);
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.ambientEquatorColor = new Color(0.20f, 0.21f, 0.235f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.13f, 0.085f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = depth.FogColor;
            RenderSettings.fogDensity = 0.01f;
            RenderSettings.fogStartDistance = FogStart;
            RenderSettings.fogEndDistance = FogEnd;
        }

        /// <summary>
        /// Proves the envelope is about to be sized off the deck and nothing
        /// else.
        /// </summary>
        /// <remarks>
        /// This exists because the failure it catches is SILENT. The envelope
        /// takes whatever renderer bounds it is handed, so parenting one more
        /// surface under the platform — the lava sea did exactly this — simply
        /// produces a cavern at the wrong scale, with no error anywhere. The
        /// only symptom was a hull radius in a log line nobody had to read.
        /// </remarks>
        private static void AssertPlatformFootprint(GameObject platform)
        {
            Renderer[] renderers = platform.GetComponentsInChildren<Renderer>(includeInactive: false);
            if (renderers.Length == 0)
                throw new InvalidOperationException("The arena platform built no renderers to enclose.");

            Bounds bounds = renderers[0].bounds;
            for (int index = 1; index < renderers.Length; index++)
                bounds.Encapsulate(renderers[index].bounds);

            float hullRadius = Mathf.Max(bounds.extents.x, bounds.extents.z);
            if (Mathf.Abs(hullRadius - DeckHalfExtent) > 0.5f || bounds.max.y > DeckY + 0.001f)
            {
                throw new InvalidOperationException(
                    $"The arena platform measures hullRadius={hullRadius:0.#}m, top y={bounds.max.y:0.##}; " +
                    $"expected {DeckHalfExtent:0.#}m and {DeckY:0.##}. The cavern envelope is sized from these " +
                    "bounds, so anything else parented under the platform silently rescales the whole cavern.");
            }
        }

        private static void AssertNoColliders(Scene destination)
        {
            Collider[] colliders = destination
                .GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Collider>(includeInactive: true))
                .ToArray();
            if (colliders.Length == 0)
                return;

            throw new InvalidOperationException(
                $"The arena map scene authored {colliders.Length} collider(s): " +
                string.Join(", ", colliders.Take(8).Select(collider => collider.gameObject.name)) +
                ". The arena's movement surface is the server's flat layout; scene collision here can only " +
                "reach an export and start blocking movement, line of sight or projectiles.");
        }

        internal static bool IsCollisionRevisionCurrent(out string failure)
        {
            if (!File.Exists(ScenePath))
            {
                failure = $"scene '{ScenePath}' is missing";
                return false;
            }

            string? sceneRevision = File.ReadLines(ScenePath)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("m_Name: ", StringComparison.Ordinal))
                .Select(line => line.Substring("m_Name: ".Length))
                .FirstOrDefault(name => name.StartsWith(CollisionRevisionNamePrefix, StringComparison.Ordinal))?
                .Substring(CollisionRevisionNamePrefix.Length);
            if (string.IsNullOrWhiteSpace(sceneRevision))
            {
                failure = "the saved scene has no collision revision marker";
                return false;
            }

            foreach (string path in new[] { BundledCollisionPath, BundledQueryCollisionPath })
            {
                if (!File.Exists(path))
                {
                    failure = $"'{path}' is missing";
                    return false;
                }

                CollisionRevisionLayout? layout =
                    JsonUtility.FromJson<CollisionRevisionLayout>(File.ReadAllText(path));
                if (layout == null
                    || !string.Equals(layout.source_revision, sceneRevision, StringComparison.Ordinal))
                {
                    failure = $"'{path}' does not match scene revision {sceneRevision}";
                    return false;
                }
            }

            failure = string.Empty;
            return true;
        }

        [MenuItem("Arena/Maps/Repair Arena Map 01 Collision From Saved Scene", false, 190)]
        private static void RepairCollisionFromSavedSceneMenu()
        {
            TryRepairCollisionFromSavedScene();
        }

        internal static bool TryRepairCollisionFromSavedScene()
        {
            if (Application.isBatchMode
                || EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError(
                    "[ArenaMap01SceneBuilder] Cannot repair saved collision while Unity is " +
                    "compiling, importing, or changing Play mode.");
                return false;
            }

            Scene previousActiveScene = SceneManager.GetActiveScene();
            Scene mapScene = SceneManager.GetSceneByPath(ScenePath);
            bool openedForRepair = !mapScene.IsValid() || !mapScene.isLoaded;
            try
            {
                if (openedForRepair)
                    mapScene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
                if (!mapScene.IsValid() || !mapScene.isLoaded || !SceneManager.SetActiveScene(mapScene))
                    throw new InvalidOperationException($"Unable to load '{ScenePath}' for collision repair.");

                AssertNoColliders(mapScene);
                GameplayCollisionExporter.PreparedSharedCollisionBake collisionBake =
                    GameplayCollisionExporter.PrepareSceneSharedCollisionBake(mapScene);
                SetCollisionRevisionMetadata(mapScene, collisionBake.Revision);
                GameplayCollisionExporter.ExportPreparedActiveSceneArenaMapCollisionData(
                    DataKey,
                    collisionBake);
                EditorSceneManager.MarkSceneDirty(mapScene);
                if (!EditorSceneManager.SaveScene(mapScene, ScenePath))
                    throw new InvalidOperationException($"Failed to save repaired scene '{ScenePath}'.");
                AssetDatabase.SaveAssets();
                Debug.Log(
                    "[ArenaMap01SceneBuilder] Repaired Arena_Map_01 collision from the saved scene at " +
                    $"revision {collisionBake.Revision}.");
                return true;
            }
            catch (Exception error)
            {
                Debug.LogError("[ArenaMap01SceneBuilder] Collision repair failed: " + error);
                return false;
            }
            finally
            {
                if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                    SceneManager.SetActiveScene(previousActiveScene);
                if (openedForRepair && mapScene.IsValid() && mapScene.isLoaded)
                    EditorSceneManager.CloseScene(mapScene, removeScene: true);
            }
        }

        private static void SetCollisionRevisionMetadata(Scene scene, string revision)
        {
            GameObject metadata = scene.GetRootGameObjects()
                .FirstOrDefault(root => string.Equals(root.name, "Arena Map 01", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Arena_Map_01 has no map metadata root.");
            foreach (Transform child in metadata.transform.Cast<Transform>()
                         .Where(child => child.name.StartsWith(CollisionRevisionNamePrefix, StringComparison.Ordinal))
                         .ToArray())
            {
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }

            GameObject marker = new($"{CollisionRevisionNamePrefix}{revision}");
            marker.transform.SetParent(metadata.transform, worldPositionStays: false);
            marker.transform.localPosition = Vector3.zero;
            EditorSceneManager.MarkSceneDirty(scene);
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            int existingIndex = Array.FindIndex(
                scenes,
                scene => string.Equals(scene.path, scenePath, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                if (!scenes[existingIndex].enabled)
                    scenes[existingIndex] = new EditorBuildSettingsScene(scenePath, enabled: true);
            }
            else
            {
                Array.Resize(ref scenes, scenes.Length + 1);
                scenes[^1] = new EditorBuildSettingsScene(scenePath, enabled: true);
            }

            EditorBuildSettings.scenes = scenes;
        }
    }
}
