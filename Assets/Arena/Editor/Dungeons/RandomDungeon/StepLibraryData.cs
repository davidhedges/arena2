using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Plastic.Newtonsoft.Json.Linq;

namespace DungeonLab.Editor
{
    internal sealed class StepFormationModeTable
    {
        public const string Path = "Assets/Arena/Content/Settings/Dungeons/RandomDungeon/step_formation_modes.json";
        private readonly Dictionary<string, StepFormationModeRecord> formations;
        private readonly string stairConnectorDirectory;
        private readonly string primaryStair;
        private readonly string primaryStairPath;
        private readonly Dictionary<string, float> stairConnectorWeights;
        private readonly Dictionary<string, AuthoredStairConnectorContractRecord> stairConnectorContracts;

        private StepFormationModeTable(
            Dictionary<string, StepFormationModeRecord> formations,
            string stairConnectorDirectory,
            string primaryStair,
            Dictionary<string, float> stairConnectorWeights,
            Dictionary<string, AuthoredStairConnectorContractRecord> stairConnectorContracts)
        {
            this.formations = formations;
            this.stairConnectorDirectory = NormalizeAssetDirectory(stairConnectorDirectory);
            this.primaryStair = primaryStair;
            primaryStairPath = ResolvePrefabPath(this.stairConnectorDirectory, primaryStair);
            this.stairConnectorWeights = stairConnectorWeights ?? new Dictionary<string, float>(StringComparer.Ordinal);
            this.stairConnectorContracts = stairConnectorContracts ?? new Dictionary<string, AuthoredStairConnectorContractRecord>(StringComparer.Ordinal);
        }

        public static StepFormationModeTable Load()
        {
            if (!File.Exists(Path))
            {
                throw new FileNotFoundException(Path);
            }

            JObject root = JObject.Parse(File.ReadAllText(Path));
            if (!(root["formations"] is JObject formationObject))
            {
                throw new InvalidOperationException($"{Path} is missing a 'formations' object.");
            }

            var formations = new Dictionary<string, StepFormationModeRecord>(StringComparer.Ordinal);
            foreach (JProperty property in formationObject.Properties())
            {
                if (!(property.Value is JObject value))
                {
                    throw new InvalidOperationException($"{Path} formation '{property.Name}' must be an object.");
                }

                string mode = value.Value<string>("mode");
                if (string.IsNullOrWhiteSpace(mode))
                {
                    throw new InvalidOperationException($"{Path} formation '{property.Name}' is missing mode.");
                }

                formations[property.Name] = new StepFormationModeRecord(
                    property.Name,
                    mode,
                    value.Value<string>("openSide"),
                    value.Value<string>("backSide"),
                    value.Value<string>("kind"),
                    value.Value<string>("confidence"));
            }

            ParseStairConnectorConfig(
                root,
                out string stairConnectorDirectory,
                out string primaryStair,
                out Dictionary<string, float> stairConnectorWeights,
                out Dictionary<string, AuthoredStairConnectorContractRecord> stairConnectorContracts);
            return new StepFormationModeTable(formations, stairConnectorDirectory, primaryStair, stairConnectorWeights, stairConnectorContracts);
        }

        public bool TryGet(string name, out StepFormationModeRecord record)
        {
            return formations.TryGetValue(name, out record);
        }

        public string StairConnectorDirectory => stairConnectorDirectory;

        public string PrimaryStair => primaryStair;

        public string PrimaryStairPath => primaryStairPath;

        public bool TryGetStairConnectorWeight(string stairName, out float weight)
        {
            if (!string.IsNullOrWhiteSpace(stairName) &&
                stairConnectorWeights.TryGetValue(stairName, out weight) &&
                weight > 0f)
            {
                return true;
            }

            weight = 0f;
            return false;
        }

        public bool TryGetStairConnectorContract(string stairName, out AuthoredStairConnectorContractRecord contract)
        {
            if (!string.IsNullOrWhiteSpace(stairName) &&
                stairConnectorContracts.TryGetValue(stairName, out contract))
            {
                return true;
            }

            contract = default;
            return false;
        }

        public bool IsStairConnectorPath(string prefabPath)
        {
            if (string.IsNullOrWhiteSpace(prefabPath))
            {
                return false;
            }

            string normalizedPath = NormalizeAssetPath(prefabPath);
            return normalizedPath.StartsWith(stairConnectorDirectory + "/", StringComparison.Ordinal) &&
                normalizedPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        private static void ParseStairConnectorConfig(
            JObject root,
            out string directory,
            out string primaryStair,
            out Dictionary<string, float> weights,
            out Dictionary<string, AuthoredStairConnectorContractRecord> contracts)
        {
            JObject connectors = root["stairConnectors"] as JObject;
            if (connectors == null)
            {
                throw new InvalidOperationException($"{Path} is missing a 'stairConnectors' object.");
            }

            directory = connectors.Value<string>("directory");
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException($"{Path} stairConnectors is missing directory.");
            }

            primaryStair = connectors.Value<string>("primaryStair");
            if (string.IsNullOrWhiteSpace(primaryStair))
            {
                throw new InvalidOperationException($"{Path} stairConnectors is missing primaryStair.");
            }

            weights = new Dictionary<string, float>(StringComparer.Ordinal);
            if (connectors["weights"] is JObject weightObject)
            {
                foreach (JProperty property in weightObject.Properties())
                {
                    float weight = property.Value.Value<float>();
                    if (!string.IsNullOrWhiteSpace(property.Name) && weight > 0f)
                    {
                        weights[property.Name] = weight;
                    }
                }
            }

            contracts = ParseStairConnectorContracts(connectors);
        }

        private static Dictionary<string, AuthoredStairConnectorContractRecord> ParseStairConnectorContracts(JObject connectors)
        {
            var contracts = new Dictionary<string, AuthoredStairConnectorContractRecord>(StringComparer.Ordinal);
            if (!(connectors["contracts"] is JObject contractObject))
            {
                return contracts;
            }

            foreach (JProperty property in contractObject.Properties())
            {
                if (property.Name.StartsWith("_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!(property.Value is JObject value))
                {
                    throw new InvalidOperationException($"{Path} stairConnectors.contracts.{property.Name} must be an object.");
                }

                contracts[property.Name] = ParseStairConnectorContract(property.Name, value);
            }

            return contracts;
        }

        private static AuthoredStairConnectorContractRecord ParseStairConnectorContract(string name, JObject value)
        {
            if (!(value["ports"] is JArray ports) || ports.Count == 0)
            {
                throw new InvalidOperationException($"{Path} stairConnectors.contracts.{name} must define at least one port.");
            }

            if (!(value["walkableCellLevels"] is JArray levels) || levels.Count == 0)
            {
                throw new InvalidOperationException($"{Path} stairConnectors.contracts.{name} must define walkableCellLevels.");
            }

            var portRecords = new List<AuthoredStairConnectorPortRecord>();
            foreach (JToken token in ports)
            {
                if (!(token is JObject port))
                {
                    throw new InvalidOperationException($"{Path} stairConnectors.contracts.{name}.ports entries must be objects.");
                }

                string direction = port.Value<string>("direction");
                if (string.IsNullOrWhiteSpace(direction))
                {
                    throw new InvalidOperationException($"{Path} stairConnectors.contracts.{name} port missing direction.");
                }

                if (!(port["cellSpan"] is JArray spanArray) || spanArray.Count == 0)
                {
                    throw new InvalidOperationException($"{Path} stairConnectors.contracts.{name} port {direction} missing cellSpan.");
                }

                var span = new List<Int2Record>();
                foreach (JToken spanToken in spanArray)
                {
                    span.Add(ParseInt2(spanToken, $"{Path} stairConnectors.contracts.{name} port {direction} cellSpan"));
                }

                if (!(port["localEdgeCenter"] is JObject edgeObject))
                {
                    throw new InvalidOperationException($"{Path} stairConnectors.contracts.{name} port {direction} missing localEdgeCenter.");
                }

                portRecords.Add(new AuthoredStairConnectorPortRecord(
                    span.ToArray(),
                    direction,
                    port.Value<int>("level"),
                    ParseVector3(edgeObject)));
            }

            var levelRecords = new List<AuthoredWalkableCellLevelRecord>();
            foreach (JToken token in levels)
            {
                if (!(token is JObject level))
                {
                    throw new InvalidOperationException($"{Path} stairConnectors.contracts.{name}.walkableCellLevels entries must be objects.");
                }

                levelRecords.Add(new AuthoredWalkableCellLevelRecord(
                    ParseInt2(level["cell"], $"{Path} stairConnectors.contracts.{name} walkableCellLevels cell"),
                    level.Value<int>("level"),
                    level.Value<int?>("sampleCount") ?? 1));
            }

            return new AuthoredStairConnectorContractRecord(
                value.Value<string>("source") ?? "measured-usage",
                value.Value<string>("confidence") ?? "high",
                levelRecords.ToArray(),
                portRecords.ToArray());
        }

        private static Int2Record ParseInt2(JToken token, string context)
        {
            if (!(token is JObject value))
            {
                throw new InvalidOperationException($"{context} must be an object with x and z.");
            }

            return new Int2Record(value.Value<int>("x"), value.Value<int>("z"));
        }

        private static Vector3 ParseVector3(JObject value)
        {
            return new Vector3(
                value.Value<float>("x"),
                value.Value<float>("y"),
                value.Value<float>("z"));
        }

        private static string ResolvePrefabPath(string directory, string prefabNameOrPath)
        {
            string normalized = NormalizeAssetPath(prefabNameOrPath);
            if (normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                    ? normalized
                    : normalized + ".prefab";
            }

            string fileName = normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : normalized + ".prefab";
            return $"{directory}/{fileName}";
        }

        private static string NormalizeAssetDirectory(string path)
        {
            return NormalizeAssetPath(path).TrimEnd('/');
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim();
        }

        public static void ApplyToRecords(
            IEnumerable<StepLibraryRecord> records,
            StepFormationModeTable table,
            bool requireAllStepFormations)
        {
            var missing = new List<string>();
            foreach (StepLibraryRecord record in records)
            {
                if (record.folder != "StepFormations")
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(record.geometryPlacementMode) ||
                    record.geometryPlacementMode == "empty")
                {
                    record.geometryPlacementMode = record.placementMode;
                    record.geometryPlacementModeConfidence = record.placementModeConfidence;
                }

                if (!table.TryGet(record.name, out StepFormationModeRecord authored))
                {
                    missing.Add(record.name);
                    record.placementModeSource = "missing-authored";
                    continue;
                }

                record.placementMode = authored.mode;
                record.placementModeConfidence = string.IsNullOrWhiteSpace(authored.confidence)
                    ? "high"
                    : authored.confidence;
                record.placementModeSource = "measured-usage";
                record.authoredOpenSide = authored.openSide ?? string.Empty;
                record.authoredBackSide = authored.backSide ?? string.Empty;
                record.authoredKind = authored.kind ?? string.Empty;
            }

            if (requireAllStepFormations && missing.Count > 0)
            {
                missing.Sort(StringComparer.Ordinal);
                throw new InvalidOperationException(
                    $"{Path} is missing authored placement modes for active StepFormations: {string.Join(", ", missing)}");
            }
        }
    }

    internal readonly struct StepFormationModeRecord
    {
        public readonly string name;
        public readonly string mode;
        public readonly string openSide;
        public readonly string backSide;
        public readonly string kind;
        public readonly string confidence;

        public StepFormationModeRecord(
            string name,
            string mode,
            string openSide,
            string backSide,
            string kind,
            string confidence)
        {
            this.name = name;
            this.mode = mode;
            this.openSide = openSide;
            this.backSide = backSide;
            this.kind = kind;
            this.confidence = confidence;
        }
    }

    internal readonly struct AuthoredStairConnectorContractRecord
    {
        public readonly string source;
        public readonly string confidence;
        public readonly AuthoredWalkableCellLevelRecord[] walkableCellLevels;
        public readonly AuthoredStairConnectorPortRecord[] ports;

        public AuthoredStairConnectorContractRecord(
            string source,
            string confidence,
            AuthoredWalkableCellLevelRecord[] walkableCellLevels,
            AuthoredStairConnectorPortRecord[] ports)
        {
            this.source = source;
            this.confidence = confidence;
            this.walkableCellLevels = walkableCellLevels ?? Array.Empty<AuthoredWalkableCellLevelRecord>();
            this.ports = ports ?? Array.Empty<AuthoredStairConnectorPortRecord>();
        }

        public WalkableCellLevelRecord[] BuildWalkableCellLevels()
        {
            var records = new WalkableCellLevelRecord[walkableCellLevels.Length];
            for (int i = 0; i < walkableCellLevels.Length; i++)
            {
                AuthoredWalkableCellLevelRecord level = walkableCellLevels[i];
                records[i] = new WalkableCellLevelRecord(level.cell.x, level.cell.z, level.level, level.sampleCount);
            }

            return records;
        }

        public List<SetPiecePortRecord> BuildPorts()
        {
            var records = new List<SetPiecePortRecord>(ports.Length);
            foreach (AuthoredStairConnectorPortRecord port in ports)
            {
                var span = new Int2Record[port.cellSpan.Length];
                Array.Copy(port.cellSpan, span, port.cellSpan.Length);
                records.Add(new SetPiecePortRecord(span, port.direction, port.level, port.localEdgeCenter));
            }

            return records;
        }
    }

    internal readonly struct AuthoredWalkableCellLevelRecord
    {
        public readonly Int2Record cell;
        public readonly int level;
        public readonly int sampleCount;

        public AuthoredWalkableCellLevelRecord(Int2Record cell, int level, int sampleCount)
        {
            this.cell = cell;
            this.level = level;
            this.sampleCount = sampleCount;
        }
    }

    internal readonly struct AuthoredStairConnectorPortRecord
    {
        public readonly Int2Record[] cellSpan;
        public readonly string direction;
        public readonly int level;
        public readonly Vector3 localEdgeCenter;

        public AuthoredStairConnectorPortRecord(
            Int2Record[] cellSpan,
            string direction,
            int level,
            Vector3 localEdgeCenter)
        {
            this.cellSpan = cellSpan ?? Array.Empty<Int2Record>();
            this.direction = direction;
            this.level = level;
            this.localEdgeCenter = localEdgeCenter;
        }
    }

    [Serializable]
    internal sealed class StepLibraryIndex
    {
        public float cellSize;
        public StepLibraryRecord[] records;
    }

    [Serializable]
    internal sealed class StepLibraryRecord
    {
        public string name;
        public string path;
        public string folder;
        public string status;
        public PlanSizeRecord footprintUnits;
        public PlanSizeRecord footprintCells;
        public float heightUnits;
        public float heightCells;
        public Vector3Record originOffset;
        public Vector3Record boundsMin;
        public Vector3Record boundsMax;
        public Vector3Record boundsSize;
        public float connectionPlaneY;
        public string connectionPlane;
        public int perimeterConnectionMask;
        public int perimeterUnsupportedMask;
        public string placementMode;
        public string closure;
        public int openSideMask;
        public int blankWallSideMask;
        public PerimeterSideRecord[] perimeterSides;
        public PerimeterSideHeightRecord[] sideHeights;
        public string geometryPlacementMode;
        public string geometryPlacementModeConfidence;
        public string placementModeSource;
        public string authoredOpenSide;
        public string authoredBackSide;
        public string authoredKind;
        public string closureSource;
        public string closureConfidence;
        public string placementModeConfidence;
        public Int2Record coverageCellGrid;
        public OccupiedCellRecord[] coverageCells;
        public int coverageCellCount;
        public string coverageMask;
        public string footprintCoverage;
        public Int2Record occupiedCellGrid;
        public OccupiedCellRecord[] occupiedCells;
        public int occupiedCellCount;
        public string occupiedCellMask;
        public Vector3Record walkableBoundsMin;
        public Vector3Record walkableBoundsMax;
        public Vector3Record walkableBoundsSize;
        public WalkableCellLevelRecord[] walkableCellLevels;
        public SetPiecePortRecord[] ports;
        public int portCount;
        public string portSource;
        public string portConfidence;
        public string stairContractBasis;
        public string stairClimbDirectionBasis;
        public SetPieceConnectionPointRecord[] connectionPoints;
        public int connectionPointCount;
        public string connectionPointSource;
        public string connectionPointConfidence;
        public int pieceCount;
        public bool hasRailing;
        public bool hasStair;
    }

    [Serializable]
    internal sealed class PerimeterSideRecord
    {
        public string side;
        public string classification;
        public float wallCoverage;
        public float wallHeight;

        public PerimeterSideRecord()
        {
        }

        public PerimeterSideRecord(string side, string classification, float wallCoverage, float wallHeight)
        {
            this.side = side;
            this.classification = classification;
            this.wallCoverage = wallCoverage;
            this.wallHeight = wallHeight;
        }
    }

    [Serializable]
    internal sealed class PerimeterSideHeightRecord
    {
        public string side;
        public float maxVerticalFaceHeight;

        public PerimeterSideHeightRecord()
        {
        }

        public PerimeterSideHeightRecord(string side, float maxVerticalFaceHeight)
        {
            this.side = side;
            this.maxVerticalFaceHeight = maxVerticalFaceHeight;
        }
    }

    [Serializable]
    internal sealed class WalkableCellLevelRecord
    {
        public Int2Record localCell;
        public int level;
        public int sampleCount;

        public WalkableCellLevelRecord()
        {
        }

        public WalkableCellLevelRecord(int x, int z, int level, int sampleCount)
        {
            localCell = new Int2Record(x, z);
            this.level = level;
            this.sampleCount = sampleCount;
        }
    }

    [Serializable]
    internal sealed class SetPiecePortRecord
    {
        public Int2Record[] cellSpan;
        public string direction;
        public int level;
        public bool hasLocalEdgeCenter;
        public Vector3Record localEdgeCenter;

        public SetPiecePortRecord()
        {
        }

        public SetPiecePortRecord(Int2Record[] cellSpan, string direction, int level)
        {
            this.cellSpan = cellSpan;
            this.direction = direction;
            this.level = level;
            hasLocalEdgeCenter = false;
            localEdgeCenter = new Vector3Record(Vector3.zero);
        }

        public SetPiecePortRecord(Int2Record[] cellSpan, string direction, int level, Vector3 localEdgeCenter)
        {
            this.cellSpan = cellSpan;
            this.direction = direction;
            this.level = level;
            hasLocalEdgeCenter = true;
            this.localEdgeCenter = new Vector3Record(localEdgeCenter);
        }
    }

    [Serializable]
    internal sealed class SetPieceConnectionPointRecord
    {
        public Int2Record localCell;
        public string direction;
        public int level;
        public string role;

        public SetPieceConnectionPointRecord()
        {
        }

        public SetPieceConnectionPointRecord(Int2Record localCell, string direction, int level, string role)
        {
            this.localCell = localCell;
            this.direction = direction;
            this.level = level;
            this.role = role;
        }
    }

    [Serializable]
    internal struct Int2Record
    {
        public int x;
        public int z;

        public Int2Record(int x, int z)
        {
            this.x = x;
            this.z = z;
        }
    }

    [Serializable]
    internal struct OccupiedCellRecord
    {
        public int x;
        public int z;

        public OccupiedCellRecord(int x, int z)
        {
            this.x = x;
            this.z = z;
        }
    }

    [Serializable]
    internal struct PlanSizeRecord
    {
        public float x;
        public float z;

        public PlanSizeRecord(float x, float z)
        {
            this.x = x;
            this.z = z;
        }
    }

    [Serializable]
    internal struct Vector3Record
    {
        public float x;
        public float y;
        public float z;

        public Vector3Record(Vector3 value)
        {
            x = value.x;
            y = value.y;
            z = value.z;
        }
    }

}
