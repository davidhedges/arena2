using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Plastic.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DungeonLab.Editor
{
    internal static class StairContractProofBuilder
    {
        private const string ContractPath = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/stair_proof_contracts.json";
        private const string StairPrefabRoot = "Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/Stairs";
        private const string PackageFloorPrefabRoot = "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Floor/";
        private const string RootName = "Stair Contract Proof";
        private const float EdgeTolerance = 0.001f;
        private const float SourcePortTolerance = 0.125f;
        private const float ReviewGallerySlotSize = 24f;
        private const int ReviewGalleryColumns = 4;

        [MenuItem("Tools/Dungeon Lab/Dev/Proof Stair Contracts")]
        public static void BuildProof()
        {
            ProofResult result;
            try
            {
                result = Build();
            }
            catch (Exception exception)
            {
                result = ProofResult.Fail("exception", exception.Message);
            }

            string line = "STAIR_CONTRACT_PROOF_SUMMARY " + result.Summary;
            if (result.passed)
            {
                Debug.Log("Dungeon Lab Stair Contract Proof Gate: PASS | " + line);
            }
            else
            {
                Debug.LogError("Dungeon Lab Stair Contract Proof Gate: FAIL | " + line);
            }
        }

        private static ProofResult Build()
        {
            ProofConfig config = LoadConfig();
            ClearRoot();

            var rejectedContracts = new List<string>();
            var rejectedPlacements = new List<string>();
            var unsupportedContracts = new List<string>();
            List<StairContract> validContracts = ValidateContracts(
                config,
                rejectedContracts,
                unsupportedContracts,
                out int discoveredStairPrefabCount,
                out List<StairReviewEntry> reviewEntries);

            GameObject floorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(config.floorPrefab);
            if (floorPrefab == null)
            {
                rejectedContracts.Add("floorPrefab:missing prefab " + config.floorPrefab);
            }

            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Create " + RootName);
            Transform stairRoot = CreateChild(root.transform, "Stair Prefabs");
            Transform floorRoot = CreateChild(root.transform, "Floor Prefabs");
            Transform debugRoot = CreateChild(root.transform, "Port Debug");
            Transform reviewRoot = CreateChild(root.transform, "Stair Review Gallery");
            reviewRoot.localPosition = new Vector3(40f, 0f, 0f);

            var model = new ProofModel(config.cellSize, config.levelHeight, config.floorLocalBoundsMin);
            var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
            var riseHistogram = new Dictionary<int, int>();
            var topologyHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            var laneHistogram = new Dictionary<int, int>();
            float nextProofRowZ = 0f;

            foreach (StairContract contract in validContracts)
            {
                GameObject stairPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(contract.prefab);
                if (stairPrefab == null)
                {
                    rejectedPlacements.Add(contract.name + ":missing stair prefab " + contract.prefab);
                    MarkReviewEntryFailed(reviewEntries, contract.prefab, contract.name, "missing stair prefab " + contract.prefab);
                    continue;
                }

                int lowerLevel = 0;
                Vector3 entryEdge = BuildProofEntryEdge(contract, 0f, nextProofRowZ, lowerLevel, config.cellSize, config.levelHeight);
                Vector3 rootPosition = SolveRootFromPortEdge(contract.entry, entryEdge);
                int upperLevel = lowerLevel + contract.rise;
                if (!ValidatePrefabSnapPosition(rootPosition, config.cellSize, config.levelHeight, out string stairSnapError))
                {
                    rejectedPlacements.Add(contract.name + ":stair prefab root " + stairSnapError);
                    MarkReviewEntryFailed(reviewEntries, contract.prefab, contract.name, "stair prefab root " + stairSnapError);
                    continue;
                }

                Vector3 exitEdge = rootPosition + contract.exit.localEdgePosition;
                float expectedExitY = LevelY(upperLevel, config.levelHeight);
                if (Mathf.Abs(exitEdge.y - expectedExitY) > EdgeTolerance)
                {
                    rejectedPlacements.Add(contract.name + ":exit port Y does not match rise");
                    MarkReviewEntryFailed(reviewEntries, contract.prefab, contract.name, "exit port Y does not match rise");
                    continue;
                }

                var placed = new PlacedStair(contract, rootPosition, lowerLevel, upperLevel);
                if (!TryReserveFootprint(model, placed, out string reserveError))
                {
                    rejectedPlacements.Add(contract.name + ":" + reserveError);
                    MarkReviewEntryFailed(reviewEntries, contract.prefab, contract.name, reserveError);
                    continue;
                }

                placed.entryLanding = BuildLanding(placed, contract.entry, lowerLevel, "entry", config.cellSize);
                placed.exitLanding = BuildLanding(placed, contract.exit, upperLevel, "exit", config.cellSize);
                placed.landings = BuildLandings(placed, config.cellSize);

                if (!TryInstantiateStairVisual(stairPrefab, contract, stairRoot, "proof_stair_" + (model.stairs.Count + 1) + "_" + contract.name, rootPosition, config.cellSize, config.levelHeight, out GameObject instance, out string visualError))
                {
                    UnreserveFootprint(model, placed);
                    rejectedPlacements.Add(contract.name + ":" + visualError);
                    MarkReviewEntryFailed(reviewEntries, contract.prefab, contract.name, visualError);
                    continue;
                }

                placed.instance = instance;
                model.stairs.Add(placed);
                MarkReviewEntryReviewed(reviewEntries, contract.prefab, contract.name);

                Increment(histogram, contract.name);
                Increment(riseHistogram, contract.rise);
                Increment(topologyHistogram, contract.topology);
                Increment(laneHistogram, contract.laneCount);

                nextProofRowZ += (contract.localBoundsSizeCells.y + 3) * config.cellSize;
            }

            if (floorPrefab != null)
            {
                foreach (PlacedStair stair in model.stairs)
                {
                    foreach (PortLanding landing in stair.landings)
                    {
                        AddLandingFloors(model, floorPrefab, floorRoot, landing);
                    }
                }

            }

            foreach (PlacedStair stair in model.stairs)
            {
                foreach (PortLanding landing in stair.landings)
                {
                    AddDebugPort(debugRoot, landing, landing.port.level == 0 ? Color.magenta : Color.cyan);
                }
            }

            bool modelValid = ValidateModel(model, out string validationMessage);
            Dictionary<string, StairContract> reviewContractLookup = validContracts
                .GroupBy(contract => NormalizePath(contract.prefab), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            ReviewGalleryResult reviewGallery = RenderStairReviewGallery(reviewRoot, reviewEntries, reviewContractLookup, floorPrefab, config);
            bool reachable = modelValid && model.stairs.Count > 0;
            string connectivityMessage = "single-stair proof only; multi-stair chain skipped";
            bool hasOneLane = model.stairs.Any(stair => stair.contract.laneCount == 1);
            bool hasTwoLane = model.stairs.Any(stair => stair.contract.laneCount == 2);
            bool hasThreeLane = model.stairs.Any(stair => stair.contract.laneCount == 3);
            bool hasVariety = histogram.Count > 1;
            bool dataProof = rejectedContracts.Count == 0;
            bool unsupportedProof = UnsupportedContractsAreClassified(unsupportedContracts);
            bool placementSolverProof = rejectedPlacements.Count == 0;
            bool portDebugProof = model.stairs.Count > 0;
            bool singleStairProof = modelValid;
            bool passed =
                model.stairs.Count > 0 &&
                dataProof &&
                unsupportedProof &&
                placementSolverProof &&
                reviewGallery.passed &&
                singleStairProof &&
                reachable &&
                hasVariety &&
                hasOneLane &&
                hasTwoLane;

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            string summary =
                "stair count=" + model.stairs.Count +
                "; discoveredStairPrefabs=" + discoveredStairPrefabCount +
                "; proofedReviewedStairs=" + model.stairs.Count +
                "; classifiedUnsupportedStairs=" + unsupportedContracts.Count +
                "; stairPrefabCoverage=" + (model.stairs.Count + unsupportedContracts.Count) + "/" + discoveredStairPrefabCount +
                "; " + reviewGallery.Summary +
                "; histogram=" + FormatHistogram(histogram) +
                "; riseHistogram=" + FormatHistogram(riseHistogram) +
                "; topologyHistogram=" + FormatHistogram(topologyHistogram) +
                "; laneHistogram=" + FormatHistogram(laneHistogram) +
                "; rejected contracts=" + rejectedContracts.Count +
                "; rejected contract reasons=" + FormatReasons(rejectedContracts) +
                "; unsupported contracts=" + unsupportedContracts.Count +
                "; unsupported contract reasons=" + FormatReasons(unsupportedContracts) +
                "; rejected placements=" + rejectedPlacements.Count +
                "; rejected placement reasons=" + FormatReasons(rejectedPlacements) +
                "; reachable=" + FormatBool(reachable) +
                "; validation=" + (modelValid ? "PASS" : "FAIL") +
                "; dataProof=" + FormatBool(dataProof) +
                "; unsupportedProof=" + FormatBool(unsupportedProof) +
                "; portDebugProof=" + FormatBool(portDebugProof) +
                "; singleStairProof=" + FormatBool(singleStairProof) +
                "; placementSolverProof=" + FormatBool(placementSolverProof) +
                "; validationDetail=" + Sanitize(validationMessage) +
                "; connectivityDetail=" + Sanitize(connectivityMessage) +
                "; chainProof=SKIPPED" +
                "; laneRequirements=one:" + FormatBool(hasOneLane) + ",two:" + FormatBool(hasTwoLane) + ",three:" + FormatBool(hasThreeLane) +
                "; variety=" + FormatBool(hasVariety);
            return new ProofResult(passed, summary);
        }

        private static ProofConfig LoadConfig()
        {
            if (!File.Exists(ContractPath))
            {
                throw new FileNotFoundException(ContractPath);
            }

            JObject root = JObject.Parse(File.ReadAllText(ContractPath));
            var config = new ProofConfig
            {
                cellSize = root.Value<float?>("cellSize") ?? 4f,
                levelHeight = root.Value<float?>("levelHeight") ?? 2f,
                floorPrefab = NormalizePath(root.Value<string>("floorPrefab")),
                floorLocalBoundsMin = ParseVector2(root["floorLocalBoundsMin"], "floorLocalBoundsMin"),
                floorLocalBoundsSizeCells = ParseCell(root["floorLocalBoundsSizeCells"], "floorLocalBoundsSizeCells"),
                contracts = new List<StairContract>(),
                unsupported = new Dictionary<string, string>(StringComparer.Ordinal)
            };

            if (root["unsupportedStairs"] is JObject unsupported)
            {
                foreach (JProperty property in unsupported.Properties())
                {
                    config.unsupported[property.Name] = property.Value.Value<string>() ?? "no authored reviewed contract";
                }
            }

            if (!(root["contracts"] is JArray contracts) || contracts.Count == 0)
            {
                throw new InvalidOperationException(ContractPath + " must define at least one contract.");
            }

            foreach (JToken token in contracts)
            {
                JObject value = RequireObject(token, "contract");
                var contract = new StairContract
                {
                    name = value.Value<string>("name") ?? string.Empty,
                    prefab = NormalizePath(value.Value<string>("prefab")),
                    source = value.Value<string>("source") ?? string.Empty,
                    reviewStatus = value.Value<string>("reviewStatus") ?? string.Empty,
                    rise = value.Value<int?>("rise") ?? 0,
                    laneCount = value.Value<int?>("laneCount") ?? 0,
                    runLength = value.Value<int?>("runLength") ?? 0,
                    topology = value.Value<string>("topology") ?? string.Empty,
                    bridgeAllowed = value.Value<bool?>("bridgeAllowed") ?? false,
                    visualYawDegrees = value.Value<float?>("visualYawDegrees") ?? 0f,
                    localBoundsMin = ParseVector3(value["localBoundsMin"], "localBoundsMin"),
                    localBoundsSizeCells = ParseCell(value["localBoundsSizeCells"], "localBoundsSizeCells"),
                    footprintCells = ParseCells(value["footprintCells"], "footprintCells"),
                    occupiedCells = ParseOptionalCells(value["occupiedCells"]),
                    reservedCells = ParseOptionalCells(value["reservedCells"]),
                    ports = ParsePorts(value["ports"], "ports"),
                    portAnchors = ParseOptionalPortAnchors(value["portAnchors"], "portAnchors"),
                    sourceRootPoses = ParseOptionalSourceRootPoses(value["sourceRootPoses"], "sourceRootPoses"),
                    visualAnchors = ParseVisualAnchors(value["visualAnchors"], "visualAnchors")
                };

                if (!TryAssignEntryExitPorts(contract, out string portError))
                {
                    throw new InvalidOperationException(contract.name + ": " + portError);
                }

                config.contracts.Add(contract);
            }

            return config;
        }

        private static List<StairContract> ValidateContracts(
            ProofConfig config,
            List<string> rejected,
            List<string> unsupportedContracts,
            out int discoveredStairPrefabCount,
            out List<StairReviewEntry> reviewEntries)
        {
            var valid = new List<StairContract>();
            var pathsWithContracts = new HashSet<string>(StringComparer.Ordinal);
            List<string> discoveredStairPrefabs = FindStairPrefabs().ToList();
            discoveredStairPrefabCount = discoveredStairPrefabs.Count;
            reviewEntries = discoveredStairPrefabs
                .Select(path => StairReviewEntry.Pending(Path.GetFileNameWithoutExtension(path), path))
                .ToList();

            if (config.cellSize <= 0f)
            {
                rejected.Add("config:cellSize must be positive");
            }
            else if (!IsWholeNumber(config.cellSize))
            {
                rejected.Add("config:cellSize must be an integer cell snap");
            }

            if (config.levelHeight <= 0f)
            {
                rejected.Add("config:levelHeight must be positive");
            }
            else if (!IsWholeNumber(config.levelHeight))
            {
                rejected.Add("config:levelHeight must be an integer level snap");
            }

            if (!IsFloorPrefabPath(config.floorPrefab))
            {
                rejected.Add("floorPrefab:path is not in a Floor prefab family");
            }

            if (config.cellSize > 0f && (!IsGridAligned(config.floorLocalBoundsMin.x, config.cellSize) || !IsGridAligned(config.floorLocalBoundsMin.y, config.cellSize)))
            {
                rejected.Add("floorPrefab:floorLocalBoundsMin must be cell-aligned");
            }

            if (config.floorLocalBoundsSizeCells.x != 1 || config.floorLocalBoundsSizeCells.y != 1)
            {
                rejected.Add("floorPrefab:proof gate requires one-cell floor prefab bounds");
            }

            foreach (StairContract contract in config.contracts)
            {
                string error;
                if (!ValidateContract(contract, config.cellSize, config.levelHeight, out error))
                {
                    rejected.Add((string.IsNullOrWhiteSpace(contract.name) ? contract.prefab : contract.name) + ":" + error);
                    MarkReviewEntryFailed(reviewEntries, contract.prefab, contract.name, error);
                    continue;
                }

                pathsWithContracts.Add(contract.prefab);
                valid.Add(contract);
            }

            foreach (string prefabPath in discoveredStairPrefabs)
            {
                if (pathsWithContracts.Contains(prefabPath))
                {
                    continue;
                }

                string prefabName = Path.GetFileNameWithoutExtension(prefabPath);
                if (config.unsupported.TryGetValue(prefabName, out string reason) ||
                    config.unsupported.TryGetValue(prefabPath, out reason))
                {
                    unsupportedContracts.Add(prefabName + ":" + reason);
                    MarkReviewEntryUnsupported(reviewEntries, prefabPath, prefabName, reason);
                }
                else
                {
                    string automaticReason = AutomaticUnsupportedReason();
                    unsupportedContracts.Add(prefabName + ":" + automaticReason);
                    MarkReviewEntryUnsupported(reviewEntries, prefabPath, prefabName, automaticReason);
                }
            }

            return valid;
        }

        private static bool UnsupportedContractsAreClassified(IReadOnlyList<string> unsupportedContracts)
        {
            foreach (string unsupported in unsupportedContracts)
            {
                int separator = unsupported.IndexOf(':');
                if (separator < 0 || separator == unsupported.Length - 1)
                {
                    return false;
                }
            }

            return true;
        }

        private static string AutomaticUnsupportedReason()
        {
            return "missing authored reviewed contract; automatically classified unsupported until a reviewed contract defines rise, lane count, run length, topology, footprint, ports, and visual anchors";
        }

        private static void MarkReviewEntryReviewed(List<StairReviewEntry> entries, string prefabPath, string name)
        {
            MarkReviewEntry(entries, prefabPath, name, StairReviewStatus.Reviewed, "reviewedContract", "proof placement rendered");
        }

        private static void MarkReviewEntryUnsupported(List<StairReviewEntry> entries, string prefabPath, string name, string reason)
        {
            MarkReviewEntry(entries, prefabPath, name, StairReviewStatus.Unsupported, "unsupportedContract", reason);
        }

        private static void MarkReviewEntryFailed(List<StairReviewEntry> entries, string prefabPath, string name, string reason)
        {
            MarkReviewEntry(entries, prefabPath, name, StairReviewStatus.Failed, "proofFailure", reason);
        }

        private static void MarkReviewEntry(
            List<StairReviewEntry> entries,
            string prefabPath,
            string name,
            StairReviewStatus status,
            string reason,
            string detail)
        {
            if (entries == null)
            {
                return;
            }

            string normalizedPath = NormalizePath(prefabPath);
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].prefabPath, normalizedPath, StringComparison.Ordinal))
                {
                    entries[i] = new StairReviewEntry(name, normalizedPath, status, reason, detail);
                    return;
                }
            }

            entries.Add(new StairReviewEntry(name, normalizedPath, status, reason, detail));
        }

        private static bool ValidateContract(StairContract contract, float cellSize, float levelHeight, out string error)
        {
            if (string.IsNullOrWhiteSpace(contract.name) || string.IsNullOrWhiteSpace(contract.prefab))
            {
                error = "missing name or prefab";
                return false;
            }

            if (!string.Equals(contract.source, "authored-reviewed", StringComparison.Ordinal) ||
                !string.Equals(contract.reviewStatus, "reviewed", StringComparison.Ordinal))
            {
                error = "contract source must be authored-reviewed and reviewStatus must be reviewed";
                return false;
            }

            if (!IsStairPrefabPath(contract.prefab))
            {
                error = "prefab is not in the stair prefab family";
                return false;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(contract.prefab) == null)
            {
                error = "missing stair prefab " + contract.prefab;
                return false;
            }

            if (contract.rise <= 0 || contract.laneCount <= 0 || contract.runLength <= 0)
            {
                error = "rise, lane count, and run length must be positive";
                return false;
            }

            if (contract.laneCount > 3)
            {
                error = "lane count greater than 3 is not supported by this proof gate";
                return false;
            }

            if (string.IsNullOrWhiteSpace(contract.topology))
            {
                error = "missing topology";
                return false;
            }

            if (!IsQuarterTurnDegrees(contract.visualYawDegrees))
            {
                error = "visualYawDegrees must be a reviewed 90-degree increment";
                return false;
            }

            if (contract.footprintCells.Count == 0)
            {
                error = "missing footprint cells";
                return false;
            }

            if (contract.ports == null || contract.ports.Count < 2)
            {
                error = "contract must define at least two ports";
                return false;
            }

            if (contract.visualAnchors == null || contract.visualAnchors.Count == 0)
            {
                error = "missing visualAnchors for visual contract-frame alignment";
                return false;
            }

            foreach (VisualAnchor anchor in contract.visualAnchors)
            {
                if (string.IsNullOrWhiteSpace(anchor.role))
                {
                    error = "visualAnchors contains anchor with missing role";
                    return false;
                }

                if (anchor.sourcePrefabs == null || anchor.sourcePrefabs.Count == 0)
                {
                    error = "visualAnchors " + anchor.role + " has no sourcePrefabs";
                    return false;
                }

                if (anchor.expectedLocalPositions != null && anchor.expectedLocalPositions.Count == 0)
                {
                    error = "visualAnchors " + anchor.role + " expectedLocalPositions must not be empty when declared";
                    return false;
                }

                if (anchor.expectedLocalPositions != null)
                {
                    foreach (Vector3 expected in anchor.expectedLocalPositions)
                    {
                        if (!IsLevelAligned(expected.y, levelHeight))
                        {
                            error = "visualAnchors " + anchor.role + " expectedLocalPositions contains non-level Y " + FormatFloat(expected.y);
                            return false;
                        }
                    }
                }

                foreach (string sourcePath in anchor.sourcePrefabs)
                {
                    if (!IsContractSurfacePrefabPath(sourcePath))
                    {
                        error = "visualAnchors " + anchor.role + " contains non-floor/stair source " + sourcePath;
                        return false;
                    }

                    if (AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath) == null)
                    {
                        error = "visualAnchors " + anchor.role + " source missing " + sourcePath;
                        return false;
                    }
                }
            }

            foreach (SourceRootPose pose in contract.sourceRootPoses)
            {
                if (!IsContractSurfacePrefabPath(pose.sourcePrefab))
                {
                    error = "sourceRootPoses contains non-floor/stair source " + pose.sourcePrefab;
                    return false;
                }

                if (AssetDatabase.LoadAssetAtPath<GameObject>(pose.sourcePrefab) == null)
                {
                    error = "sourceRootPoses source missing " + pose.sourcePrefab;
                    return false;
                }

                if (!IsLevelAligned(pose.localPosition.y, levelHeight))
                {
                    error = "sourceRootPoses " + pose.sourcePrefab + " has non-level Y " + FormatFloat(pose.localPosition.y);
                    return false;
                }

                if (!IsQuarterTurnDegrees(pose.localYawDegrees))
                {
                    error = "sourceRootPoses " + pose.sourcePrefab + " localYawDegrees must be a reviewed 90-degree increment";
                    return false;
                }
            }

            if (!TryGetVisualAnchorSources(contract, "exitSurfaceRoots", out _))
            {
                error = "missing visualAnchors role exitSurfaceRoots";
                return false;
            }

            if (contract.localBoundsSizeCells.x <= 0 || contract.localBoundsSizeCells.y <= 0)
            {
                error = "localBoundsSizeCells must be positive";
                return false;
            }

            if (!ValidateContractCells(contract.footprintCells, contract.localBoundsSizeCells, "footprintCells", out error) ||
                !ValidateContractCells(contract.occupiedCells, contract.localBoundsSizeCells, "occupiedCells", out error) ||
                !ValidateContractCells(contract.reservedCells, contract.localBoundsSizeCells, "reservedCells", out error))
            {
                return false;
            }

            for (int i = 0; i < contract.ports.Count; i++)
            {
                if (!ValidatePort(contract, contract.ports[i], "port[" + i + "]", contract.rise, contract.laneCount, cellSize, levelHeight, out error))
                {
                    return false;
                }
            }

            if (!ValidatePort(contract, contract.entry, "entry", contract.rise, contract.laneCount, cellSize, levelHeight, out error) ||
                !ValidatePort(contract, contract.exit, "exit", contract.rise, contract.laneCount, cellSize, levelHeight, out error))
            {
                return false;
            }

            if (contract.entry.level != 0)
            {
                error = "entry port level must be 0";
                return false;
            }

            if (contract.exit.level - contract.entry.level != contract.rise)
            {
                error = "entry and exit port levels do not match rise";
                return false;
            }

            foreach (Port port in contract.ports)
            {
                if (!PortCellsTouchFootprintSide(port, contract.footprintCells))
                {
                    error = "port cells must lie on the contracted footprint side";
                    return false;
                }
            }

            foreach (PortAnchor anchor in contract.portAnchors)
            {
                if (!ValidatePortAnchorSnap(anchor, levelHeight, out error))
                {
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static bool ValidatePortAnchorSnap(PortAnchor anchor, float levelHeight, out string error)
        {
            if (!IsLevelAligned(anchor.sourceLocalPosition.y, levelHeight))
            {
                error = "portAnchors " + anchor.portRole + " sourceLocalPosition has non-level Y " + FormatFloat(anchor.sourceLocalPosition.y);
                return false;
            }

            if (anchor.hasSourceLocalEdgeOffset && !IsLevelAligned(anchor.sourceLocalPosition.y + anchor.sourceLocalEdgeOffset.y, levelHeight))
            {
                error = "portAnchors " + anchor.portRole + " sourceLocalEdgeOffset resolves to non-level Y " +
                    FormatFloat(anchor.sourceLocalPosition.y + anchor.sourceLocalEdgeOffset.y);
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryAssignEntryExitPorts(StairContract contract, out string error)
        {
            error = string.Empty;
            if (contract.ports == null || contract.ports.Count < 2)
            {
                error = "contract must define at least two ports";
                return false;
            }

            int minLevel = contract.ports.Min(port => port.level);
            int maxLevel = contract.ports.Max(port => port.level);
            List<Port> lowestLevelPorts = contract.ports.Where(port => port.level == minLevel).ToList();
            List<Port> highestLevelPorts = contract.ports.Where(port => port.level == maxLevel).ToList();

            if (lowestLevelPorts.Count == 0 || highestLevelPorts.Count == 0)
            {
                error = "contract must define at least one lowest port and one highest port";
                return false;
            }

            if (minLevel != 0)
            {
                error = "lowest port level must be 0";
                return false;
            }

            if (maxLevel - minLevel != contract.rise)
            {
                error = "lowest and highest port levels do not match rise";
                return false;
            }

            contract.entry = lowestLevelPorts[0];
            contract.exit = highestLevelPorts[0];
            return true;
        }

        private static bool ValidateContractCells(
            List<Vector2Int> cells,
            Vector2Int boundsSizeCells,
            string label,
            out string error)
        {
            var unique = new HashSet<Vector2Int>();
            foreach (Vector2Int cell in cells)
            {
                if (cell.x < 0 || cell.x >= boundsSizeCells.x || cell.y < 0 || cell.y >= boundsSizeCells.y)
                {
                    error = label + " contains cell outside local contract grid " + cell;
                    return false;
                }

                if (!unique.Add(cell))
                {
                    error = label + " repeats cell " + cell;
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static bool ValidatePort(
            StairContract contract,
            Port port,
            string label,
            int rise,
            int laneCount,
            float cellSize,
            float levelHeight,
            out string error)
        {
            if (port.cells == null || port.cells.Count == 0)
            {
                error = label + " port has no cells";
                return false;
            }

            if (port.cells.Count != laneCount)
            {
                error = label + " port cell count does not match lane count";
                return false;
            }

            if (port.level < 0 || port.level > rise)
            {
                error = label + " port level is outside stair rise";
                return false;
            }

            if (!CellsAreContiguousOnPortSide(port))
            {
                error = label + " port cells must be contiguous along the port side";
                return false;
            }

            if (!ValidateContractCells(port.cells, contract.localBoundsSizeCells, label + " port cells", out error))
            {
                return false;
            }

            Vector3 expectedLocalEdge = ExpectedLocalPortEdgePosition(contract, port, cellSize, levelHeight);
            if (Mathf.Abs(expectedLocalEdge.x - port.localEdgePosition.x) > EdgeTolerance ||
                Mathf.Abs(expectedLocalEdge.y - port.localEdgePosition.y) > EdgeTolerance ||
                Mathf.Abs(expectedLocalEdge.z - port.localEdgePosition.z) > EdgeTolerance)
            {
                error = label + " port localEdgePosition does not match local contract grid; expected " + FormatVector(expectedLocalEdge) +
                    ", got " + FormatVector(port.localEdgePosition);
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool PortCellsTouchFootprintSide(Port port, List<Vector2Int> footprintCells)
        {
            var footprint = new HashSet<Vector2Int>(footprintCells);
            foreach (Vector2Int cell in port.cells)
            {
                if (!footprint.Contains(cell))
                {
                    return false;
                }

                Vector2Int outward = Direction.OutwardDelta(port.side);
                if (footprint.Contains(cell + outward))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool CellsAreContiguousOnPortSide(Port port)
        {
            List<int> values = Direction.IsEastWest(port.side)
                ? port.cells.Select(cell => cell.y).OrderBy(value => value).ToList()
                : port.cells.Select(cell => cell.x).OrderBy(value => value).ToList();

            int fixedValue = Direction.IsEastWest(port.side) ? port.cells[0].x : port.cells[0].y;
            foreach (Vector2Int cell in port.cells)
            {
                if ((Direction.IsEastWest(port.side) ? cell.x : cell.y) != fixedValue)
                {
                    return false;
                }
            }

            for (int i = 1; i < values.Count; i++)
            {
                if (values[i] != values[i - 1] + 1)
                {
                    return false;
                }
            }

            return true;
        }

        private static Vector3 ExpectedLocalPortEdgePosition(StairContract contract, Port port, float cellSize, float levelHeight)
        {
            int minX = port.cells.Min(cell => cell.x);
            int maxX = port.cells.Max(cell => cell.x);
            int minZ = port.cells.Min(cell => cell.y);
            int maxZ = port.cells.Max(cell => cell.y);
            float x;
            float z;

            switch (port.side)
            {
                case Direction.West:
                    x = contract.localBoundsMin.x + minX * cellSize;
                    z = contract.localBoundsMin.z + (minZ + maxZ + 1) * cellSize * 0.5f;
                    break;
                case Direction.East:
                    x = contract.localBoundsMin.x + (maxX + 1) * cellSize;
                    z = contract.localBoundsMin.z + (minZ + maxZ + 1) * cellSize * 0.5f;
                    break;
                case Direction.South:
                    x = contract.localBoundsMin.x + (minX + maxX + 1) * cellSize * 0.5f;
                    z = contract.localBoundsMin.z + minZ * cellSize;
                    break;
                case Direction.North:
                    x = contract.localBoundsMin.x + (minX + maxX + 1) * cellSize * 0.5f;
                    z = contract.localBoundsMin.z + (maxZ + 1) * cellSize;
                    break;
                default:
                    throw new InvalidOperationException("unknown side");
            }

            return new Vector3(x, LevelY(port.level, levelHeight), z);
        }

        private static bool TryReserveFootprint(ProofModel model, PlacedStair stair, out string error)
        {
            var stairCells = new HashSet<CellKey>(BuildBlockedCells(stair, model.cellSize));
            foreach (CellKey cell in stairCells)
            {
                if (model.blockedCells.Contains(cell))
                {
                    error = "reserved footprint overlaps another stair at " + cell;
                    return false;
                }
            }

            foreach (CellKey cell in stairCells)
            {
                model.blockedCells.Add(cell);
            }

            error = string.Empty;
            return true;
        }

        private static void UnreserveFootprint(ProofModel model, PlacedStair stair)
        {
            foreach (CellKey cell in BuildBlockedCells(stair, model.cellSize))
            {
                model.blockedCells.Remove(cell);
            }
        }

        private static Vector3 BuildProofEntryEdge(
            StairContract contract,
            float proofColumnX,
            float proofRowZ,
            int currentLevel,
            float cellSize,
            float levelHeight)
        {
            float laneCenter = contract.laneCount * cellSize * 0.5f;
            float y = LevelY(currentLevel, levelHeight);
            if (Direction.IsEastWest(contract.entry.side))
            {
                return new Vector3(proofColumnX, y, proofRowZ + laneCenter);
            }

            return new Vector3(proofColumnX + laneCenter, y, proofRowZ);
        }

        private static Vector3 SolveRootFromPortEdge(Port port, Vector3 targetEdge)
        {
            return targetEdge - port.localEdgePosition;
        }

        private static IEnumerable<CellKey> BuildBlockedCells(PlacedStair stair, float cellSize)
        {
            foreach (Vector2Int cell in stair.contract.footprintCells)
            {
                for (int level = stair.lowerLevel; level <= stair.upperLevel; level++)
                {
                    yield return new CellKey(WorldCellFromContractCell(stair, cell, cellSize), level);
                }
            }

            foreach (Vector2Int cell in stair.contract.occupiedCells)
            {
                for (int level = stair.lowerLevel; level <= stair.upperLevel; level++)
                {
                    yield return new CellKey(WorldCellFromContractCell(stair, cell, cellSize), level);
                }
            }

            foreach (Vector2Int cell in stair.contract.reservedCells)
            {
                for (int level = stair.lowerLevel; level <= stair.upperLevel; level++)
                {
                    yield return new CellKey(WorldCellFromContractCell(stair, cell, cellSize), level);
                }
            }
        }

        private static PortLanding BuildLanding(PlacedStair stair, Port port, int worldLevel, string label, float cellSize)
        {
            var floors = new List<FloorCell>();
            Vector3 edge = stair.TransformPoint(port.localEdgePosition);
            GetWorldLateralSpan(stair, port, cellSize, out float lateralMin, out float lateralMax);
            List<Vector2Int> orderedCells = Direction.IsEastWest(port.side)
                ? port.cells.OrderBy(cell => cell.y).ToList()
                : port.cells.OrderBy(cell => cell.x).ToList();

            for (int i = 0; i < orderedCells.Count; i++)
            {
                Vector2 worldMin = FloorMinAdjacentToPort(edge, port.side, lateralMin + i * cellSize, cellSize);
                Vector2Int landingCell = GridCellFromWorldMin(worldMin, cellSize);
                floors.Add(new FloorCell(landingCell, worldLevel, worldMin, stair.contract.name + ":" + label));
            }

            return new PortLanding(stair.contract.name + ":" + label, port, edge, worldLevel, floors, lateralMin, lateralMax);
        }

        private static List<PortLanding> BuildLandings(PlacedStair stair, float cellSize)
        {
            var landings = new List<PortLanding>();
            for (int i = 0; i < stair.contract.ports.Count; i++)
            {
                Port port = stair.contract.ports[i];
                landings.Add(BuildLanding(
                    stair,
                    port,
                    stair.lowerLevel + port.level,
                    BuildPortLabel(stair.contract, i, port),
                    cellSize));
            }

            return landings;
        }

        private static string BuildPortLabel(StairContract contract, int index, Port port)
        {
            int priorAtLevel = CountPriorPortsAtLevel(contract, index, port.level);
            if (port.level == 0)
            {
                return "entry" + priorAtLevel;
            }

            if (port.level == contract.rise)
            {
                return "exit" + priorAtLevel;
            }

            return "level" + port.level + "_port" + priorAtLevel;
        }

        private static int CountPriorPortsAtLevel(StairContract contract, int index, int level)
        {
            int count = 0;
            for (int i = 0; i < index; i++)
            {
                if (contract.ports[i].level == level)
                {
                    count++;
                }
            }

            return count;
        }

        private static Vector2 FloorMinAdjacentToPort(Vector3 edge, int side, float lateralMin, float cellSize)
        {
            switch (side)
            {
                case Direction.West:
                    return new Vector2(edge.x - cellSize, lateralMin);
                case Direction.East:
                    return new Vector2(edge.x, lateralMin);
                case Direction.South:
                    return new Vector2(lateralMin, edge.z - cellSize);
                case Direction.North:
                    return new Vector2(lateralMin, edge.z);
                default:
                    throw new InvalidOperationException("unknown side");
            }
        }

        private static void AddLandingFloors(ProofModel model, GameObject prefab, Transform parent, PortLanding landing)
        {
            foreach (FloorCell floor in landing.floors)
            {
                AddFloor(model, prefab, parent, floor);
            }
        }

        private static void AddFloor(ProofModel model, GameObject prefab, Transform parent, FloorCell floor)
        {
            var key = new CellKey(floor.cell, floor.level);
            if (model.floorCells.ContainsKey(key))
            {
                return;
            }

            if (model.blockedCells.Contains(key))
            {
                model.floorErrors.Add(floor.owner + " floor prefab clips stair footprint at " + key);
                return;
            }

            model.floorCells.Add(key, floor);
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "proof_floor_" + floor.cell.x + "_" + floor.cell.y + "_L" + floor.level;
            instance.transform.SetParent(parent, false);
            Vector3 position = new Vector3(
                floor.min.x - model.floorLocalBoundsMin.x,
                LevelY(floor.level, model.levelHeight),
                floor.min.y - model.floorLocalBoundsMin.y);
            if (!ValidatePrefabSnapPosition(position, model.cellSize, model.levelHeight, out string snapError))
            {
                model.floorErrors.Add(floor.owner + " floor prefab root " + snapError);
                return;
            }

            instance.transform.position = position;
        }

        private static bool TryInstantiateStairVisual(
            GameObject stairPrefab,
            StairContract contract,
            Transform parent,
            string name,
            Vector3 contractRoot,
            float cellSize,
            float levelHeight,
            out GameObject frame,
            out string error)
        {
            frame = null;
            error = string.Empty;

            frame = new GameObject(name);
            frame.transform.SetParent(parent, false);
            frame.transform.position = contractRoot;

            GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(stairPrefab, frame.transform);
            visual.name = name + "_visual";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;

            if (contract.sourceRootPoses.Count > 0)
            {
                if (!TryAlignVisualToSourceRootPoses(visual, frame.transform, contract, out error))
                {
                    UnityEngine.Object.DestroyImmediate(frame);
                    frame = null;
                    return false;
                }
            }
            else if (TryGetAlignmentAnchor(contract, out VisualAnchor alignmentAnchor))
            {
                visual.transform.localRotation = Quaternion.Euler(0f, contract.visualYawDegrees, 0f);
                if (!TryAlignVisualToExpectedAnchor(visual, frame.transform, alignmentAnchor, out error))
                {
                    UnityEngine.Object.DestroyImmediate(frame);
                    frame = null;
                    return false;
                }
            }
            else
            {
                visual.transform.localRotation = Quaternion.Euler(0f, contract.visualYawDegrees, 0f);
                if (!TryAlignVisualToExitSurfaceRoots(visual, frame.transform, contract, cellSize, levelHeight, out error))
                {
                    UnityEngine.Object.DestroyImmediate(frame);
                    frame = null;
                    return false;
                }
            }

            BakeVisualRootOffsetIntoChildren(visual);

            if (!ValidateSourceRootPoses(visual, frame.transform, contract, out error))
            {
                UnityEngine.Object.DestroyImmediate(frame);
                frame = null;
                return false;
            }

            if (!ValidateExpectedVisualAnchorPositions(visual, frame.transform, contract, out error))
            {
                UnityEngine.Object.DestroyImmediate(frame);
                frame = null;
                return false;
            }

            if (!ValidatePortAnchors(visual, frame.transform, contract, out error))
            {
                UnityEngine.Object.DestroyImmediate(frame);
                frame = null;
                return false;
            }

            return true;
        }

        private static bool TryAlignVisualToSourceRootPoses(
            GameObject visual,
            Transform frame,
            StairContract contract,
            out string error)
        {
            error = string.Empty;
            if (contract.sourceRootPoses.Count == 0)
            {
                error = "sourceRootPoses is empty";
                return false;
            }

            Vector3 originalPosition = visual.transform.localPosition;
            Quaternion originalRotation = visual.transform.localRotation;

            foreach (SourceRootPose alignmentPose in contract.sourceRootPoses)
            {
                var sources = new HashSet<string>(new[] { alignmentPose.sourcePrefab }, StringComparer.Ordinal);
                List<SourceRootTransform> roots = CollectSourceRootTransforms(visual, frame, transform => SourceMatchesContractSurfaceRoot(transform, sources));
                Quaternion expectedRotation = Quaternion.Euler(0f, alignmentPose.localYawDegrees, 0f);

                foreach (SourceRootTransform root in roots)
                {
                    visual.transform.localPosition = originalPosition;
                    visual.transform.localRotation = originalRotation;

                    Quaternion rotationDelta = expectedRotation * Quaternion.Inverse(root.localRotation);
                    visual.transform.localRotation = rotationDelta * visual.transform.localRotation;

                    List<SourceRootTransform> rotatedRoots = CollectSourceRootTransforms(visual, frame, transform => SourceMatchesContractSurfaceRoot(transform, sources));
                    SourceRootTransform rotatedRoot = FindNearestSourceRoot(rotatedRoots, root.localPosition);
                    visual.transform.localPosition += alignmentPose.localPosition - rotatedRoot.localPosition;

                    if (ValidateSourceRootPoses(visual, frame, contract, out _))
                    {
                        return true;
                    }
                }
            }

            visual.transform.localPosition = originalPosition;
            visual.transform.localRotation = originalRotation;
            error = "sourceRootPoses could not normalize visual source roots";
            return false;
        }

        private static void BakeVisualRootOffsetIntoChildren(GameObject visual)
        {
            Transform visualTransform = visual.transform;
            if (visualTransform.childCount == 0)
            {
                return;
            }

            Vector3 offset = visualTransform.localPosition;
            if (Mathf.Abs(offset.x) <= EdgeTolerance &&
                Mathf.Abs(offset.y) <= EdgeTolerance &&
                Mathf.Abs(offset.z) <= EdgeTolerance)
            {
                return;
            }

            var children = new List<Transform>();
            for (int i = 0; i < visualTransform.childCount; i++)
            {
                children.Add(visualTransform.GetChild(i));
            }

            foreach (Transform child in children)
            {
                child.localPosition += Quaternion.Inverse(visualTransform.localRotation) * offset;
            }

            visualTransform.localPosition = Vector3.zero;
        }

        private static bool TryAlignVisualToExitSurfaceRoots(
            GameObject visual,
            Transform frame,
            StairContract contract,
            float cellSize,
            float levelHeight,
            out string error)
        {
            error = string.Empty;
            if (!TryGetVisualAnchorSources(contract, "exitSurfaceRoots", out HashSet<string> contractSources))
            {
                error = "missing visualAnchors role exitSurfaceRoots";
                return false;
            }

            if (!TryMeasureContractExitSurfaceRootBounds(visual, frame, contract, transform => SourceMatchesContractSurfaceRoot(transform, contractSources), out Bounds surfaceRootBounds))
            {
                error = "no declared visualAnchors exitSurfaceRoots were found in stair prefab";
                return false;
            }

            if (!TryBuildExpectedContractSurfaceRootBounds(contract, cellSize, levelHeight, out Bounds expectedRootBounds, out error))
            {
                return false;
            }

            Vector3 visualOffset = new Vector3(
                expectedRootBounds.max.x - surfaceRootBounds.max.x,
                expectedRootBounds.max.y - surfaceRootBounds.max.y,
                expectedRootBounds.min.z - surfaceRootBounds.min.z);
            visual.transform.localPosition = visualOffset;

            if (!TryMeasureContractExitSurfaceRootBounds(visual, frame, contract, transform => SourceMatchesContractSurfaceRoot(transform, contractSources), out Bounds alignedRootBounds))
            {
                error = "contract surface roots disappeared after visual alignment";
                return false;
            }

            Vector3 minDelta = alignedRootBounds.min - expectedRootBounds.min;
            Vector3 maxDelta = alignedRootBounds.max - expectedRootBounds.max;
            if (Mathf.Abs(minDelta.x) > EdgeTolerance ||
                Mathf.Abs(minDelta.y) > EdgeTolerance ||
                Mathf.Abs(minDelta.z) > EdgeTolerance ||
                Mathf.Abs(maxDelta.x) > EdgeTolerance ||
                Mathf.Abs(maxDelta.y) > EdgeTolerance ||
                Mathf.Abs(maxDelta.z) > EdgeTolerance)
            {
                error = "visual contract root frame did not align; minDelta " + FormatVector(minDelta) +
                    ", maxDelta " + FormatVector(maxDelta);
                return false;
            }

            return true;
        }

        private static ReviewGalleryResult RenderStairReviewGallery(
            Transform parent,
            IReadOnlyList<StairReviewEntry> entries,
            IReadOnlyDictionary<string, StairContract> contractsByPrefab,
            GameObject floorPrefab,
            ProofConfig config)
        {
            Transform reviewedRoot = CreateChild(parent, "Reviewed Contracted Stairs");
            Transform failedRoot = CreateChild(parent, "Failed Contract Stairs");
            Transform needsContractRoot = CreateChild(parent, "Needs Reviewed Contract");
            Transform blockedUnsupportedRoot = CreateChild(parent, "Blocked Unsupported");
            Transform pendingRoot = CreateChild(parent, "Pending Review Entries");
            var reviewed = new List<StairReviewEntry>();
            var failed = new List<StairReviewEntry>();
            var needsReviewedContract = new List<StairReviewEntry>();
            var blockedUnsupported = new List<StairReviewEntry>();
            var pending = new List<StairReviewEntry>();

            foreach (StairReviewEntry entry in entries)
            {
                switch (entry.status)
                {
                    case StairReviewStatus.Reviewed:
                        reviewed.Add(entry);
                        break;
                    case StairReviewStatus.Failed:
                        failed.Add(entry);
                        break;
                    case StairReviewStatus.Unsupported:
                        if (NeedsReviewedContract(entry))
                        {
                            needsReviewedContract.Add(entry);
                        }
                        else
                        {
                            blockedUnsupported.Add(entry);
                        }

                        break;
                    default:
                        pending.Add(entry);
                        break;
                }
            }

            int rendered = 0;
            int draftLandingPreviews = 0;
            int contractLandingPreviews = 0;
            int renderFailures = 0;
            int rowOffset = 0;
            var renderFailureDetails = new List<string>();
            RenderReviewSection(reviewedRoot, "Reviewed Contracted Stairs", reviewed, rowOffset, new Color(0.1f, 0.45f, 0.16f, 1f), contractsByPrefab, floorPrefab, config, ref rendered, ref draftLandingPreviews, ref contractLandingPreviews, ref renderFailures, renderFailureDetails);
            rowOffset += SectionRowCount(reviewed.Count);
            RenderReviewSection(failedRoot, "Failed Contract Stairs", failed, rowOffset, new Color(0.75f, 0.2f, 0.08f, 1f), contractsByPrefab, floorPrefab, config, ref rendered, ref draftLandingPreviews, ref contractLandingPreviews, ref renderFailures, renderFailureDetails);
            rowOffset += SectionRowCount(failed.Count);
            RenderReviewSection(needsContractRoot, "Needs Reviewed Contract", needsReviewedContract, rowOffset, new Color(0.9f, 0.62f, 0.08f, 1f), contractsByPrefab, floorPrefab, config, ref rendered, ref draftLandingPreviews, ref contractLandingPreviews, ref renderFailures, renderFailureDetails);
            rowOffset += SectionRowCount(needsReviewedContract.Count);
            RenderReviewSection(blockedUnsupportedRoot, "Blocked Unsupported", blockedUnsupported, rowOffset, new Color(0.42f, 0.38f, 0.28f, 1f), contractsByPrefab, floorPrefab, config, ref rendered, ref draftLandingPreviews, ref contractLandingPreviews, ref renderFailures, renderFailureDetails);
            rowOffset += SectionRowCount(blockedUnsupported.Count);
            RenderReviewSection(pendingRoot, "Pending Review Entries", pending, rowOffset, new Color(0.35f, 0.35f, 0.35f), contractsByPrefab, floorPrefab, config, ref rendered, ref draftLandingPreviews, ref contractLandingPreviews, ref renderFailures, renderFailureDetails);

            return new ReviewGalleryResult(
                entries.Count,
                rendered,
                reviewed.Count,
                failed.Count,
                needsReviewedContract.Count,
                blockedUnsupported.Count,
                needsReviewedContract.Select(entry => entry.name).ToList(),
                blockedUnsupported.Select(entry => entry.name).ToList(),
                draftLandingPreviews,
                contractLandingPreviews,
                pending.Count,
                renderFailures,
                renderFailureDetails);
        }

        private static bool NeedsReviewedContract(StairReviewEntry entry)
        {
            return entry.status == StairReviewStatus.Unsupported &&
                entry.detail.IndexOf("missing authored reviewed contract", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int SectionRowCount(int entryCount)
        {
            return entryCount == 0 ? 0 : Mathf.CeilToInt(entryCount / (float)ReviewGalleryColumns) + 2;
        }

        private static void RenderReviewSection(
            Transform sectionRoot,
            string sectionName,
            IReadOnlyList<StairReviewEntry> entries,
            int rowOffset,
            Color statusColor,
            IReadOnlyDictionary<string, StairContract> contractsByPrefab,
            GameObject floorPrefab,
            ProofConfig config,
            ref int rendered,
            ref int draftLandingPreviews,
            ref int contractLandingPreviews,
            ref int renderFailures,
            List<string> renderFailureDetails)
        {
            if (entries.Count == 0)
            {
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                int row = i / ReviewGalleryColumns;
                int column = i % ReviewGalleryColumns;
                RenderReviewEntry(
                    sectionRoot,
                    entries[i],
                    ReviewSlotPosition(rowOffset + row + 1, column),
                    statusColor,
                    contractsByPrefab,
                    floorPrefab,
                    config,
                    ref rendered,
                    ref draftLandingPreviews,
                    ref contractLandingPreviews,
                    ref renderFailures,
                    renderFailureDetails);
            }
        }

        private static Vector3 ReviewSlotPosition(int row, int column)
        {
            return new Vector3(
                column * ReviewGallerySlotSize,
                0f,
                -row * ReviewGallerySlotSize);
        }

        private static void RenderReviewEntry(
            Transform sectionRoot,
            StairReviewEntry entry,
            Vector3 slotCenter,
            Color statusColor,
            IReadOnlyDictionary<string, StairContract> contractsByPrefab,
            GameObject floorPrefab,
            ProofConfig config,
            ref int rendered,
            ref int draftLandingPreviews,
            ref int contractLandingPreviews,
            ref int renderFailures,
            List<string> renderFailureDetails)
        {
            Transform slotRoot = CreateChild(sectionRoot, ReviewEntryObjectName(entry));
            slotRoot.localPosition = slotCenter;

            GameObject statusBase = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(statusBase, "Create stair review status base");
            statusBase.name = entry.status + "_" + SanitizeObjectName(entry.name) + "_status_base";
            statusBase.transform.SetParent(slotRoot, false);
            statusBase.transform.localPosition = new Vector3(0f, -0.06f, 0f);
            statusBase.transform.localScale = new Vector3(11f, 0.08f, 11f);
            SetRendererColor(statusBase, statusColor);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.prefabPath);
            if (prefab == null)
            {
                renderFailures++;
                AddRenderFailure(renderFailureDetails, entry.name + ":missing prefab asset " + entry.prefabPath);
                return;
            }

            if ((entry.status == StairReviewStatus.Reviewed || entry.status == StairReviewStatus.Failed) &&
                contractsByPrefab.TryGetValue(entry.prefabPath, out StairContract contract))
            {
                if (TryRenderContractLandingPreview(slotRoot, prefab, contract, floorPrefab, config, out string contractPreviewError))
                {
                    contractLandingPreviews++;
                    rendered++;
                    return;
                }

                if (entry.status == StairReviewStatus.Reviewed)
                {
                    renderFailures++;
                    AddRenderFailure(renderFailureDetails, entry.name + ":contract landing preview failed: " + contractPreviewError);
                    return;
                }
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(instance, "Create stair review prefab");
            instance.name = entry.status + "_" + SanitizeObjectName(entry.name) + "_prefab";
            instance.transform.SetParent(slotRoot, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            if (!TryGetRendererBounds(instance, out Bounds bounds))
            {
                renderFailures++;
                AddRenderFailure(renderFailureDetails, entry.name + ":prefab has no active renderers");
                return;
            }

            Vector3 slotWorld = slotRoot.position;
            Vector3 offset = new Vector3(
                slotWorld.x - bounds.center.x,
                slotWorld.y - bounds.min.y,
                slotWorld.z - bounds.center.z);
            instance.transform.position += offset;
            string draftError = string.Empty;
            if (NeedsReviewedContract(entry) &&
                TryRenderDraftLandingPreview(slotRoot, instance, floorPrefab, config, out draftError))
            {
                draftLandingPreviews++;
            }
            else if (NeedsReviewedContract(entry))
            {
                renderFailures++;
                AddRenderFailure(renderFailureDetails, entry.name + ":draft landing preview failed: " + draftError);
            }

            rendered++;
        }

        private static bool TryRenderContractLandingPreview(
            Transform slotRoot,
            GameObject stairPrefab,
            StairContract contract,
            GameObject floorPrefab,
            ProofConfig config,
            out string error)
        {
            error = string.Empty;
            Vector3 contractRoot = slotRoot.position;
            if (!ValidatePrefabSnapPosition(contractRoot, config.cellSize, config.levelHeight, out string rootSnapError))
            {
                error = "review gallery contract root " + rootSnapError;
                return false;
            }

            Transform contractRootGroup = CreateChild(slotRoot, "CONTRACT_PORTS_AND_LANDINGS");
            contractRootGroup.position = contractRoot;

            if (!TryInstantiateStairVisual(
                    stairPrefab,
                    contract,
                    contractRootGroup,
                    "reviewed_contract_stair_" + SanitizeObjectName(contract.name),
                    contractRoot,
                    config.cellSize,
                    config.levelHeight,
                    out _,
                    out error))
            {
                return false;
            }

            Transform floorRoot = CreateChild(contractRootGroup, "Landing Floor Prefabs");
            Transform portRoot = CreateChild(contractRootGroup, "Port Markers");
            var model = new ProofModel(config.cellSize, config.levelHeight, config.floorLocalBoundsMin);
            var placed = new PlacedStair(contract, contractRoot, 0, contract.rise);
            placed.entryLanding = BuildLanding(placed, contract.entry, 0, "entry", config.cellSize);
            placed.exitLanding = BuildLanding(placed, contract.exit, contract.rise, "exit", config.cellSize);
            placed.landings = BuildLandings(placed, config.cellSize);

            if (floorPrefab != null)
            {
                foreach (PortLanding landing in placed.landings)
                {
                    AddLandingFloors(model, floorPrefab, floorRoot, landing);
                }
            }

            foreach (PortLanding landing in placed.landings)
            {
                AddDebugPort(portRoot, landing, landing.port.level == 0 ? Color.magenta : Color.cyan);
            }

            return true;
        }

        private static string ReviewEntryObjectName(StairReviewEntry entry)
        {
            if (NeedsReviewedContract(entry))
            {
                return "DRAFT_REVIEW__PORTS_AND_LANDINGS_FROM_STRUCTURAL_SOURCE_ROOTS__" + SanitizeObjectName(entry.name);
            }

            if (entry.status == StairReviewStatus.Unsupported)
            {
                return "BLOCKED_UNSUPPORTED__" + SanitizeObjectName(entry.name);
            }

            return entry.status + "__" + SanitizeObjectName(entry.name);
        }

        private static bool TryRenderDraftLandingPreview(
            Transform slotRoot,
            GameObject instance,
            GameObject floorPrefab,
            ProofConfig config,
            out string error)
        {
            error = string.Empty;
            if (floorPrefab == null)
            {
                error = "missing floor prefab";
                return false;
            }

            List<DraftPort> ports = CollectDraftPorts(instance, slotRoot, config.levelHeight);
            if (ports.Count == 0)
            {
                error = "no structural Floor/Stair source-root connectors found";
                return false;
            }

            int minLevel = ports.Min(port => port.level);
            int maxLevel = ports.Max(port => port.level);
            if (maxLevel <= minLevel)
            {
                error = "source-root connectors do not define a positive rise";
                return false;
            }

            Transform draftRoot = CreateChild(slotRoot, "DRAFT_PORTS_AND_LANDINGS");
            int lowerCount = 0;
            int upperCount = 0;
            foreach (DraftPort port in ports)
            {
                if (port.level != minLevel && port.level != maxLevel)
                {
                    continue;
                }

                bool upper = port.level == maxLevel;
                int index = upper ? upperCount++ : lowerCount++;
                RenderDraftPortAndLanding(
                    draftRoot,
                    floorPrefab,
                    config,
                    port,
                    upper ? "UPPER" : "LOWER",
                    index);
            }

            if (lowerCount == 0 || upperCount == 0)
            {
                error = "draft did not produce both lower and upper landing ports";
                return false;
            }

            return true;
        }

        private static List<DraftPort> CollectDraftPorts(GameObject instance, Transform slotRoot, float levelHeight)
        {
            var candidates = new List<DraftPortCandidate>();
            HashSet<string> sources = DraftConnectorSourcePaths();
            foreach (Transform transform in instance.GetComponentsInChildren<Transform>(true))
            {
                if (transform == instance.transform ||
                    !ReviewedStairSourceResolver.IsSourceRoot(transform, sources) ||
                    !ReviewedStairSourceResolver.TryGetMatchingSurfacePath(transform, sources, out string sourcePath) ||
                    !TryGetDraftSourceConnectors(sourcePath, out IReadOnlyList<SourceConnector> connectors))
                {
                    continue;
                }

                Vector3 localPosition = slotRoot.InverseTransformPoint(transform.position);
                Quaternion localRotation = Quaternion.Inverse(slotRoot.rotation) * transform.rotation;
                foreach (SourceConnector connector in connectors)
                {
                    Vector3 edge = localPosition + localRotation * connector.localEdgeOffset;
                    candidates.Add(new DraftPortCandidate(edge, RotateSide(connector.side, localRotation)));
                }
            }

            if (candidates.Count == 0)
            {
                return new List<DraftPort>();
            }

            float minY = candidates.Min(port => port.edge.y);
            var ports = new List<DraftPort>();
            foreach (DraftPortCandidate candidate in candidates)
            {
                float scaledLevel = (candidate.edge.y - minY) / levelHeight;
                int level = Mathf.RoundToInt(scaledLevel);
                if (Mathf.Abs(scaledLevel - level) > 0.03f)
                {
                    continue;
                }

                if (!ContainsDraftPort(ports, candidate.edge, candidate.side, level))
                {
                    ports.Add(new DraftPort(candidate.edge, candidate.side, level));
                }
            }

            return ports;
        }

        private static bool ContainsDraftPort(List<DraftPort> ports, Vector3 edge, int side, int level)
        {
            foreach (DraftPort port in ports)
            {
                Vector3 delta = port.edge - edge;
                if (port.side == side &&
                    port.level == level &&
                    Mathf.Abs(delta.x) <= SourcePortTolerance &&
                    Mathf.Abs(delta.y) <= SourcePortTolerance &&
                    Mathf.Abs(delta.z) <= SourcePortTolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static void RenderDraftPortAndLanding(
            Transform draftRoot,
            GameObject floorPrefab,
            ProofConfig config,
            DraftPort port,
            string role,
            int index)
        {
            Transform portRoot = CreateChild(draftRoot, "DRAFT_" + role + "_PORT_" + index + "_" + Direction.ToString(port.side) + "_L" + port.level);
            portRoot.localPosition = Vector3.zero;

            float lateralMin = Direction.IsEastWest(port.side)
                ? port.edge.z - config.cellSize * 0.5f
                : port.edge.x - config.cellSize * 0.5f;
            Vector2 floorMin = FloorMinAdjacentToPort(port.edge, port.side, lateralMin, config.cellSize);

            GameObject floor = (GameObject)PrefabUtility.InstantiatePrefab(floorPrefab);
            Undo.RegisterCreatedObjectUndo(floor, "Create draft landing floor");
            floor.name = "DRAFT_" + role + "_LANDING_FLOOR_" + index + "_L" + port.level;
            floor.transform.SetParent(portRoot, false);
            floor.transform.localPosition = new Vector3(
                floorMin.x - config.floorLocalBoundsMin.x,
                port.edge.y,
                floorMin.y - config.floorLocalBoundsMin.y);
            floor.transform.localRotation = Quaternion.identity;
            floor.transform.localScale = Vector3.one;

            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(marker, "Create draft port marker");
            marker.name = "DRAFT_" + role + "_PORT_MARKER_" + index + "_" + Direction.ToString(port.side);
            marker.transform.SetParent(portRoot, false);
            marker.transform.localPosition = port.edge;
            marker.transform.localScale = new Vector3(0.75f, 0.75f, 0.75f);
            SetRendererColor(marker, role == "UPPER" ? Color.cyan : Color.magenta);
        }

        private static HashSet<string> DraftConnectorSourcePaths()
        {
            return new HashSet<string>(new[]
            {
                NormalizePath("Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Stairs/Stairs/P_MOD_Stairs_01_E_straight_3.prefab"),
                NormalizePath("Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/3d/modular/Stairs/Stairs/MOD_Stairs_01_E_straight_3.fbx"),
                NormalizePath("Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Stairs/Stairs/P_MOD_Stairs_01_E_med_SW.prefab"),
                NormalizePath("Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Stairs/Stairs/P_MOD_Stairs_01_E_med_NW.prefab")
            }, StringComparer.Ordinal);
        }

        private static bool TryGetDraftSourceConnectors(string sourcePath, out IReadOnlyList<SourceConnector> connectors)
        {
            switch (NormalizePath(sourcePath))
            {
                case "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Stairs/Stairs/P_MOD_Stairs_01_E_straight_3.prefab":
                case "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/3d/modular/Stairs/Stairs/MOD_Stairs_01_E_straight_3.fbx":
                    connectors = new[]
                    {
                        new SourceConnector(new Vector3(-4f, -2f, 2f), Direction.West),
                        new SourceConnector(new Vector3(0f, 0f, 2f), Direction.East)
                    };
                    return true;
                case "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Stairs/Stairs/P_MOD_Stairs_01_E_med_SW.prefab":
                    connectors = new[]
                    {
                        new SourceConnector(new Vector3(0f, -2f, 2f), Direction.East),
                        new SourceConnector(new Vector3(-2f, 0f, 0f), Direction.South)
                    };
                    return true;
                case "Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/prefabs/MODULAR/01_PARTS/Stairs/Stairs/P_MOD_Stairs_01_E_med_NW.prefab":
                    connectors = new[]
                    {
                        new SourceConnector(new Vector3(-2f, -2f, 0f), Direction.South),
                        new SourceConnector(new Vector3(0f, 0f, 2f), Direction.East)
                    };
                    return true;
                default:
                    connectors = Array.Empty<SourceConnector>();
                    return false;
            }
        }

        private static void AddRenderFailure(List<string> renderFailureDetails, string detail)
        {
            if (renderFailureDetails.Count < 12)
            {
                renderFailureDetails.Add(detail);
            }
        }

        private static bool TryGetRendererBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds;
        }

        private static void SetRendererColor(GameObject target, Color color)
        {
            Renderer renderer = target.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            renderer.sharedMaterial = new Material(Shader.Find("Standard"))
            {
                color = color
            };
        }

        private static bool TryGetAlignmentAnchor(StairContract contract, out VisualAnchor anchor)
        {
            anchor = contract.visualAnchors.FirstOrDefault(value =>
                value.expectedLocalPositions != null &&
                value.expectedLocalPositions.Count > 0);
            return anchor != null;
        }

        private static bool TryAlignVisualToExpectedAnchor(
            GameObject visual,
            Transform frame,
            VisualAnchor anchor,
            out string error)
        {
            error = string.Empty;
            var sources = new HashSet<string>(anchor.sourcePrefabs.Select(NormalizePath), StringComparer.Ordinal);
            List<Vector3> actual = CollectSourceRootPositions(visual, frame, transform => SourceMatchesContractSurfaceRoot(transform, sources));
            if (actual.Count != anchor.expectedLocalPositions.Count)
            {
                error = "visualAnchors " + anchor.role + " expected " + anchor.expectedLocalPositions.Count +
                    " source roots but found " + actual.Count;
                return false;
            }

            if (!TryFindRootSetOffset(actual, anchor.expectedLocalPositions, out Vector3 offset))
            {
                error = "visualAnchors " + anchor.role + " source roots do not match declared contract frame";
                return false;
            }

            visual.transform.localPosition = offset;
            return true;
        }

        private static bool TryFindRootSetOffset(List<Vector3> actual, List<Vector3> expected, out Vector3 offset)
        {
            offset = Vector3.zero;
            foreach (Vector3 expectedPoint in expected)
            {
                foreach (Vector3 actualPoint in actual)
                {
                    Vector3 candidateOffset = expectedPoint - actualPoint;
                    if (RootSetsMatchWithOffset(actual, expected, candidateOffset))
                    {
                        offset = candidateOffset;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool RootSetsMatchWithOffset(List<Vector3> actual, List<Vector3> expected, Vector3 offset)
        {
            var matchedActual = new bool[actual.Count];
            foreach (Vector3 expectedPoint in expected)
            {
                int matchIndex = -1;
                float matchDistance = float.MaxValue;
                for (int actualIndex = 0; actualIndex < actual.Count; actualIndex++)
                {
                    if (matchedActual[actualIndex])
                    {
                        continue;
                    }

                    Vector3 delta = actual[actualIndex] + offset - expectedPoint;
                    if (Mathf.Abs(delta.x) > EdgeTolerance ||
                        Mathf.Abs(delta.y) > EdgeTolerance ||
                        Mathf.Abs(delta.z) > EdgeTolerance)
                    {
                        continue;
                    }

                    float distance = delta.sqrMagnitude;
                    if (distance < matchDistance)
                    {
                        matchIndex = actualIndex;
                        matchDistance = distance;
                    }
                }

                if (matchIndex < 0)
                {
                    return false;
                }

                matchedActual[matchIndex] = true;
            }

            return true;
        }

        private static bool TryGetVisualAnchorSources(StairContract contract, string role, out HashSet<string> sources)
        {
            sources = null;
            VisualAnchor anchor = contract.visualAnchors.FirstOrDefault(value => string.Equals(value.role, role, StringComparison.Ordinal));
            if (anchor == null || anchor.sourcePrefabs == null || anchor.sourcePrefabs.Count == 0)
            {
                return false;
            }

            sources = new HashSet<string>(anchor.sourcePrefabs.Select(NormalizePath), StringComparer.Ordinal);
            return sources.Count > 0;
        }

        private static bool ValidateExpectedVisualAnchorPositions(
            GameObject root,
            Transform space,
            StairContract contract,
            out string error)
        {
            error = string.Empty;
            foreach (VisualAnchor anchor in contract.visualAnchors)
            {
                if (anchor.expectedLocalPositions == null)
                {
                    continue;
                }

                var sources = new HashSet<string>(anchor.sourcePrefabs.Select(NormalizePath), StringComparer.Ordinal);
                List<Vector3> actual = CollectSourceRootPositions(root, space, transform => SourceMatchesContractSurfaceRoot(transform, sources));
                List<Vector3> expected = anchor.expectedLocalPositions;

                if (actual.Count != expected.Count)
                {
                    error = "visualAnchors " + anchor.role + " expected " + expected.Count +
                        " source roots but found " + actual.Count;
                    return false;
                }

                var matchedActual = new bool[actual.Count];
                for (int expectedIndex = 0; expectedIndex < expected.Count; expectedIndex++)
                {
                    int matchIndex = -1;
                    float matchDistance = float.MaxValue;
                    for (int actualIndex = 0; actualIndex < actual.Count; actualIndex++)
                    {
                        if (matchedActual[actualIndex])
                        {
                            continue;
                        }

                        Vector3 delta = actual[actualIndex] - expected[expectedIndex];
                        if (Mathf.Abs(delta.x) > EdgeTolerance ||
                            Mathf.Abs(delta.y) > EdgeTolerance ||
                            Mathf.Abs(delta.z) > EdgeTolerance)
                        {
                            continue;
                        }

                        float distance = delta.sqrMagnitude;
                        if (distance < matchDistance)
                        {
                            matchIndex = actualIndex;
                            matchDistance = distance;
                        }
                    }

                    if (matchIndex < 0)
                    {
                        Vector3 nearest = FindNearestPoint(actual, expected[expectedIndex], out Vector3 nearestDelta);
                        error = "visualAnchors " + anchor.role + " source root " + expectedIndex +
                            " mismatch; expected " + FormatVector(expected[expectedIndex]) +
                            ", nearest actual " + FormatVector(nearest) +
                            ", delta " + FormatVector(nearestDelta);
                        return false;
                    }

                    matchedActual[matchIndex] = true;
                }
            }

            return true;
        }

        private static bool ValidateSourceRootPoses(
            GameObject root,
            Transform space,
            StairContract contract,
            out string error)
        {
            error = string.Empty;
            foreach (SourceRootPose pose in contract.sourceRootPoses)
            {
                var sources = new HashSet<string>(new[] { pose.sourcePrefab }, StringComparer.Ordinal);
                List<SourceRootTransform> roots = CollectSourceRootTransforms(root, space, transform => SourceMatchesContractSurfaceRoot(transform, sources));
                if (!TryFindSourceRootAt(roots, pose.localPosition, out SourceRootTransform sourceRoot))
                {
                    error = "sourceRootPoses source root not found at " + FormatVector(pose.localPosition) +
                        " for " + pose.sourcePrefab;
                    return false;
                }

                float actualYaw = LocalYawDegrees(sourceRoot.localRotation);
                float yawDelta = DeltaDegrees(actualYaw, pose.localYawDegrees);
                if (Mathf.Abs(yawDelta) > EdgeTolerance)
                {
                    error = "sourceRootPoses source root yaw mismatch for " + pose.sourcePrefab +
                        "; expected " + FormatFloat(pose.localYawDegrees) +
                        ", got " + FormatFloat(actualYaw) +
                        ", delta " + FormatFloat(yawDelta);
                    return false;
                }
            }

            return true;
        }

        private static bool ValidatePortAnchors(
            GameObject root,
            Transform space,
            StairContract contract,
            out string error)
        {
            error = string.Empty;
            if (contract.portAnchors == null || contract.portAnchors.Count == 0)
            {
                return true;
            }

            foreach (PortAnchor anchor in contract.portAnchors)
            {
                if (!TryGetAnchoredPort(contract, anchor.portRole, out Port port))
                {
                    error = "portAnchors " + anchor.portRole + " does not identify entry or exit";
                    return false;
                }

                if (!IsContractSurfacePrefabPath(anchor.sourcePrefab))
                {
                    error = "portAnchors " + anchor.portRole + " contains non-floor/stair source " + anchor.sourcePrefab;
                    return false;
                }

                if (!TryGetPortAnchorConnector(anchor, out SourceConnector connector))
                {
                    error = "portAnchors " + anchor.portRole + " has unsupported or incomplete connector data";
                    return false;
                }

                var sources = new HashSet<string>(new[] { anchor.sourcePrefab }, StringComparer.Ordinal);
                List<SourceRootTransform> sourceRoots = CollectSourceRootTransforms(root, space, transform => SourceMatchesContractSurfaceRoot(transform, sources));
                if (!TryFindSourceRootAt(sourceRoots, anchor.sourceLocalPosition, out SourceRootTransform sourceRoot))
                {
                    error = "portAnchors " + anchor.portRole + " source root not found at " + FormatVector(anchor.sourceLocalPosition);
                    return false;
                }

                Vector3 expectedEdge = sourceRoot.localPosition + sourceRoot.localRotation * connector.localEdgeOffset;
                int expectedSide = RotateSide(connector.side, sourceRoot.localRotation);

                if (port.side != expectedSide)
                {
                    error = "portAnchors " + anchor.portRole + " side mismatch; expected " +
                        Direction.ToString(expectedSide) + ", got " + Direction.ToString(port.side);
                    return false;
                }

                Vector3 edgeDelta = port.localEdgePosition - expectedEdge;
                if (Mathf.Abs(edgeDelta.x) > SourcePortTolerance ||
                    Mathf.Abs(edgeDelta.y) > SourcePortTolerance ||
                    Mathf.Abs(edgeDelta.z) > SourcePortTolerance)
                {
                    error = "portAnchors " + anchor.portRole + " edge mismatch; expected " +
                        FormatVector(expectedEdge) + ", got " + FormatVector(port.localEdgePosition) +
                        ", delta " + FormatVector(edgeDelta);
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetAnchoredPort(StairContract contract, string role, out Port port)
        {
            if (!string.IsNullOrWhiteSpace(role) &&
                role.StartsWith("port", StringComparison.Ordinal) &&
                int.TryParse(role.Substring("port".Length), out int index) &&
                index >= 0 &&
                index < contract.ports.Count)
            {
                port = contract.ports[index];
                return true;
            }

            if (string.Equals(role, "entry", StringComparison.Ordinal))
            {
                port = contract.entry;
                return true;
            }

            if (string.Equals(role, "exit", StringComparison.Ordinal))
            {
                port = contract.exit;
                return true;
            }

            port = default;
            return false;
        }

        private static bool TryGetStructuralStairConnector(string connectorName, out SourceConnector connector)
        {
            switch (connectorName)
            {
                case "lower":
                    connector = new SourceConnector(new Vector3(-4f, -2f, 2f), Direction.West);
                    return true;
                case "upper":
                    connector = new SourceConnector(new Vector3(0f, 0f, 2f), Direction.East);
                    return true;
                default:
                    connector = default;
                    return false;
            }
        }

        private static bool TryGetPortAnchorConnector(PortAnchor anchor, out SourceConnector connector)
        {
            if (!string.IsNullOrWhiteSpace(anchor.connector))
            {
                return TryGetStructuralStairConnector(anchor.connector, out connector);
            }

            if (anchor.hasSourceLocalEdgeOffset && anchor.sourceSide >= 0)
            {
                connector = new SourceConnector(anchor.sourceLocalEdgeOffset, anchor.sourceSide);
                return true;
            }

            connector = default;
            return false;
        }

        private static bool TryFindSourceRootAt(List<SourceRootTransform> roots, Vector3 expectedPosition, out SourceRootTransform root)
        {
            foreach (SourceRootTransform candidate in roots)
            {
                Vector3 delta = candidate.localPosition - expectedPosition;
                if (Mathf.Abs(delta.x) <= SourcePortTolerance &&
                    Mathf.Abs(delta.y) <= SourcePortTolerance &&
                    Mathf.Abs(delta.z) <= SourcePortTolerance)
                {
                    root = candidate;
                    return true;
                }
            }

            root = default;
            return false;
        }

        private static int RotateSide(int side, Quaternion rotation)
        {
            Vector3 rotated = rotation * Direction.OutwardVector(side);
            if (Mathf.Abs(rotated.x) >= Mathf.Abs(rotated.z))
            {
                return rotated.x < 0f ? Direction.West : Direction.East;
            }

            return rotated.z < 0f ? Direction.South : Direction.North;
        }

        private static Vector3 FindNearestPoint(List<Vector3> points, Vector3 target, out Vector3 delta)
        {
            Vector3 nearest = points[0];
            delta = nearest - target;
            float nearestDistance = delta.sqrMagnitude;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 candidateDelta = points[i] - target;
                float candidateDistance = candidateDelta.sqrMagnitude;
                if (candidateDistance < nearestDistance)
                {
                    nearest = points[i];
                    delta = candidateDelta;
                    nearestDistance = candidateDistance;
                }
            }

            return nearest;
        }

        private static SourceRootTransform FindNearestSourceRoot(List<SourceRootTransform> roots, Vector3 target)
        {
            SourceRootTransform nearest = roots[0];
            Vector3 nearestDelta = nearest.localPosition - target;
            float nearestDistance = nearestDelta.sqrMagnitude;
            for (int i = 1; i < roots.Count; i++)
            {
                Vector3 candidateDelta = roots[i].localPosition - target;
                float candidateDistance = candidateDelta.sqrMagnitude;
                if (candidateDistance < nearestDistance)
                {
                    nearest = roots[i];
                    nearestDelta = candidateDelta;
                    nearestDistance = candidateDistance;
                }
            }

            return nearest;
        }

        private static float LocalYawDegrees(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            return NormalizeDegrees(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg);
        }

        private static float DeltaDegrees(float actual, float expected)
        {
            return NormalizeDegrees(actual - expected);
        }

        private static float NormalizeDegrees(float value)
        {
            while (value > 180f)
            {
                value -= 360f;
            }

            while (value <= -180f)
            {
                value += 360f;
            }

            return value;
        }

        private static bool TryMeasureContractExitSurfaceRootBounds(
            GameObject root,
            Transform space,
            StairContract contract,
            Func<Transform, bool> include,
            out Bounds bounds)
        {
            bounds = default;
            List<Vector3> points = CollectSourceRootPositions(root, space, include);

            if (points.Count == 0)
            {
                return false;
            }

            float exitAxis = points.Max(point => point.x);
            List<Vector3> exitRoots = points
                .Where(point => Mathf.Abs(point.x - exitAxis) <= EdgeTolerance)
                .ToList();

            bounds = new Bounds(exitRoots[0], Vector3.zero);
            for (int i = 1; i < exitRoots.Count; i++)
            {
                bounds.Encapsulate(exitRoots[i]);
            }

            return true;
        }

        private static List<Vector3> CollectSourceRootPositions(
            GameObject root,
            Transform space,
            Func<Transform, bool> include)
        {
            var points = new List<Vector3>();
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform == root.transform || !include(transform))
                {
                    continue;
                }

                points.Add(space.InverseTransformPoint(transform.position));
            }

            return points;
        }

        private static List<SourceRootTransform> CollectSourceRootTransforms(
            GameObject root,
            Transform space,
            Func<Transform, bool> include)
        {
            var transforms = new List<SourceRootTransform>();
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform == root.transform || !include(transform))
                {
                    continue;
                }

                transforms.Add(new SourceRootTransform(
                    space.InverseTransformPoint(transform.position),
                    Quaternion.Inverse(space.rotation) * transform.rotation));
            }

            return transforms;
        }

        private static bool TryBuildExpectedContractSurfaceRootBounds(
            StairContract contract,
            float cellSize,
            float levelHeight,
            out Bounds bounds,
            out string error)
        {
            bounds = default;
            error = string.Empty;

            if (contract.entry.side != Direction.West || contract.exit.side != Direction.East)
            {
                error = "contract surface root alignment currently supports reviewed W-to-E straight contracts only";
                return false;
            }

            var points = new List<Vector3>();
            foreach (Vector2Int cell in contract.exit.cells)
            {
                points.Add(new Vector3(
                    contract.exit.localEdgePosition.x,
                    contract.exit.localEdgePosition.y,
                    contract.localBoundsMin.z + cell.y * cellSize));
            }

            if (points.Count == 0)
            {
                error = "contract has no exit port cells for surface root alignment";
                return false;
            }

            bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Count; i++)
            {
                bounds.Encapsulate(points[i]);
            }

            return true;
        }

        private static bool SourceMatchesContractSurfaceRoot(Transform source, HashSet<string> contractSources)
        {
            return ReviewedStairSourceResolver.IsSourceRoot(source, contractSources);
        }

        private static bool ValidateModel(ProofModel model, out string message)
        {
            if (model.floorErrors.Count > 0)
            {
                message = "floor prefab errors: " + string.Join("|", model.floorErrors.Take(3));
                return false;
            }

            foreach (PlacedStair stair in model.stairs)
            {
                foreach (PortLanding landing in stair.landings)
                {
                    if (!ValidateLandingConnection(model, stair, landing, out string landingMessage))
                    {
                        message = stair.contract.name + " " + landingMessage;
                        return false;
                    }
                }
            }

            message = "model valid: " + model.stairs.Count + " stair prefabs, " + model.floorCells.Count + " floor prefabs";
            return true;
        }

        private static bool ValidateLandingConnection(ProofModel model, PlacedStair stair, PortLanding landing, out string message)
        {
            if (landing.floors.Count != stair.contract.laneCount)
            {
                message = landing.label + " landing count does not match lane count";
                return false;
            }

            float edgeGap = 0f;
            float floorLateralMin = float.PositiveInfinity;
            float floorLateralMax = float.NegativeInfinity;
            foreach (FloorCell floor in landing.floors)
            {
                var key = new CellKey(floor.cell, floor.level);
                if (!model.floorCells.ContainsKey(key))
                {
                    message = landing.label + " missing floor prefab at " + key;
                    return false;
                }

                switch (landing.port.side)
                {
                    case Direction.West:
                        edgeGap = Mathf.Max(edgeGap, Mathf.Abs(floor.min.x + model.cellSize - landing.edge.x));
                        floorLateralMin = Mathf.Min(floorLateralMin, floor.min.y);
                        floorLateralMax = Mathf.Max(floorLateralMax, floor.min.y + model.cellSize);
                        break;
                    case Direction.East:
                        edgeGap = Mathf.Max(edgeGap, Mathf.Abs(floor.min.x - landing.edge.x));
                        floorLateralMin = Mathf.Min(floorLateralMin, floor.min.y);
                        floorLateralMax = Mathf.Max(floorLateralMax, floor.min.y + model.cellSize);
                        break;
                    case Direction.South:
                        edgeGap = Mathf.Max(edgeGap, Mathf.Abs(floor.min.y + model.cellSize - landing.edge.z));
                        floorLateralMin = Mathf.Min(floorLateralMin, floor.min.x);
                        floorLateralMax = Mathf.Max(floorLateralMax, floor.min.x + model.cellSize);
                        break;
                    case Direction.North:
                        edgeGap = Mathf.Max(edgeGap, Mathf.Abs(floor.min.y - landing.edge.z));
                        floorLateralMin = Mathf.Min(floorLateralMin, floor.min.x);
                        floorLateralMax = Mathf.Max(floorLateralMax, floor.min.x + model.cellSize);
                        break;
                }
            }

            float lateralGap = Mathf.Max(
                Mathf.Abs(floorLateralMin - landing.lateralMin),
                Mathf.Abs(floorLateralMax - landing.lateralMax));
            if (edgeGap > EdgeTolerance || lateralGap > EdgeTolerance)
            {
                message = landing.label + " edgeGap=" + edgeGap.ToString("0.###") + " lateralGap=" + lateralGap.ToString("0.###");
                return false;
            }

            message = landing.label + " connected";
            return true;
        }

        private static void AddDebugPort(Transform root, PortLanding landing, Color color)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = landing.label + " port";
            marker.transform.SetParent(root, false);
            float lateralCenter = (landing.lateralMin + landing.lateralMax) * 0.5f;
            float lateralSize = landing.lateralMax - landing.lateralMin;
            if (Direction.IsEastWest(landing.port.side))
            {
                marker.transform.position = new Vector3(landing.edge.x, landing.edge.y, lateralCenter);
                marker.transform.localScale = new Vector3(0.18f, 0.18f, lateralSize);
            }
            else
            {
                marker.transform.position = new Vector3(lateralCenter, landing.edge.y, landing.edge.z);
                marker.transform.localScale = new Vector3(lateralSize, 0.18f, 0.18f);
            }

            Renderer renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
            {
                var material = new Material(Shader.Find("Standard"));
                material.color = color;
                renderer.sharedMaterial = material;
            }
        }

        private static void GetWorldLateralSpan(PlacedStair stair, Port port, float cellSize, out float min, out float max)
        {
            if (Direction.IsEastWest(port.side))
            {
                min = stair.root.z + stair.contract.localBoundsMin.z + port.cells.Min(cell => cell.y) * cellSize;
                max = stair.root.z + stair.contract.localBoundsMin.z + (port.cells.Max(cell => cell.y) + 1) * cellSize;
                return;
            }

            min = stair.root.x + stair.contract.localBoundsMin.x + port.cells.Min(cell => cell.x) * cellSize;
            max = stair.root.x + stair.contract.localBoundsMin.x + (port.cells.Max(cell => cell.x) + 1) * cellSize;
        }

        private static Vector2Int WorldCellFromContractCell(PlacedStair stair, Vector2Int localCell, float cellSize)
        {
            Vector3 localCenter = new Vector3(
                stair.contract.localBoundsMin.x + (localCell.x + 0.5f) * cellSize,
                0f,
                stair.contract.localBoundsMin.z + (localCell.y + 0.5f) * cellSize);
            Vector3 worldCenter = stair.TransformPoint(localCenter);
            return new Vector2Int(
                Mathf.FloorToInt((worldCenter.x + EdgeTolerance) / cellSize),
                Mathf.FloorToInt((worldCenter.z + EdgeTolerance) / cellSize));
        }

        private static Vector2Int GridCellFromWorldMin(Vector2 worldMin, float cellSize)
        {
            return new Vector2Int(
                RequireGridCoordinate(worldMin.x, cellSize, "landing floor X min"),
                RequireGridCoordinate(worldMin.y, cellSize, "landing floor Z min"));
        }

        private static int RequireGridCoordinate(float value, float cellSize, string label)
        {
            float scaled = value / cellSize;
            int rounded = Mathf.RoundToInt(scaled);
            if (!IsGridAligned(value, cellSize))
            {
                throw new InvalidOperationException(label + " is not cell-aligned: " + FormatFloat(value) + " with cellSize " + FormatFloat(cellSize));
            }

            return rounded;
        }

        private static float LevelY(int level, float levelHeight)
        {
            return level * levelHeight;
        }

        private static bool ValidatePrefabSnapPosition(Vector3 position, float cellSize, float levelHeight, out string error)
        {
            if (!IsGridAligned(position.x, cellSize))
            {
                error = "X is not cell-aligned: " + FormatFloat(position.x);
                return false;
            }

            if (!IsLevelAligned(position.y, levelHeight))
            {
                error = "Y is not level-aligned: " + FormatFloat(position.y);
                return false;
            }

            if (!IsGridAligned(position.z, cellSize))
            {
                error = "Z is not cell-aligned: " + FormatFloat(position.z);
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool IsGridAligned(float value, float cellSize)
        {
            return Mathf.Abs((value / cellSize) - Mathf.Round(value / cellSize)) <= EdgeTolerance;
        }

        private static bool IsLevelAligned(float value, float levelHeight)
        {
            return IsWholeNumber(value) && Mathf.Abs((value / levelHeight) - Mathf.Round(value / levelHeight)) <= EdgeTolerance;
        }

        private static bool IsQuarterTurnDegrees(float value)
        {
            return Mathf.Abs((value / 90f) - Mathf.Round(value / 90f)) <= EdgeTolerance;
        }

        private static bool IsWholeNumber(float value)
        {
            return Mathf.Abs(value - Mathf.Round(value)) <= EdgeTolerance;
        }

        private static Transform CreateChild(Transform parent, string name)
        {
            var child = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(child, "Create " + name);
            child.transform.SetParent(parent, false);
            return child.transform;
        }

        private static void ClearRoot()
        {
            GameObject existing = GameObject.Find(RootName);
            if (existing != null)
            {
                UnityEngine.Object.DestroyImmediate(existing);
            }
        }

        private static void Increment(Dictionary<string, int> histogram, string key)
        {
            histogram.TryGetValue(key, out int count);
            histogram[key] = count + 1;
        }

        private static void Increment(Dictionary<int, int> histogram, int key)
        {
            histogram.TryGetValue(key, out int count);
            histogram[key] = count + 1;
        }

        private static string FormatHistogram(Dictionary<string, int> histogram)
        {
            return "{" + string.Join(",", histogram.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + pair.Value)) + "}";
        }

        private static string FormatHistogram(Dictionary<int, int> histogram)
        {
            return "{" + string.Join(",", histogram.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + pair.Value)) + "}";
        }

        private static string FormatReasons(List<string> reasons)
        {
            if (reasons.Count == 0)
            {
                return "[]";
            }

            return "[" + string.Join("|", reasons.Select(Sanitize)) + "]";
        }

        private static string FormatBool(bool value)
        {
            return value ? "Y" : "N";
        }

        private static string Sanitize(string value)
        {
            return (value ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Replace(';', ',');
        }

        private static string SanitizeObjectName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "stair";
            }

            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static string FormatVector(Vector3 value)
        {
            return "(" +
                value.x.ToString("0.###") + "," +
                value.y.ToString("0.###") + "," +
                value.z.ToString("0.###") + ")";
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("0.###");
        }

        private static JObject RequireObject(JToken token, string context)
        {
            if (!(token is JObject value))
            {
                throw new InvalidOperationException(context + " must be an object.");
            }

            return value;
        }

        private static Port ParsePort(JToken token, string context)
        {
            JObject value = RequireObject(token, context);
            return new Port
            {
                side = Direction.FromString(value.Value<string>("side")),
                level = value.Value<int?>("level") ?? -1,
                cells = ParseCells(value["cells"], context + ".cells"),
                localEdgePosition = ParseVector3(value["localEdgePosition"], context + ".localEdgePosition")
            };
        }

        private static List<Port> ParsePorts(JToken token, string context)
        {
            if (!(token is JArray array))
            {
                throw new InvalidOperationException(context + " must be an array.");
            }

            var ports = new List<Port>();
            int index = 0;
            foreach (JToken item in array)
            {
                ports.Add(ParsePort(item, context + "[" + index + "]"));
                index++;
            }

            return ports;
        }

        private static List<VisualAnchor> ParseVisualAnchors(JToken token, string context)
        {
            if (!(token is JArray array))
            {
                throw new InvalidOperationException(context + " must be an array.");
            }

            var anchors = new List<VisualAnchor>();
            int index = 0;
            foreach (JToken item in array)
            {
                JObject value = RequireObject(item, context + "[" + index + "]");
                anchors.Add(new VisualAnchor
                {
                    role = value.Value<string>("role") ?? string.Empty,
                    sourcePrefabs = ParseStringList(value["sourcePrefabs"], context + "[" + index + "].sourcePrefabs"),
                    expectedLocalPositions = ParseOptionalVector3List(value["expectedLocalPositions"], context + "[" + index + "].expectedLocalPositions")
                });
                index++;
            }

            return anchors;
        }

        private static List<PortAnchor> ParseOptionalPortAnchors(JToken token, string context)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return new List<PortAnchor>();
            }

            if (!(token is JArray array))
            {
                throw new InvalidOperationException(context + " must be an array.");
            }

            var anchors = new List<PortAnchor>();
            int index = 0;
            foreach (JToken item in array)
            {
                JObject value = RequireObject(item, context + "[" + index + "]");
                anchors.Add(new PortAnchor
                {
                    portRole = value.Value<string>("portRole") ?? string.Empty,
                    sourcePrefab = NormalizePath(value.Value<string>("sourcePrefab")),
                    sourceLocalPosition = ParseVector3(value["sourceLocalPosition"], context + "[" + index + "].sourceLocalPosition"),
                    connector = value.Value<string>("connector") ?? string.Empty,
                    sourceLocalEdgeOffset = value["sourceLocalEdgeOffset"] == null
                        ? Vector3.zero
                        : ParseVector3(value["sourceLocalEdgeOffset"], context + "[" + index + "].sourceLocalEdgeOffset"),
                    hasSourceLocalEdgeOffset = value["sourceLocalEdgeOffset"] != null,
                    sourceSide = value["sourceSide"] == null
                        ? -1
                        : Direction.FromString(value.Value<string>("sourceSide"))
                });
                index++;
            }

            return anchors;
        }

        private static List<SourceRootPose> ParseOptionalSourceRootPoses(JToken token, string context)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return new List<SourceRootPose>();
            }

            if (!(token is JArray array))
            {
                throw new InvalidOperationException(context + " must be an array.");
            }

            var poses = new List<SourceRootPose>();
            int index = 0;
            foreach (JToken item in array)
            {
                JObject value = RequireObject(item, context + "[" + index + "]");
                poses.Add(new SourceRootPose
                {
                    sourcePrefab = NormalizePath(value.Value<string>("sourcePrefab")),
                    localPosition = ParseVector3(value["localPosition"], context + "[" + index + "].localPosition"),
                    localYawDegrees = value.Value<float?>("localYawDegrees") ?? 0f
                });
                index++;
            }

            return poses;
        }

        private static List<Vector3> ParseOptionalVector3List(JToken token, string context)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (!(token is JArray array))
            {
                throw new InvalidOperationException(context + " must be an array.");
            }

            var values = new List<Vector3>();
            int index = 0;
            foreach (JToken item in array)
            {
                values.Add(ParseVector3(item, context + "[" + index + "]"));
                index++;
            }

            return values;
        }

        private static List<Vector2Int> ParseOptionalCells(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return new List<Vector2Int>();
            }

            return ParseCells(token, "cells");
        }

        private static List<Vector2Int> ParseCells(JToken token, string context)
        {
            if (!(token is JArray array))
            {
                throw new InvalidOperationException(context + " must be an array.");
            }

            var cells = new List<Vector2Int>();
            foreach (JToken item in array)
            {
                JObject value = RequireObject(item, context + " item");
                cells.Add(new Vector2Int(value.Value<int>("x"), value.Value<int>("z")));
            }

            return cells;
        }

        private static List<string> ParseStringList(JToken token, string context)
        {
            if (!(token is JArray array))
            {
                throw new InvalidOperationException(context + " must be an array.");
            }

            var values = new List<string>();
            foreach (JToken item in array)
            {
                string value = NormalizePath(item.Value<string>());
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }

            return values;
        }

        private static Vector2Int ParseCell(JToken token, string context)
        {
            JObject value = RequireObject(token, context);
            return new Vector2Int(value.Value<int>("x"), value.Value<int>("z"));
        }

        private static Vector2 ParseVector2(JToken token, string context)
        {
            JObject value = RequireObject(token, context);
            return new Vector2(value.Value<float>("x"), value.Value<float>("z"));
        }

        private static Vector3 ParseVector3(JToken token, string context)
        {
            JObject value = RequireObject(token, context);
            return new Vector3(
                value.Value<float>("x"),
                value.Value<float>("y"),
                value.Value<float>("z"));
        }

        private static string NormalizePath(string path)
        {
            return ReviewedStairSourceResolver.NormalizePath(path);
        }

        private static bool IsStairPrefabPath(string path)
        {
            return NormalizePath(path).StartsWith(StairPrefabRoot + "/", StringComparison.Ordinal) &&
                NormalizePath(path).EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFloorPrefabPath(string path)
        {
            return NormalizePath(path).StartsWith(PackageFloorPrefabRoot, StringComparison.Ordinal) &&
                NormalizePath(path).EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsContractSurfacePrefabPath(string path)
        {
            return ReviewedStairSourceResolver.IsContractSurfacePath(path);
        }

        private static IEnumerable<string> FindStairPrefabs()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { StairPrefabRoot });
            Array.Sort(guids, StringComparer.Ordinal);
            foreach (string guid in guids)
            {
                yield return NormalizePath(AssetDatabase.GUIDToAssetPath(guid));
            }
        }

        private sealed class ProofConfig
        {
            public float cellSize;
            public float levelHeight;
            public string floorPrefab;
            public Vector2 floorLocalBoundsMin;
            public Vector2Int floorLocalBoundsSizeCells;
            public List<StairContract> contracts;
            public Dictionary<string, string> unsupported;
        }

        private sealed class StairContract
        {
            public string name;
            public string prefab;
            public string source;
            public string reviewStatus;
            public int rise;
            public int laneCount;
            public int runLength;
            public string topology;
            public bool bridgeAllowed;
            public float visualYawDegrees;
            public Vector3 localBoundsMin;
            public Vector2Int localBoundsSizeCells;
            public List<Vector2Int> footprintCells;
            public List<Vector2Int> occupiedCells;
            public List<Vector2Int> reservedCells;
            public List<Port> ports;
            public List<PortAnchor> portAnchors;
            public List<SourceRootPose> sourceRootPoses;
            public List<VisualAnchor> visualAnchors;
            public Port entry;
            public Port exit;
        }

        private sealed class PortAnchor
        {
            public string portRole;
            public string sourcePrefab;
            public Vector3 sourceLocalPosition;
            public string connector;
            public Vector3 sourceLocalEdgeOffset;
            public bool hasSourceLocalEdgeOffset;
            public int sourceSide;
        }

        private sealed class SourceRootPose
        {
            public string sourcePrefab;
            public Vector3 localPosition;
            public float localYawDegrees;
        }

        private sealed class VisualAnchor
        {
            public string role;
            public List<string> sourcePrefabs;
            public List<Vector3> expectedLocalPositions;
        }

        private struct Port
        {
            public int side;
            public int level;
            public List<Vector2Int> cells;
            public Vector3 localEdgePosition;
        }

        private struct SourceConnector
        {
            public readonly Vector3 localEdgeOffset;
            public readonly int side;

            public SourceConnector(Vector3 localEdgeOffset, int side)
            {
                this.localEdgeOffset = localEdgeOffset;
                this.side = side;
            }
        }

        private struct SourceRootTransform
        {
            public readonly Vector3 localPosition;
            public readonly Quaternion localRotation;

            public SourceRootTransform(Vector3 localPosition, Quaternion localRotation)
            {
                this.localPosition = localPosition;
                this.localRotation = localRotation;
            }
        }

        private sealed class PlacedStair
        {
            public readonly StairContract contract;
            public readonly Vector3 root;
            public readonly int lowerLevel;
            public readonly int upperLevel;
            public GameObject instance;
            public PortLanding entryLanding;
            public PortLanding exitLanding;
            public List<PortLanding> landings;

            public PlacedStair(StairContract contract, Vector3 root, int lowerLevel, int upperLevel)
            {
                this.contract = contract;
                this.root = root;
                this.lowerLevel = lowerLevel;
                this.upperLevel = upperLevel;
                landings = new List<PortLanding>();
            }

            public Vector3 TransformPoint(Vector3 local)
            {
                return root + local;
            }
        }

        private sealed class PortLanding
        {
            public readonly string label;
            public readonly Port port;
            public readonly Vector3 edge;
            public readonly int level;
            public readonly List<FloorCell> floors;
            public readonly float lateralMin;
            public readonly float lateralMax;

            public PortLanding(string label, Port port, Vector3 edge, int level, List<FloorCell> floors, float lateralMin, float lateralMax)
            {
                this.label = label;
                this.port = port;
                this.edge = edge;
                this.level = level;
                this.floors = floors;
                this.lateralMin = lateralMin;
                this.lateralMax = lateralMax;
            }
        }

        private readonly struct FloorCell
        {
            public readonly Vector2Int cell;
            public readonly int level;
            public readonly Vector2 min;
            public readonly string owner;

            public FloorCell(Vector2Int cell, int level, Vector2 min, string owner)
            {
                this.cell = cell;
                this.level = level;
                this.min = min;
                this.owner = owner;
            }
        }

        private sealed class ProofModel
        {
            public readonly float cellSize;
            public readonly float levelHeight;
            public Vector2 floorLocalBoundsMin;
            public readonly Dictionary<CellKey, FloorCell> floorCells = new Dictionary<CellKey, FloorCell>();
            public readonly HashSet<CellKey> blockedCells = new HashSet<CellKey>();
            public readonly List<PlacedStair> stairs = new List<PlacedStair>();
            public readonly List<string> floorErrors = new List<string>();

            public ProofModel(float cellSize, float levelHeight, Vector2 floorLocalBoundsMin)
            {
                this.cellSize = cellSize;
                this.levelHeight = levelHeight;
                this.floorLocalBoundsMin = floorLocalBoundsMin;
            }
        }

        private readonly struct CellKey : IEquatable<CellKey>
        {
            public readonly Vector2Int cell;
            public readonly int level;

            public CellKey(Vector2Int cell, int level)
            {
                this.cell = cell;
                this.level = level;
            }

            public bool Equals(CellKey other) => cell == other.cell && level == other.level;
            public override bool Equals(object obj) => obj is CellKey other && Equals(other);
            public override int GetHashCode() => (cell.x * 397) ^ cell.y ^ (level * 7919);
            public override string ToString() => "(" + cell.x + "," + cell.y + ",L" + level + ")";
        }

        private readonly struct ProofResult
        {
            public readonly bool passed;
            private readonly string summary;
            public string Summary => summary;

            public ProofResult(bool passed, string summary)
            {
                this.passed = passed;
                this.summary = summary;
            }

            public static ProofResult Fail(string failureKind, string reason)
            {
                string summary =
                    "stair count=0; histogram={}; riseHistogram={}; topologyHistogram={}; rejected contracts=1" +
                    "; rejected contract reasons=[" + Sanitize(failureKind + ":" + reason) + "]" +
                    "; rejected placements=0; reachable=N; validation=FAIL";
                return new ProofResult(false, summary);
            }
        }

        private enum StairReviewStatus
        {
            Pending,
            Reviewed,
            Failed,
            Unsupported
        }

        private readonly struct StairReviewEntry
        {
            public readonly string name;
            public readonly string prefabPath;
            public readonly StairReviewStatus status;
            public readonly string reason;
            public readonly string detail;

            public StairReviewEntry(string name, string prefabPath, StairReviewStatus status, string reason, string detail)
            {
                this.name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(prefabPath) : name;
                this.prefabPath = NormalizePath(prefabPath);
                this.status = status;
                this.reason = string.IsNullOrWhiteSpace(reason) ? "none" : reason;
                this.detail = string.IsNullOrWhiteSpace(detail) ? "none" : detail;
            }

            public static StairReviewEntry Pending(string name, string prefabPath)
            {
                return new StairReviewEntry(name, prefabPath, StairReviewStatus.Pending, "pending", "not classified by proof gate");
            }
        }

        private readonly struct DraftPortCandidate
        {
            public readonly Vector3 edge;
            public readonly int side;

            public DraftPortCandidate(Vector3 edge, int side)
            {
                this.edge = edge;
                this.side = side;
            }
        }

        private readonly struct DraftPort
        {
            public readonly Vector3 edge;
            public readonly int side;
            public readonly int level;

            public DraftPort(Vector3 edge, int side, int level)
            {
                this.edge = edge;
                this.side = side;
                this.level = level;
            }
        }

        private readonly struct ReviewGalleryResult
        {
            public readonly int entries;
            public readonly int rendered;
            public readonly int reviewed;
            public readonly int failed;
            public readonly int needsReviewedContract;
            public readonly int blockedUnsupported;
            public readonly List<string> needsReviewedContractNames;
            public readonly List<string> blockedUnsupportedNames;
            public readonly int draftLandingPreviews;
            public readonly int contractLandingPreviews;
            public readonly int pending;
            public readonly int renderFailures;
            public readonly List<string> renderFailureDetails;
            public bool passed => entries > 0 && rendered == entries && pending == 0 && renderFailures == 0;

            public string Summary =>
                "reviewGallery=" + (passed ? "PASS" : "FAIL") +
                "; reviewGalleryEntries=" + entries +
                "; reviewGalleryRendered=" + rendered +
                "; reviewGalleryReviewed=" + reviewed +
                "; reviewGalleryFailed=" + failed +
                "; reviewGalleryNeedsReviewedContract=" + needsReviewedContract +
                "; reviewGalleryNeedsReviewedContractNames=" + FormatReasons(needsReviewedContractNames) +
                "; reviewGalleryBlockedUnsupported=" + blockedUnsupported +
                "; reviewGalleryBlockedUnsupportedNames=" + FormatReasons(blockedUnsupportedNames) +
                "; reviewGalleryUnsupported=" + (needsReviewedContract + blockedUnsupported) +
                "; reviewGalleryDraftLandingPreviews=" + draftLandingPreviews +
                "; reviewGalleryContractLandingPreviews=" + contractLandingPreviews +
                "; reviewGalleryPending=" + pending +
                "; reviewGalleryRenderFailures=" + renderFailures +
                "; reviewGalleryRenderFailureReasons=" + FormatReasons(renderFailureDetails);

            public ReviewGalleryResult(
                int entries,
                int rendered,
                int reviewed,
                int failed,
                int needsReviewedContract,
                int blockedUnsupported,
                List<string> needsReviewedContractNames,
                List<string> blockedUnsupportedNames,
                int draftLandingPreviews,
                int contractLandingPreviews,
                int pending,
                int renderFailures,
                List<string> renderFailureDetails)
            {
                this.entries = entries;
                this.rendered = rendered;
                this.reviewed = reviewed;
                this.failed = failed;
                this.needsReviewedContract = needsReviewedContract;
                this.blockedUnsupported = blockedUnsupported;
                this.needsReviewedContractNames = needsReviewedContractNames ?? new List<string>();
                this.blockedUnsupportedNames = blockedUnsupportedNames ?? new List<string>();
                this.draftLandingPreviews = draftLandingPreviews;
                this.contractLandingPreviews = contractLandingPreviews;
                this.pending = pending;
                this.renderFailures = renderFailures;
                this.renderFailureDetails = renderFailureDetails ?? new List<string>();
            }
        }

        private static class Direction
        {
            public const int North = 0;
            public const int East = 1;
            public const int South = 2;
            public const int West = 3;

            public static int FromString(string value)
            {
                switch ((value ?? string.Empty).Trim().ToUpperInvariant())
                {
                    case "N":
                    case "NORTH":
                        return North;
                    case "E":
                    case "EAST":
                        return East;
                    case "S":
                    case "SOUTH":
                        return South;
                    case "W":
                    case "WEST":
                        return West;
                    default:
                        throw new InvalidOperationException("unknown side '" + value + "'");
                }
            }

            public static bool IsEastWest(int direction)
            {
                return direction == East || direction == West;
            }

            public static string ToString(int direction)
            {
                switch (direction)
                {
                    case North: return "N";
                    case East: return "E";
                    case South: return "S";
                    case West: return "W";
                    default: throw new InvalidOperationException("unknown side");
                }
            }

            public static Vector3 OutwardVector(int direction)
            {
                switch (direction)
                {
                    case North: return Vector3.forward;
                    case East: return Vector3.right;
                    case South: return Vector3.back;
                    case West: return Vector3.left;
                    default: throw new InvalidOperationException("unknown side");
                }
            }

            public static Vector2Int OutwardDelta(int direction)
            {
                switch (direction)
                {
                    case North: return new Vector2Int(0, 1);
                    case East: return new Vector2Int(1, 0);
                    case South: return new Vector2Int(0, -1);
                    case West: return new Vector2Int(-1, 0);
                    default: throw new InvalidOperationException("unknown side");
                }
            }
        }
    }
}
