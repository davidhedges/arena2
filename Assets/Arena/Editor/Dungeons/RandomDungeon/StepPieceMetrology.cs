using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    /// <summary>
    /// Stair-forge step 2 metrology pass: measures the package's atomic pieces
    /// (stair flights, walls, railings, bottom caps, COMP composites) from renderer
    /// and mesh geometry into Assets/Arena/Content/Settings/Dungeons/RandomDungeon/step_piece_library.json.
    /// Names are never trusted for dimensions (the flight suffix is inverse to its
    /// rise); everything is derived from measured geometry, and entries whose
    /// measurements fail snap/shape checks are flagged confidence "review" for a
    /// one-time human confirmation instead of being silently guessed.
    /// </summary>
    internal static class StepPieceMetrology
    {
        private const string PackageInventoryPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/package_inventory.json";
        private const string PackageAssetRoot = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/";
        private const string OutputPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/step_piece_library.json";
        private const float CellSize = 4f;
        // Pack pieces are authored on a 0.25u modeling grid; anything further than
        // this from a grid multiple is authoring dust and flags the piece for review.
        private const float SnapTolerance = 0.08f;
        private const float UpFacingNormalMinY = 0.7f;

        [MenuItem("Tools/Dungeon Lab/Measure Step Piece Library")]
        public static void MeasureStepPieceLibrary()
        {
            Dictionary<string, string> inventory = LoadInventoryPaths();
            Dictionary<string, bool> previousConfirmations = LoadPreviousConfirmations();

            var pieces = new List<JObject>();
            int reviewCount = 0;
            var reviewNames = new List<string>();
            foreach (KeyValuePair<string, string> item in inventory.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string category = ClassifyPiece(item.Key);
                if (category == null)
                {
                    continue;
                }

                JObject record;
                try
                {
                    record = MeasurePiece(item.Key, item.Value, category);
                }
                catch (Exception exception)
                {
                    record = new JObject
                    {
                        ["name"] = item.Key,
                        ["path"] = item.Value,
                        ["category"] = category,
                        ["confidence"] = "review",
                        ["reviewReasons"] = new JArray($"measurement failed: {exception.Message}")
                    };
                }

                bool measurementChanged = true;
                if (previousConfirmations.TryGetValue(item.Key, out bool wasConfirmed))
                {
                    measurementChanged = false;
                }

                record["humanConfirmed"] = !measurementChanged && wasConfirmed;
                if (string.Equals(record.Value<string>("confidence"), "review", StringComparison.Ordinal))
                {
                    reviewCount++;
                    reviewNames.Add(item.Key);
                }

                pieces.Add(record);
            }

            var root = new JObject
            {
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["unitsPerCell"] = CellSize,
                ["pieceCount"] = pieces.Count,
                ["pieces"] = new JArray(pieces)
            };

            File.WriteAllText(OutputPath, root.ToString(Unity.Plastic.Newtonsoft.Json.Formatting.Indented));
            AssetDatabase.ImportAsset(OutputPath);
            Debug.Log(
                $"Dungeon Lab Metrology: measured {pieces.Count} pieces into {OutputPath}; " +
                $"{pieces.Count - reviewCount} high confidence, {reviewCount} flagged for review" +
                (reviewCount > 0 ? $": {string.Join(", ", reviewNames)}" : "."));
        }

        // Selection is by name family only for picking WHICH pieces to measure;
        // every recorded dimension comes from geometry. Props, doors (deferred by
        // design decision 7) and FX composites are not forge material and are
        // excluded outright rather than measured-and-flagged.
        private static string ClassifyPiece(string name)
        {
            if (name.StartsWith("P_MOD_Stairs_01_E_straight_", StringComparison.Ordinal))
            {
                return "stairFlight";
            }

            if (name.StartsWith("P_MOD_Stairs_01_Railing", StringComparison.Ordinal))
            {
                return "stairRailing";
            }

            // Stair-support families the forge dresses flights with (step 6): thin
            // side shells (WallCover), solid side walls (Wall_L/R), sloped underside
            // caps (BotCap) and side trims.
            if (name.StartsWith("P_MOD_Stairs_01_WallCover_", StringComparison.Ordinal))
            {
                return "stairSideCover";
            }

            // The quarter-turn curve kit (E_med_NW/SW + matching wall and botcap;
            // the hand-authored curved stairs co-locate exactly these).
            if (name.StartsWith("P_MOD_Stairs_01_E_med_", StringComparison.Ordinal))
            {
                return name.EndsWith("_BotCap", StringComparison.Ordinal) ? "stairCurvedBotCap" : "stairCurvedFlight";
            }

            // Round TIER-edge risers (step 9): concave/convex quarter-rounds and
            // 45-degree angle steps that wrap dais rims and rounded tier corners
            // (the gold scene's throne dais rim and the user's _claude_step_example
            // compositions). Not forge flights — the round-tier dressing stream
            // owns them; side-plane areas are the orientation signal.
            if (name.StartsWith("P_MOD_Stairs_01_E_concave_", StringComparison.Ordinal) ||
                name.StartsWith("P_MOD_Stairs_01_E_convex_", StringComparison.Ordinal) ||
                name.StartsWith("P_MOD_Stairs_01_E_angle_", StringComparison.Ordinal))
            {
                return "tierStepEdge";
            }

            if (name.StartsWith("P_MOD_Stairs_01_med_", StringComparison.Ordinal))
            {
                return "stairCurvedWall";
            }

            if (name.StartsWith("P_MOD_Stairs_01_Wall_", StringComparison.Ordinal))
            {
                return "stairSideWall";
            }

            if (name.StartsWith("P_MOD_Stairs_01_BotCap_", StringComparison.Ordinal))
            {
                return "stairBotCap";
            }

            if (name.StartsWith("P_MOD_Stairs_01_WallTrim_", StringComparison.Ordinal))
            {
                return "stairWallTrim";
            }

            // One-sided straight floors: landing decks and half-cell flat spans for
            // forged staircases (and the measured source for landing-trim floors).
            if (name.StartsWith("P_MOD_Floor_01_O_straight_", StringComparison.Ordinal))
            {
                return "floor";
            }

            // Round floor caps that pair with the tier-edge risers (concave/convex/
            // angle in O and E faces, med/tiny/half sizes): the gold dais co-locates
            // each round riser with its matching floor cap.
            if ((name.StartsWith("P_MOD_Floor_01_O_", StringComparison.Ordinal) ||
                 name.StartsWith("P_MOD_Floor_01_E_", StringComparison.Ordinal)) &&
                (name.Contains("_concave") || name.Contains("_convex") || name.Contains("_angle")))
            {
                return "floorRound";
            }

            if (name.StartsWith("P_MOD_Railing_01_", StringComparison.Ordinal))
            {
                return name.Contains("column") ? "railingColumn" : "railing";
            }

            // Wall-top trim curbs (Trim/WallTrim family, one-sided variants): the
            // cover piece that runs under ledge railings (user rule 2026-06-11).
            // The straight piece is the placement target; corner/angle variants
            // ride along for future corner dressing.
            if (name.StartsWith("P_MOD_WallTrim_01_O_", StringComparison.Ordinal))
            {
                return "wallTrim";
            }

            if (name.StartsWith("P_MOD_Wall_01_", StringComparison.Ordinal) ||
                name.StartsWith("COMP_Wall_01_", StringComparison.Ordinal))
            {
                return "wall";
            }

            if (name.StartsWith("P_MOD_Base_01_", StringComparison.Ordinal))
            {
                return "bottomCap";
            }

            if (name.StartsWith("COMP_Column_01_", StringComparison.Ordinal))
            {
                return "column";
            }

            if (name.StartsWith("COMP_PROP_", StringComparison.Ordinal) ||
                name.StartsWith("COMP_Door_", StringComparison.Ordinal))
            {
                return null;
            }

            if (name.StartsWith("COMP_", StringComparison.Ordinal))
            {
                return "composite";
            }

            return null;
        }

        private static JObject MeasurePiece(string name, string prefabPath, string category)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException($"missing prefab at '{prefabPath}'");
            }

            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.hideFlags = HideFlags.HideAndDontSave;
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;

                if (!TryGetRendererBounds(instance, out Bounds bounds, out int rendererCount))
                {
                    throw new InvalidOperationException("prefab has no renderer bounds");
                }

                var reviewReasons = new List<string>();
                var record = new JObject
                {
                    ["name"] = name,
                    ["path"] = prefabPath,
                    ["category"] = category,
                    ["rendererCount"] = rendererCount,
                    ["boundsMin"] = VectorToken(bounds.min),
                    ["boundsMax"] = VectorToken(bounds.max),
                    ["sizeUnits"] = VectorToken(bounds.size)
                };

                switch (category)
                {
                    case "stairFlight":
                        MeasureStairFlight(instance, bounds, record, reviewReasons);
                        break;
                    case "stairCurvedFlight":
                        MeasureCurvedStairFlight(instance, bounds, record, reviewReasons);
                        break;
                    case "bottomCap":
                    case "tierStepEdge":
                    case "floorRound":
                        MeasureFacadePiece(bounds, record);
                        MeasureSidePlaneAreas(instance, bounds, record);
                        break;
                    case "wall":
                    case "railing":
                    case "stairRailing":
                    case "railingColumn":
                    case "column":
                    case "stairSideCover":
                    case "stairSideWall":
                    case "stairBotCap":
                    case "stairWallTrim":
                    case "wallTrim":
                    case "stairCurvedWall":
                    case "stairCurvedBotCap":
                    case "floor":
                        MeasureFacadePiece(bounds, record);
                        break;
                    case "composite":
                        MeasureComposite(bounds, record);
                        break;
                }

                record["confidence"] = reviewReasons.Count == 0 ? "high" : "review";
                record["reviewReasons"] = new JArray(reviewReasons);
                return record;
            }
            finally
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
        }

        private static void MeasureStairFlight(GameObject instance, Bounds bounds, JObject record, List<string> reviewReasons)
        {
            List<Vector3> walkPoints = CollectUpFacingTriangleCentroids(instance);
            if (walkPoints.Count < 4)
            {
                reviewReasons.Add($"only {walkPoints.Count} up-facing walk-surface samples; climb analysis impossible");
                return;
            }

            // The climb axis is the horizontal axis whose position correlates with
            // walk-surface height; tread geometry makes this near-monotonic.
            float corrX = Correlation(walkPoints, p => p.x);
            float corrZ = Correlation(walkPoints, p => p.z);
            bool climbAlongX = Mathf.Abs(corrX) >= Mathf.Abs(corrZ);
            float primary = climbAlongX ? corrX : corrZ;
            float secondary = climbAlongX ? corrZ : corrX;
            string climbAxis = (climbAlongX ? "x" : "z") + (primary >= 0f ? "+" : "-");
            if (Mathf.Abs(primary) < 0.75f || Mathf.Abs(secondary) > 0.4f)
            {
                reviewReasons.Add(
                    $"ambiguous climb axis (corrX {corrX:0.##}, corrZ {corrZ:0.##})");
            }

            float walkTopY = walkPoints.Max(p => p.y);
            float walkBottomY = walkPoints.Min(p => p.y);
            // Rise is the climb from the floor the foot rests on (the bottom of the
            // piece) to the top walk surface. The pack's flights are pivot-anchored
            // at the TOP (walk top ~y0, foot at -rise), matching how the engine
            // mounts stairs by their upper edge — so rise must NOT be read as the
            // walk-top height above the pivot.
            float footY = bounds.min.y;
            float riseUnits = walkTopY - footY;
            float runUnits = climbAlongX ? bounds.size.x : bounds.size.z;
            float lateralWidthUnits = climbAlongX ? bounds.size.z : bounds.size.x;

            record["climbAxis"] = climbAxis;
            record["riseUnits"] = Round(riseUnits);
            record["runUnits"] = Round(runUnits);
            record["lateralWidthUnits"] = Round(lateralWidthUnits);
            record["walkSurfaceTopY"] = Round(walkTopY);
            record["walkSurfaceMinY"] = Round(walkBottomY);
            record["footY"] = Round(footY);
            // Which end sits at the prefab pivot: the forge needs this to compute
            // mount offsets ("top" = upper landing at y~0, the pack's convention).
            record["pivotAnchor"] = Mathf.Abs(walkTopY) <= Mathf.Abs(footY) ? "top" : "bottom";

            if (Mathf.Abs(riseUnits - Mathf.Round(riseUnits)) > SnapTolerance || riseUnits < 0.5f)
            {
                reviewReasons.Add($"rise {riseUnits:0.###}u is not a clean integer level rise");
            }
            else
            {
                // The integer 1u-level rise the forge consumes; riseUnits keeps the
                // raw measurement including authoring dust.
                record["riseLevels"] = Mathf.RoundToInt(riseUnits);
            }

            record["runGridSnapped"] = IsSnapped(runUnits, 0.5f);
            record["lateralWidthGridSnapped"] = IsSnapped(lateralWidthUnits, 0.5f);

            // Mount sockets derived from bounds + climb direction: entry at the low
            // edge on the pivot plane, exit at the high edge on the walk top.
            float sign = primary >= 0f ? 1f : -1f;
            Vector3 lowEdge;
            Vector3 highEdge;
            if (climbAlongX)
            {
                float lowX = sign > 0f ? bounds.min.x : bounds.max.x;
                float highX = sign > 0f ? bounds.max.x : bounds.min.x;
                float midZ = bounds.center.z;
                lowEdge = new Vector3(lowX, footY, midZ);
                highEdge = new Vector3(highX, walkTopY, midZ);
            }
            else
            {
                float lowZ = sign > 0f ? bounds.min.z : bounds.max.z;
                float highZ = sign > 0f ? bounds.max.z : bounds.min.z;
                float midX = bounds.center.x;
                lowEdge = new Vector3(midX, footY, lowZ);
                highEdge = new Vector3(midX, walkTopY, highZ);
            }

            record["sockets"] = new JArray(
                SocketToken("entry", lowEdge, InvertAxis(climbAxis)),
                SocketToken("exit", highEdge, climbAxis));
            record["pivotToWalkTop"] = VectorToken(new Vector3(highEdge.x, walkTopY, highEdge.z));
        }

        // Quarter-turn flights climb along an arc, so the straight measurer's
        // height-vs-axis correlation cannot find a climb axis. Instead the two
        // open boundary faces are found by where the walk surface meets the
        // piece's side planes: the face whose nearby walk samples sit lowest is
        // the entry, the highest is the exit; socket directions are the faces'
        // outward normals and the turn sign falls out of the in/out pair.
        private static void MeasureCurvedStairFlight(GameObject instance, Bounds bounds, JObject record, List<string> reviewReasons)
        {
            List<Vector3> walkPoints = CollectUpFacingTriangleCentroids(instance);
            if (walkPoints.Count < 8)
            {
                reviewReasons.Add($"only {walkPoints.Count} up-facing walk-surface samples; arc analysis impossible");
                return;
            }

            float walkTopY = walkPoints.Max(p => p.y);
            float footY = bounds.min.y;
            float riseUnits = walkTopY - footY;
            record["riseUnits"] = Round(riseUnits);
            record["walkSurfaceTopY"] = Round(walkTopY);
            record["footY"] = Round(footY);
            record["pivotAnchor"] = Mathf.Abs(walkTopY) <= Mathf.Abs(footY) ? "top" : "bottom";
            if (Mathf.Abs(riseUnits - Mathf.Round(riseUnits)) > SnapTolerance || riseUnits < 0.5f)
            {
                reviewReasons.Add($"rise {riseUnits:0.###}u is not a clean integer level rise");
            }
            else
            {
                record["riseLevels"] = Mathf.RoundToInt(riseUnits);
            }

            // Entry/exit face detection. The naive "average sample height near the
            // plane" ranking fails on arcs: the walk surface hugs the two CLOSED
            // outer faces for its whole climb, so those faces collect samples at
            // every height and can out-rank the true walk edges. The real walk
            // edges are wide bands at one height: the entry face carries a band at
            // FOOT height, the exit face a band at WALK-TOP height, and the two
            // faces of a quarter turn are perpendicular — so perpendicular ordered
            // pairs are scored by their band populations and the best pair wins.
            const float faceBand = 0.6f;
            const float heightBand = 0.7f;
            const float minBandExtent = 1.5f;
            (string axis, Vector2 outward, Func<Vector3, float> planeDistance, Func<Vector3, float> lateral)[] faces =
            {
                ("x-", Vector2.left, p => Mathf.Abs(p.x - bounds.min.x), p => p.z),
                ("x+", Vector2.right, p => Mathf.Abs(p.x - bounds.max.x), p => p.z),
                ("z-", Vector2.down, p => Mathf.Abs(p.z - bounds.min.z), p => p.x),
                ("z+", Vector2.up, p => Mathf.Abs(p.z - bounds.max.z), p => p.x),
            };

            var faceStats = new List<(string axis, Vector2 outward, List<Vector3> lowBand, List<Vector3> highBand, Func<Vector3, float> lateral)>();
            foreach (var face in faces)
            {
                List<Vector3> near = walkPoints.Where(p => face.planeDistance(p) <= faceBand).ToList();
                faceStats.Add((
                    face.axis,
                    face.outward,
                    near.Where(p => p.y <= footY + heightBand).ToList(),
                    near.Where(p => p.y >= walkTopY - heightBand).ToList(),
                    face.lateral));
            }

            float BandExtent(List<Vector3> band, Func<Vector3, float> lateral)
            {
                return band.Count < 2 ? 0f : band.Max(lateral) - band.Min(lateral);
            }

            int bestScore = -1;
            (string axis, Vector2 outward, List<Vector3> band, Func<Vector3, float> lateral) entry = default;
            (string axis, Vector2 outward, List<Vector3> band, Func<Vector3, float> lateral) exit = default;
            foreach (var entryFace in faceStats)
            {
                foreach (var exitFace in faceStats)
                {
                    if (entryFace.axis == exitFace.axis ||
                        Mathf.Abs(Vector2.Dot(entryFace.outward, exitFace.outward)) > 0.5f)
                    {
                        continue;
                    }

                    if (entryFace.lowBand.Count < 2 || exitFace.highBand.Count < 2 ||
                        BandExtent(entryFace.lowBand, entryFace.lateral) < minBandExtent ||
                        BandExtent(exitFace.highBand, exitFace.lateral) < minBandExtent)
                    {
                        continue;
                    }

                    int score = entryFace.lowBand.Count + exitFace.highBand.Count;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        entry = (entryFace.axis, entryFace.outward, entryFace.lowBand, entryFace.lateral);
                        exit = (exitFace.axis, exitFace.outward, exitFace.highBand, exitFace.lateral);
                    }
                }
            }

            if (bestScore < 0)
            {
                string diagnostics = string.Join("; ", faceStats.Select(f =>
                    $"{f.axis}: low {f.lowBand.Count}/{BandExtent(f.lowBand, f.lateral):0.#}u high {f.highBand.Count}/{BandExtent(f.highBand, f.lateral):0.#}u"));
                reviewReasons.Add($"no perpendicular entry/exit face pair with wide walk bands; per-face {diagnostics}");
                return;
            }

            // Turn sign in the forge's convention: walking IN (opposite the entry
            // outward normal) then OUT (the exit outward normal); negative cross
            // means a right turn (+1), positive a left turn (-1).
            Vector2 walkIn = -entry.outward;
            Vector2 walkOut = exit.outward;
            float cross = walkIn.x * walkOut.y - walkIn.y * walkOut.x;
            record["turnSign"] = cross < 0f ? 1 : -1;

            // The bands identify WHICH faces are open; the socket position is the
            // face's geometric center. A band's sample mean must not position the
            // socket: arc treads are wedges, denser toward the outer rim, so the
            // mean sits ~0.5u off-center and shifts the placed piece into its
            // neighbour (the first forged curves overlapped their lower flight by
            // exactly that bias).
            Vector3 SocketOnFace(string axis)
            {
                return axis[0] == 'x'
                    ? new Vector3(axis[1] == '-' ? bounds.min.x : bounds.max.x, 0f, (bounds.min.z + bounds.max.z) * 0.5f)
                    : new Vector3((bounds.min.x + bounds.max.x) * 0.5f, 0f, axis[1] == '-' ? bounds.min.z : bounds.max.z);
            }

            Vector3 entrySocket = SocketOnFace(entry.axis);
            Vector3 exitSocket = SocketOnFace(exit.axis);
            record["sockets"] = new JArray(
                SocketToken("entry", new Vector3(entrySocket.x, footY, entrySocket.z), entry.axis),
                SocketToken("exit", new Vector3(exitSocket.x, walkTopY, exitSocket.z), exit.axis));
            record["planSizeGridSnapped"] = IsSnapped(bounds.size.x, CellSize) && IsSnapped(bounds.size.z, CellSize);
        }

        private static void MeasureFacadePiece(Bounds bounds, JObject record)
        {
            // Walls/caps/railings are facade strips: width along the longer
            // horizontal axis, thickness along the shorter one. Off-grid heights
            // are normal (decorative tops, railing profiles), so they are recorded
            // faithfully with a gridSnapped marker instead of a review flag —
            // "review" is reserved for measurements that may be WRONG, not for
            // pieces that are not grid-pretty. The forge filters on gridSnapped.
            bool widthAlongX = bounds.size.x >= bounds.size.z;
            float widthUnits = widthAlongX ? bounds.size.x : bounds.size.z;
            float thicknessUnits = widthAlongX ? bounds.size.z : bounds.size.x;
            record["widthAxis"] = widthAlongX ? "x" : "z";
            record["heightUnits"] = Round(bounds.size.y);
            record["widthUnits"] = Round(widthUnits);
            record["thicknessUnits"] = Round(thicknessUnits);
            record["heightGridSnapped"] = IsSnapped(bounds.size.y, 0.5f);
            record["widthGridSnapped"] = IsSnapped(widthUnits, 0.5f);

            Vector3 topCenter = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
            Vector3 baseCenter = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            record["sockets"] = new JArray(
                SocketToken("base", baseCenter, "y-"),
                SocketToken("top", topCenter, "y+"));
        }

        // Rotational orientation that square bounds cannot reveal, measured from
        // geometry these pieces actually HAVE. The pack's base blocks are
        // face-culled like its one-sided floors — they have NO top faces (an
        // up-facing centroid pass found zero area) — but their flat SIDE faces
        // exist and lie exactly on the bounds planes. A quarter-round has flat
        // faces on exactly the two adjacent planes that meet at its inner
        // corner (the curved face pulls away from the other two); a straight
        // block is solid on all four. Per-plane on-plane triangle area is the
        // discriminator, robust on ~15-vertex meshes.
        private static void MeasureSidePlaneAreas(GameObject instance, Bounds bounds, JObject record)
        {
            const float planeBand = 0.08f;
            float areaXMinus = 0f, areaXPlus = 0f, areaZMinus = 0f, areaZPlus = 0f;
            foreach (MeshFilter filter in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                Transform meshTransform = filter.transform;
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    Vector3 a = meshTransform.TransformPoint(vertices[triangles[i]]);
                    Vector3 b = meshTransform.TransformPoint(vertices[triangles[i + 1]]);
                    Vector3 c = meshTransform.TransformPoint(vertices[triangles[i + 2]]);
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    if (cross.sqrMagnitude <= 1e-10f || Mathf.Abs(cross.normalized.y) >= UpFacingNormalMinY)
                    {
                        continue;
                    }

                    float area = cross.magnitude * 0.5f;
                    bool OnPlane(Func<Vector3, float> coordinate, float plane)
                    {
                        return Mathf.Abs(coordinate(a) - plane) <= planeBand &&
                            Mathf.Abs(coordinate(b) - plane) <= planeBand &&
                            Mathf.Abs(coordinate(c) - plane) <= planeBand;
                    }

                    if (OnPlane(p => p.x, bounds.min.x))
                    {
                        areaXMinus += area;
                    }
                    else if (OnPlane(p => p.x, bounds.max.x))
                    {
                        areaXPlus += area;
                    }
                    else if (OnPlane(p => p.z, bounds.min.z))
                    {
                        areaZMinus += area;
                    }
                    else if (OnPlane(p => p.z, bounds.max.z))
                    {
                        areaZPlus += area;
                    }
                }
            }

            record["sidePlaneAreas"] = new JObject
            {
                ["xMinus"] = Round(areaXMinus),
                ["xPlus"] = Round(areaXPlus),
                ["zMinus"] = Round(areaZMinus),
                ["zPlus"] = Round(areaZPlus),
            };
        }

        private static void MeasureComposite(Bounds bounds, JObject record)
        {
            record["heightUnits"] = Round(bounds.size.y);
            record["footprintCellsX"] = Round(bounds.size.x / CellSize);
            record["footprintCellsZ"] = Round(bounds.size.z / CellSize);
            // Plan-cell coverage marker for forge fitting: only half-cell-snapped
            // composites can be tiled directly; the rest stay available as dressing.
            record["footprintGridSnapped"] =
                IsSnapped(bounds.size.x, CellSize * 0.5f) && IsSnapped(bounds.size.z, CellSize * 0.5f);
        }

        private static bool IsSnapped(float value, float quantum)
        {
            return Mathf.Abs(value - Mathf.Round(value / quantum) * quantum) <= SnapTolerance;
        }

        private static List<Vector3> CollectUpFacingTriangleCentroids(GameObject instance)
        {
            var centroids = new List<Vector3>();
            foreach (MeshFilter filter in instance.GetComponentsInChildren<MeshFilter>(true))
            {
                // No isReadable guard: editor scripts may read mesh data regardless
                // of the import setting (the restriction only applies in players),
                // and the pack's meshes are imported non-readable.
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                Transform meshTransform = filter.transform;
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    Vector3 a = meshTransform.TransformPoint(vertices[triangles[i]]);
                    Vector3 b = meshTransform.TransformPoint(vertices[triangles[i + 1]]);
                    Vector3 c = meshTransform.TransformPoint(vertices[triangles[i + 2]]);
                    Vector3 normal = Vector3.Cross(b - a, c - a);
                    if (normal.sqrMagnitude <= 1e-10f || normal.normalized.y < UpFacingNormalMinY)
                    {
                        continue;
                    }

                    centroids.Add((a + b + c) / 3f);
                }
            }

            return centroids;
        }

        private static float Correlation(List<Vector3> points, Func<Vector3, float> horizontal)
        {
            float meanH = points.Average(horizontal);
            float meanY = points.Average(p => p.y);
            float covariance = 0f;
            float varianceH = 0f;
            float varianceY = 0f;
            foreach (Vector3 point in points)
            {
                float dh = horizontal(point) - meanH;
                float dy = point.y - meanY;
                covariance += dh * dy;
                varianceH += dh * dh;
                varianceY += dy * dy;
            }

            float denominator = Mathf.Sqrt(varianceH * varianceY);
            return denominator <= 1e-8f ? 0f : covariance / denominator;
        }

        private static bool TryGetRendererBounds(GameObject instance, out Bounds bounds, out int rendererCount)
        {
            bounds = default;
            rendererCount = 0;
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (rendererCount == 0)
                {
                    bounds = renderer.bounds;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }

                rendererCount++;
            }

            return rendererCount > 0;
        }

        private static Dictionary<string, string> LoadInventoryPaths()
        {
            if (!File.Exists(PackageInventoryPath))
            {
                throw new InvalidOperationException($"Missing package inventory at '{PackageInventoryPath}'.");
            }

            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JToken item in JArray.Parse(File.ReadAllText(PackageInventoryPath)))
            {
                string name = item.Value<string>("name");
                string path = item.Value<string>("path");
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
                {
                    paths[name] = PackageAssetRoot + path;
                }
            }

            return paths;
        }

        // Re-running the tool must not throw away the one-time human confirmations;
        // a confirmed piece stays confirmed as long as it is still present.
        private static Dictionary<string, bool> LoadPreviousConfirmations()
        {
            var confirmations = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (!File.Exists(OutputPath))
            {
                return confirmations;
            }

            try
            {
                JObject previous = JObject.Parse(File.ReadAllText(OutputPath));
                if (previous["pieces"] is JArray pieces)
                {
                    foreach (JToken piece in pieces)
                    {
                        string name = piece.Value<string>("name");
                        if (!string.IsNullOrEmpty(name))
                        {
                            confirmations[name] = piece.Value<bool?>("humanConfirmed") == true;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Dungeon Lab Metrology: could not read previous library for confirmation carry-over; {exception.Message}");
            }

            return confirmations;
        }

        private static string InvertAxis(string axis)
        {
            return axis.EndsWith("+", StringComparison.Ordinal)
                ? axis.Substring(0, 1) + "-"
                : axis.Substring(0, 1) + "+";
        }

        private static JObject SocketToken(string role, Vector3 local, string direction)
        {
            return new JObject
            {
                ["role"] = role,
                ["local"] = VectorToken(local),
                ["direction"] = direction
            };
        }

        private static JObject VectorToken(Vector3 value)
        {
            return new JObject
            {
                ["x"] = Round(value.x),
                ["y"] = Round(value.y),
                ["z"] = Round(value.z)
            };
        }

        private static float Round(float value)
        {
            return Mathf.Round(value * 1000f) / 1000f;
        }
    }
}
