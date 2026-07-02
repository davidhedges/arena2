#nullable enable

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Entity;
using Arena.Input;
using Arena.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Debugging
{
    /// <summary>
    /// Draws the same line-of-sight probe rays used by the server:
    /// caster 85% capsule height to target upper-torso center/side probe points.
    /// Toggle with left bracket ([).
    /// </summary>
    internal sealed class LineOfSightDebugGuide : MonoBehaviour
    {
        private const KeyCode ToggleKey = KeyCode.LeftBracket;
        private const float ProbeRadius = 0.05f;
        private const float CollisionEpsilon = 0.0001f;
        private const float OpenWorldRaycastStep = 0.25f;
        private const int OpenWorldRaycastRefineIters = 7;
        private const float TargetFacingArcRadians = Mathf.PI;
        private const float FacingDotEpsilon = 0.0001f;
        private const int ProbeCount = 6;
        private const float TargetSideProbeFraction = 0.75f;
        private static readonly float[] TargetHeightFractions = { 0.75f, 0.60f };
        private static readonly Color ClearColor = new(0.15f, 1.0f, 0.35f, 0.95f);
        private static readonly Color BlockedColor = new(1.0f, 0.18f, 0.12f, 0.98f);
        private static readonly Color FacingClearColor = new(0.2f, 0.75f, 1.0f, 0.95f);
        private static readonly Color FacingBlockedColor = new(1.0f, 0.55f, 0.05f, 0.95f);
        private static readonly Color MarkerColor = new(1.0f, 0.86f, 0.15f, 1.0f);

        private bool _visible;
        private GUIStyle? _style;
        private GUIStyle? _headerStyle;
        private Material? _lineMaterial;
        private Transform? _visualRoot;
        private ProbeVisual[]? _probes;
        private LineRenderer? _facingLine;
        private GameObject? _originMarker;
        private GameObject? _hitMarker;
        private string _status = "Select a target to draw LOS probes.";
        private string _probeStatus = string.Empty;
        private ServerLosCollisionData? _serverCollisionData;
        private string _serverCollisionScene = string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("LineOfSightDebugGuide");
            DontDestroyOnLoad(go);
            go.AddComponent<LineOfSightDebugGuide>();
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                _visible = false;
                SetVisualsActive(false);
                return;
            }

            if (UnityEngine.Input.GetKeyDown(ToggleKey))
            {
                _visible = !_visible;
                SetVisualsActive(_visible);
            }

            if (!_visible)
                return;

            EnsureVisuals();
            UpdateGuide();
        }

        private void OnGUI()
        {
            if (!_visible || !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            _style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = Color.white },
                richText = true,
            };
            _headerStyle ??= new GUIStyle(_style)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
            };

            GUILayout.BeginArea(new Rect(10f, 225f, 820f, 230f), GUI.skin.window);
            GUILayout.Label("LOS Guide ([)", _headerStyle);
            GUILayout.Label("Server probes: caster 85% height -> target upper torso, center/side target offsets.", _style);
            GUILayout.Label("Targeted spells also require the target in the server-facing front 180 degrees.", _style);
            GUILayout.Label("Line color uses bundled server collision data only: heightfield, exported boxes, query meshes.", _style);
            GUILayout.Label(_status, _style);
            GUILayout.Label(_probeStatus, _style);
            GUILayout.EndArea();
        }

        private void OnDisable()
        {
            SetVisualsActive(false);
        }

        private void EnsureVisuals()
        {
            if (_visualRoot == null)
            {
                var root = new GameObject("LineOfSightDebugGuide_Visuals");
                DontDestroyOnLoad(root);
                _visualRoot = root.transform;
            }

            _lineMaterial ??= CreateLineMaterial();

            if (_probes == null)
            {
                _probes = new ProbeVisual[ProbeCount];
                for (int i = 0; i < ProbeCount; i++)
                    _probes[i] = ProbeVisual.Create(_visualRoot, _lineMaterial, i);
            }

            _facingLine ??= ProbeVisual.CreateLine(_visualRoot, _lineMaterial, "LineOfSightDebugGuide_Facing");
            _originMarker ??= CreateMarker("LineOfSightDebugGuide_Origin", _visualRoot, MarkerColor, 0.13f);
            _hitMarker ??= CreateMarker("LineOfSightDebugGuide_Hit", _visualRoot, BlockedColor, 0.16f);
            SetVisualsActive(true);
        }

        private void UpdateGuide()
        {
            PlayerEntity? local = EntityRegistry.Instance?.LocalPlayerEntity;
            ICombatTargetEntity? target = TargetSelector.Instance?.SelectedTarget;

            if (local == null || target == null || local.IsDestroyed || target.IsDestroyed)
            {
                HideProbeGeometry();
                _status = local == null ? "No local player." : "No selected target.";
                _probeStatus = string.Empty;
                return;
            }

            Vector3 casterBase = local.SimState.GetServerPosition();
            Vector3 targetBase = target.GetRenderPosition();
            float casterHeight = Mathf.Max(local.SimState.HitHeight, 0.5f);
            float targetHeight = Mathf.Max(target.HitHeight, 0.5f);
            float targetRadius = Mathf.Max(target.HitRadius, 0f);
            Vector3 origin = casterBase + Vector3.up * (casterHeight * 0.85f);
            LocalPlayerStateProvider? stateProvider = local.GetLocalStateProvider();
            float facingYaw = stateProvider?.HasPredictedState == true
                ? stateProvider.MovementFacingYaw
                : local.SimState.GetServerYawRadians();
            bool targetInFacingArc = IsTargetWithinFacingArc(casterBase, targetBase, facingYaw);
            ServerLosCollisionData? serverCollisionData = GetServerCollisionData();

            if (_originMarker != null)
            {
                _originMarker.transform.position = origin;
                _originMarker.SetActive(true);
            }

            float closestHitDistance = float.PositiveInfinity;
            Vector3 closestHitPoint = default;
            string closestBlocker = string.Empty;
            bool anyServerClear = false;
            var probeDetails = new List<string>(ProbeCount);
            DrawFacingLine(origin, facingYaw, targetInFacingArc);

            int probeIndex = 0;
            foreach ((Vector3 end, string label) in BuildTargetProbePoints(casterBase, targetBase, targetHeight, targetRadius))
            {
                LocalProbeHit? hit = serverCollisionData?.FindFirstHit(origin, end, ProbeRadius);
                if (hit.HasValue)
                {
                    LocalProbeHit localHit = hit.Value;
                    if (localHit.Distance < closestHitDistance)
                    {
                        closestHitDistance = localHit.Distance;
                        closestHitPoint = localHit.Point;
                        closestBlocker = localHit.Blocker;
                    }

                    _probes?[probeIndex].Set(origin, end, hit, BlockedColor);
                    Debug.DrawLine(origin, end, BlockedColor);
                    probeDetails.Add($"{label}: BLOCK {localHit.Distance:F2}m @ ({localHit.Point.x:F2},{localHit.Point.y:F2},{localHit.Point.z:F2}) {ShortenBlocker(localHit.Blocker)}");
                }
                else
                {
                    anyServerClear = true;
                    _probes?[probeIndex].Set(origin, end, hit, ClearColor);
                    Debug.DrawLine(origin, end, ClearColor);
                    probeDetails.Add($"{label}: CLEAR");
                }

                probeIndex++;
            }

            if (_probes != null)
            {
                for (int i = probeIndex; i < _probes.Length; i++)
                    _probes[i].SetActive(false);
            }

            if (_hitMarker != null)
            {
                bool hasHit = closestHitDistance < float.PositiveInfinity;
                _hitMarker.SetActive(hasHit);
                if (hasHit)
                    _hitMarker.transform.position = closestHitPoint;
            }

            float distance = Vector3.Distance(origin, targetBase);
            string serverResult = serverCollisionData == null
                ? "server collision data unavailable"
                : anyServerClear
                    ? "SERVER LOS: CLEAR"
                    : $"SERVER LOS: BLOCKED by {ShortenBlocker(closestBlocker)}";
            string facingResult = targetInFacingArc ? "facing OK" : "target outside front 180 arc";
            string movementResult = HasCurrentMovementInput(local) ? "movement input active" : "no movement input";
            string targetName = string.IsNullOrWhiteSpace(target.DisplayName)
                ? target.TargetIdentity.ToString()
                : target.DisplayName;
            _status = $"Target: {targetName}  distance={distance:F2}m  {facingResult}  {movementResult}  {serverResult}";
            _probeStatus = string.Join("\n", probeDetails);
        }

        private static IEnumerable<(Vector3 End, string Label)> BuildTargetProbePoints(
            Vector3 casterBase,
            Vector3 targetBase,
            float targetHeight,
            float targetRadius)
        {
            Vector3 toTarget = targetBase - casterBase;
            toTarget.y = 0f;
            float sideOffset = Mathf.Max(targetRadius, 0f) * TargetSideProbeFraction;
            Vector3 side = Vector3.zero;
            if (toTarget.sqrMagnitude > CollisionEpsilon && sideOffset > CollisionEpsilon)
            {
                Vector3 dir = toTarget.normalized;
                side = new Vector3(-dir.z, 0f, dir.x) * sideOffset;
            }

            for (int heightIndex = 0; heightIndex < TargetHeightFractions.Length; heightIndex++)
            {
                float heightFraction = TargetHeightFractions[heightIndex];
                Vector3 baseEnd = targetBase + Vector3.up * (targetHeight * heightFraction);
                yield return (baseEnd, $"{heightFraction:P0} center");
                if (side != Vector3.zero)
                {
                    yield return (baseEnd + side, $"{heightFraction:P0} side A");
                    yield return (baseEnd - side, $"{heightFraction:P0} side B");
                }
            }
        }

        private static string ShortenBlocker(string blocker)
        {
            if (string.IsNullOrWhiteSpace(blocker))
                return "unknown";

            const int maxLength = 72;
            if (blocker.Length <= maxLength)
                return blocker;

            int keep = Math.Min(maxLength - 3, blocker.Length);
            return "..." + blocker.Substring(blocker.Length - keep);
        }

        private ServerLosCollisionData? GetServerCollisionData()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (_serverCollisionData != null && string.Equals(_serverCollisionScene, sceneName, StringComparison.Ordinal))
                return _serverCollisionData;

            _serverCollisionScene = sceneName;
            OpenWorldSceneProfile profile = OpenWorldSceneProfile.ForSceneName(sceneName);
            _serverCollisionData = ServerLosCollisionData.Load(profile);
            return _serverCollisionData;
        }

        private void DrawFacingLine(Vector3 origin, float facingYaw, bool targetInFacingArc)
        {
            if (_facingLine == null)
                return;

            Vector3 forward = new(Mathf.Sin(facingYaw), 0f, Mathf.Cos(facingYaw));
            ProbeVisual.SetLine(
                _facingLine,
                origin,
                origin + forward.normalized * 3.0f,
                targetInFacingArc ? FacingClearColor : FacingBlockedColor);
            _facingLine.gameObject.SetActive(true);
        }

        private static bool IsTargetWithinFacingArc(Vector3 casterBase, Vector3 targetBase, float facingYaw)
        {
            Vector3 toTarget = targetBase - casterBase;
            toTarget.y = 0f;
            float lenSq = toTarget.sqrMagnitude;
            if (lenSq <= 0.0001f)
                return true;

            Vector3 dir = toTarget / Mathf.Sqrt(lenSq);
            Vector3 forward = new(Mathf.Sin(facingYaw), 0f, Mathf.Cos(facingYaw));
            float dot = Vector3.Dot(forward, dir);
            float minDot = Mathf.Cos(TargetFacingArcRadians * 0.5f);
            return dot + FacingDotEpsilon >= minDot;
        }

        private static bool HasCurrentMovementInput(PlayerEntity local)
        {
            LocalPlayerInputSource? input = local.GetLocalInputSource();
            if (input == null)
                return false;

            Vector2 move = input.Move;
            return input.JumpPressed || move.sqrMagnitude > 0.0001f;
        }

        private static Material CreateLineMaterial()
        {
            Shader? shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            var material = new Material(shader)
            {
                name = "LineOfSightDebugGuideMaterial",
                hideFlags = HideFlags.HideAndDontSave,
            };
            return material;
        }

        private static GameObject CreateMarker(string name, Transform parent, Color color, float scale)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = name;
            marker.transform.SetParent(parent, false);
            marker.transform.localScale = Vector3.one * scale;
            if (marker.TryGetComponent(out Collider collider))
                Destroy(collider);
            if (marker.TryGetComponent(out Renderer renderer))
            {
                Material material = new(CreateLineMaterial())
                {
                    color = color,
                    name = $"{name}_Material",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                renderer.sharedMaterial = material;
            }
            marker.SetActive(false);
            return marker;
        }

        private void HideProbeGeometry()
        {
            if (_probes != null)
            {
                foreach (ProbeVisual probe in _probes)
                    probe.SetActive(false);
            }

            _facingLine?.gameObject.SetActive(false);
            _originMarker?.SetActive(false);
            _hitMarker?.SetActive(false);
        }

        private void SetVisualsActive(bool active)
        {
            if (_visualRoot != null)
                _visualRoot.gameObject.SetActive(active);
        }

        private readonly struct LocalProbeHit
        {
            public LocalProbeHit(Vector3 point, float distance, string blocker)
            {
                Point = point;
                Distance = distance;
                Blocker = blocker;
            }

            public Vector3 Point { get; }
            public float Distance { get; }
            public string Blocker { get; }
        }

        private sealed class ServerLosCollisionData
        {
            private readonly ServerHeightfield? _heightfield;
            private readonly ServerCollisionBox[] _boxes;
            private readonly ServerQueryMeshInstance[] _queryMeshInstances;

            private ServerLosCollisionData(
                ServerHeightfield? heightfield,
                ServerCollisionBox[] boxes,
                ServerQueryMeshInstance[] queryMeshInstances)
            {
                _heightfield = heightfield;
                _boxes = boxes;
                _queryMeshInstances = queryMeshInstances;
            }

            public static ServerLosCollisionData? Load(OpenWorldSceneProfile profile)
            {
                string heightfieldPath = $"SharedData/Worlds/{profile.DataKey}.heightfield.shared";
                TextAsset? heightfieldAsset = Resources.Load<TextAsset>(heightfieldPath);
                ServerHeightfield? heightfield = null;
                if (heightfieldAsset != null)
                {
                    HeightfieldFile? file = JsonUtility.FromJson<HeightfieldFile>(heightfieldAsset.text);
                    if (file != null &&
                        file.origin != null &&
                        file.origin.Length == 3 &&
                        file.size != null &&
                        file.size.Length == 3)
                    {
                        var parsed = new ServerHeightfield(
                            file.origin[0],
                            file.origin[1],
                            file.origin[2],
                            file.size[0],
                            file.size[1],
                            file.size[2],
                            file.resolution_x,
                            file.resolution_z,
                            file.heights ?? Array.Empty<float>());
                        if (parsed.IsValid)
                            heightfield = parsed;
                    }
                }

                List<ServerCollisionBox> boxes = new();
                LoadBoxes($"SharedData/Worlds/{profile.DataKey}.collision.shared", boxes);
                GameplayCollisionLayoutFile? queryLayout =
                    LoadLayout($"SharedData/Worlds/{profile.DataKey}.query_collision.shared");
                if (queryLayout != null)
                {
                    AddBoxes(queryLayout, boxes);
                }

                ServerQueryMeshInstance[] queryMeshInstances = queryLayout != null
                    ? BuildQueryMeshInstances(queryLayout)
                    : Array.Empty<ServerQueryMeshInstance>();

                if (heightfield == null && boxes.Count == 0 && queryMeshInstances.Length == 0)
                    return null;

                return new ServerLosCollisionData(heightfield, boxes.ToArray(), queryMeshInstances);
            }

            public LocalProbeHit? FindFirstHit(Vector3 origin, Vector3 end, float radius)
            {
                Vector3 delta = end - origin;
                float distance = delta.magnitude;
                if (distance <= CollisionEpsilon)
                    return null;

                Vector3 direction = delta / distance;
                LocalProbeHit? terrainHit = RaycastHeightfield(origin, direction, distance, radius);
                LocalProbeHit? best = terrainHit;

                for (int i = 0; i < _boxes.Length; i++)
                {
                    if (_boxes[i].TryRaycast(origin, direction, distance, radius, out float t) &&
                        IsCloser(t, best))
                    {
                        best = new LocalProbeHit(origin + direction * t, t, _boxes[i].Name);
                    }
                }

                for (int i = 0; i < _queryMeshInstances.Length; i++)
                {
                    if (_queryMeshInstances[i].TryRaycast(origin, direction, distance, radius, best, out float t) &&
                        IsCloser(t, best))
                    {
                        best = new LocalProbeHit(origin + direction * t, t, _queryMeshInstances[i].Name);
                    }
                }

                return best;
            }

            private static bool IsCloser(float t, LocalProbeHit? best)
            {
                return t >= 0f && (!best.HasValue || t < best.Value.Distance);
            }

            private static GameplayCollisionLayoutFile? LoadLayout(string resourcePath)
            {
                TextAsset? asset = Resources.Load<TextAsset>(resourcePath);
                return asset == null ? null : JsonUtility.FromJson<GameplayCollisionLayoutFile>(asset.text);
            }

            private static void LoadBoxes(string resourcePath, List<ServerCollisionBox> boxes)
            {
                GameplayCollisionLayoutFile? layout = LoadLayout(resourcePath);
                if (layout != null)
                    AddBoxes(layout, boxes);
            }

            private static void AddBoxes(GameplayCollisionLayoutFile layout, List<ServerCollisionBox> boxes)
            {
                GameplayCollisionBoxFile[] files = layout.boxes ?? Array.Empty<GameplayCollisionBoxFile>();
                for (int i = 0; i < files.Length; i++)
                {
                    if (ServerCollisionBox.TryCreate(files[i], out ServerCollisionBox box))
                        boxes.Add(box);
                }
            }

            private static ServerQueryMeshInstance[] BuildQueryMeshInstances(GameplayCollisionLayoutFile layout)
            {
                GameplayQueryMeshGeometryFile[] geometryFiles =
                    layout.mesh_geometries ?? Array.Empty<GameplayQueryMeshGeometryFile>();
                GameplayQueryMeshInstanceFile[] instanceFiles =
                    layout.mesh_instances ?? Array.Empty<GameplayQueryMeshInstanceFile>();
                if (geometryFiles.Length == 0 || instanceFiles.Length == 0)
                    return Array.Empty<ServerQueryMeshInstance>();

                var geometries = new Dictionary<string, ServerQueryMeshGeometry>(geometryFiles.Length);
                for (int i = 0; i < geometryFiles.Length; i++)
                {
                    if (ServerQueryMeshGeometry.TryCreate(geometryFiles[i], out ServerQueryMeshGeometry geometry))
                        geometries[geometry.Id] = geometry;
                }

                var instances = new List<ServerQueryMeshInstance>(instanceFiles.Length);
                for (int i = 0; i < instanceFiles.Length; i++)
                {
                    GameplayQueryMeshInstanceFile file = instanceFiles[i];
                    if (string.IsNullOrEmpty(file.geometry_id) ||
                        !geometries.TryGetValue(file.geometry_id, out ServerQueryMeshGeometry geometry))
                    {
                        continue;
                    }

                    if (ServerQueryMeshInstance.TryCreate(file, geometry, out ServerQueryMeshInstance instance))
                        instances.Add(instance);
                }

                return instances.ToArray();
            }

            private LocalProbeHit? RaycastHeightfield(
                Vector3 origin,
                Vector3 direction,
                float maxDistance,
                float radius)
            {
                if (_heightfield == null)
                    return null;

                bool IntersectsAt(float t)
                {
                    Vector3 point = origin + direction * t;
                    return point.y <= _heightfield.Value.SampleHeight(point.x, point.z) + radius;
                }

                if (IntersectsAt(0f))
                    return new LocalProbeHit(origin, 0f, "server heightfield terrain");

                float previousT = 0f;
                float tCursor = Mathf.Max(OpenWorldRaycastStep, CollisionEpsilon);
                while (tCursor <= maxDistance + CollisionEpsilon)
                {
                    float clampedT = Mathf.Min(tCursor, maxDistance);
                    if (IntersectsAt(clampedT))
                    {
                        float lo = previousT;
                        float hi = clampedT;
                        for (int i = 0; i < OpenWorldRaycastRefineIters; i++)
                        {
                            float mid = (lo + hi) * 0.5f;
                            if (IntersectsAt(mid))
                                hi = mid;
                            else
                                lo = mid;
                        }

                        return new LocalProbeHit(origin + direction * hi, hi, "server heightfield terrain");
                    }

                    previousT = clampedT;
                    if (clampedT >= maxDistance)
                        break;
                    tCursor += OpenWorldRaycastStep;
                }

                return null;
            }
        }

#pragma warning disable CS0649 // JsonUtility populates these fields by name.
        [Serializable]
        private sealed class HeightfieldFile
        {
            public float[] origin = Array.Empty<float>();
            public float[] size = Array.Empty<float>();
            public int resolution_x;
            public int resolution_z;
            public float[] heights = Array.Empty<float>();
        }

        [Serializable]
        private sealed class GameplayCollisionLayoutFile
        {
            public GameplayCollisionBoxFile[] boxes = Array.Empty<GameplayCollisionBoxFile>();
            public GameplayQueryMeshGeometryFile[] mesh_geometries = Array.Empty<GameplayQueryMeshGeometryFile>();
            public GameplayQueryMeshInstanceFile[] mesh_instances = Array.Empty<GameplayQueryMeshInstanceFile>();
        }

        [Serializable]
        private sealed class GameplayCollisionBoxFile
        {
            public string name = string.Empty;
            public string shape = string.Empty;
            public float[] center = Array.Empty<float>();
            public float[] size = Array.Empty<float>();
            public float[] rotation = Array.Empty<float>();
            public float rotation_y_deg;
        }

        [Serializable]
        private sealed class GameplayQueryMeshGeometryFile
        {
            public string id = string.Empty;
            public string source = string.Empty;
            public int vertex_count;
            public int triangle_count;
            public float[] vertices = Array.Empty<float>();
            public int[] indices = Array.Empty<int>();
        }

        [Serializable]
        private sealed class GameplayQueryMeshInstanceFile
        {
            public string name = string.Empty;
            public string geometry_id = string.Empty;
            public float[] transform = Array.Empty<float>();
        }
#pragma warning restore CS0649

        private readonly struct ServerHeightfield
        {
            public ServerHeightfield(
                float originX,
                float originY,
                float originZ,
                float sizeX,
                float sizeY,
                float sizeZ,
                int resolutionX,
                int resolutionZ,
                float[] heights)
            {
                OriginX = originX;
                OriginY = originY;
                OriginZ = originZ;
                SizeX = sizeX;
                SizeY = sizeY;
                SizeZ = sizeZ;
                ResolutionX = resolutionX;
                ResolutionZ = resolutionZ;
                Heights = heights;
            }

            private float OriginX { get; }
            private float OriginY { get; }
            private float OriginZ { get; }
            private float SizeX { get; }
            private float SizeY { get; }
            private float SizeZ { get; }
            private int ResolutionX { get; }
            private int ResolutionZ { get; }
            private float[] Heights { get; }

            public bool IsValid =>
                ResolutionX >= 2 &&
                ResolutionZ >= 2 &&
                Heights.Length == ResolutionX * ResolutionZ;

            public float SampleHeight(float x, float z)
            {
                if (!IsValid)
                    return OriginY;

                float normalizedX = Mathf.Clamp01((x - OriginX) / Mathf.Max(SizeX, CollisionEpsilon));
                float normalizedZ = Mathf.Clamp01((z - OriginZ) / Mathf.Max(SizeZ, CollisionEpsilon));
                float sampleX = normalizedX * (ResolutionX - 1);
                float sampleZ = normalizedZ * (ResolutionZ - 1);

                int x0 = Mathf.Clamp(Mathf.FloorToInt(sampleX), 0, ResolutionX - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt(sampleZ), 0, ResolutionZ - 1);
                int x1 = Mathf.Min(x0 + 1, ResolutionX - 1);
                int z1 = Mathf.Min(z0 + 1, ResolutionZ - 1);

                float tx = sampleX - x0;
                float tz = sampleZ - z0;
                float h00 = Heights[z0 * ResolutionX + x0];
                float h10 = Heights[z0 * ResolutionX + x1];
                float h01 = Heights[z1 * ResolutionX + x0];
                float h11 = Heights[z1 * ResolutionX + x1];

                float hx0 = Mathf.Lerp(h00, h10, tx);
                float hx1 = Mathf.Lerp(h01, h11, tx);
                return Mathf.Lerp(hx0, hx1, tz);
            }
        }

        private readonly struct ServerCollisionBox
        {
            private enum BoxKind
            {
                Aabb,
                ObbY,
                ObbXyz,
            }

            private readonly BoxKind _kind;
            private readonly Vector3 _center;
            private readonly Vector3 _half;
            private readonly float _sinY;
            private readonly float _cosY;
            private readonly Vector3 _axisX;
            private readonly Vector3 _axisY;
            private readonly Vector3 _axisZ;

            private ServerCollisionBox(
                string name,
                BoxKind kind,
                Vector3 center,
                Vector3 half,
                float sinY,
                float cosY,
                Vector3 axisX,
                Vector3 axisY,
                Vector3 axisZ)
            {
                Name = string.IsNullOrWhiteSpace(name) ? "server collision box" : name;
                _kind = kind;
                _center = center;
                _half = half;
                _sinY = sinY;
                _cosY = cosY;
                _axisX = axisX;
                _axisY = axisY;
                _axisZ = axisZ;
            }

            public string Name { get; }

            public static bool TryCreate(GameplayCollisionBoxFile file, out ServerCollisionBox box)
            {
                box = default;
                if (file.center == null || file.center.Length < 3 || file.size == null || file.size.Length < 3)
                    return false;

                Vector3 center = new(file.center[0], file.center[1], file.center[2]);
                Vector3 half = new(
                    Mathf.Abs(file.size[0]) * 0.5f,
                    Mathf.Abs(file.size[1]) * 0.5f,
                    Mathf.Abs(file.size[2]) * 0.5f);
                string shape = file.shape ?? string.Empty;
                if (string.Equals(shape, "aabb", StringComparison.Ordinal))
                {
                    box = new ServerCollisionBox(
                        file.name,
                        BoxKind.Aabb,
                        center,
                        half,
                        0f,
                        1f,
                        Vector3.right,
                        Vector3.up,
                        Vector3.forward);
                    return true;
                }

                if (string.Equals(shape, "obb_xyz", StringComparison.Ordinal))
                {
                    if (!QuaternionToAxes(file.rotation, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ))
                        return false;
                    box = new ServerCollisionBox(file.name, BoxKind.ObbXyz, center, half, 0f, 1f, axisX, axisY, axisZ);
                    return true;
                }

                float yaw = file.rotation_y_deg * Mathf.Deg2Rad;
                box = new ServerCollisionBox(
                    file.name,
                    BoxKind.ObbY,
                    center,
                    half,
                    Mathf.Sin(yaw),
                    Mathf.Cos(yaw),
                    Vector3.right,
                    Vector3.up,
                    Vector3.forward);
                return true;
            }

            public bool TryRaycast(Vector3 origin, Vector3 direction, float maxDistance, float radius, out float t)
            {
                Vector3 expandedHalf = _half + Vector3.one * Mathf.Max(radius, 0f);
                switch (_kind)
                {
                    case BoxKind.Aabb:
                        return RaycastCenteredAabb(origin - _center, direction, expandedHalf, maxDistance, out t);
                    case BoxKind.ObbY:
                    {
                        Vector3 rel = origin - _center;
                        Vector3 localOrigin = new(
                            rel.x * _cosY - rel.z * _sinY,
                            rel.y,
                            rel.x * _sinY + rel.z * _cosY);
                        Vector3 localDir = new(
                            direction.x * _cosY - direction.z * _sinY,
                            direction.y,
                            direction.x * _sinY + direction.z * _cosY);
                        return RaycastCenteredAabb(localOrigin, localDir, expandedHalf, maxDistance, out t);
                    }
                    case BoxKind.ObbXyz:
                    {
                        Vector3 rel = origin - _center;
                        Vector3 localOrigin = new(Vector3.Dot(rel, _axisX), Vector3.Dot(rel, _axisY), Vector3.Dot(rel, _axisZ));
                        Vector3 localDir = new(
                            Vector3.Dot(direction, _axisX),
                            Vector3.Dot(direction, _axisY),
                            Vector3.Dot(direction, _axisZ));
                        return RaycastCenteredAabb(localOrigin, localDir, expandedHalf, maxDistance, out t);
                    }
                    default:
                        t = 0f;
                        return false;
                }
            }

            private static bool QuaternionToAxes(float[]? rotation, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ)
            {
                axisX = Vector3.right;
                axisY = Vector3.up;
                axisZ = Vector3.forward;
                if (rotation == null || rotation.Length < 4)
                    return false;

                float x = rotation[0];
                float y = rotation[1];
                float z = rotation[2];
                float w = rotation[3];
                float lengthSquared = x * x + y * y + z * z + w * w;
                if (!float.IsFinite(lengthSquared) || lengthSquared <= CollisionEpsilon)
                    return false;

                float invLength = 1f / Mathf.Sqrt(lengthSquared);
                x *= invLength;
                y *= invLength;
                z *= invLength;
                w *= invLength;

                float xx = x * x;
                float yy = y * y;
                float zz = z * z;
                float xy = x * y;
                float xz = x * z;
                float yz = y * z;
                float wx = w * x;
                float wy = w * y;
                float wz = w * z;

                axisX = new Vector3(1f - 2f * (yy + zz), 2f * (xy + wz), 2f * (xz - wy));
                axisY = new Vector3(2f * (xy - wz), 1f - 2f * (xx + zz), 2f * (yz + wx));
                axisZ = new Vector3(2f * (xz + wy), 2f * (yz - wx), 1f - 2f * (xx + yy));
                return true;
            }
        }

        private sealed class ServerQueryMeshGeometry
        {
            private ServerQueryMeshGeometry(string id, Vector3[] vertices, int[] indices)
            {
                Id = id;
                Vertices = vertices;
                Indices = indices;
            }

            public string Id { get; }
            public Vector3[] Vertices { get; }
            public int[] Indices { get; }

            public static bool TryCreate(GameplayQueryMeshGeometryFile file, out ServerQueryMeshGeometry geometry)
            {
                geometry = null!;
                if (string.IsNullOrEmpty(file.id) ||
                    file.vertices == null ||
                    file.indices == null ||
                    file.vertices.Length < 9 ||
                    file.indices.Length < 3 ||
                    file.vertices.Length % 3 != 0 ||
                    file.indices.Length % 3 != 0)
                {
                    return false;
                }

                Vector3[] vertices = new Vector3[file.vertices.Length / 3];
                for (int i = 0; i < vertices.Length; i++)
                {
                    int offset = i * 3;
                    vertices[i] = new Vector3(file.vertices[offset], file.vertices[offset + 1], file.vertices[offset + 2]);
                }

                geometry = new ServerQueryMeshGeometry(file.id, vertices, file.indices);
                return true;
            }
        }

        private sealed class ServerQueryMeshInstance
        {
            private ServerQueryMeshInstance(string name, Vector3[] worldVertices, int[] indices, ServerBounds bounds)
            {
                Name = string.IsNullOrWhiteSpace(name) ? "server query mesh" : name;
                _worldVertices = worldVertices;
                _indices = indices;
                _bounds = bounds;
            }

            private readonly Vector3[] _worldVertices;
            private readonly int[] _indices;
            private readonly ServerBounds _bounds;

            public string Name { get; }

            public static bool TryCreate(
                GameplayQueryMeshInstanceFile file,
                ServerQueryMeshGeometry geometry,
                out ServerQueryMeshInstance instance)
            {
                instance = null!;
                if (file.transform == null || file.transform.Length != 16)
                    return false;

                Vector3[] worldVertices = new Vector3[geometry.Vertices.Length];
                ServerBounds bounds = default;
                for (int i = 0; i < geometry.Vertices.Length; i++)
                {
                    Vector3 world = TransformPoint(file.transform, geometry.Vertices[i]);
                    worldVertices[i] = world;
                    bounds = i == 0 ? new ServerBounds(world, world) : bounds.Encapsulate(world);
                }

                instance = new ServerQueryMeshInstance(file.name, worldVertices, geometry.Indices, bounds);
                return true;
            }

            public bool TryRaycast(
                Vector3 origin,
                Vector3 direction,
                float maxDistance,
                float radius,
                LocalProbeHit? best,
                out float hitT)
            {
                hitT = 0f;
                float closest = best.HasValue ? Mathf.Min(best.Value.Distance, maxDistance) : maxDistance;
                if (!_bounds.Expanded(radius).Raycast(origin, direction, closest, out _))
                    return false;

                bool hit = false;
                for (int i = 0; i < _indices.Length; i += 3)
                {
                    int ia = _indices[i];
                    int ib = _indices[i + 1];
                    int ic = _indices[i + 2];
                    if ((uint)ia >= _worldVertices.Length ||
                        (uint)ib >= _worldVertices.Length ||
                        (uint)ic >= _worldVertices.Length)
                    {
                        continue;
                    }

                    float? t = RaycastSweptSphereTriangle(
                        origin,
                        direction,
                        Mathf.Max(radius, 0f),
                        _worldVertices[ia],
                        _worldVertices[ib],
                        _worldVertices[ic],
                        closest);
                    if (t.HasValue && t.Value >= 0f && t.Value <= closest)
                    {
                        closest = t.Value;
                        hitT = t.Value;
                        hit = true;
                    }
                }

                return hit;
            }
        }

        private readonly struct ServerBounds
        {
            public ServerBounds(Vector3 min, Vector3 max)
            {
                Min = min;
                Max = max;
            }

            private Vector3 Min { get; }
            private Vector3 Max { get; }

            public ServerBounds Encapsulate(Vector3 point)
            {
                return new ServerBounds(Vector3.Min(Min, point), Vector3.Max(Max, point));
            }

            public ServerBounds Expanded(float amount)
            {
                Vector3 expansion = Vector3.one * Mathf.Max(amount, 0f);
                return new ServerBounds(Min - expansion, Max + expansion);
            }

            public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out float t)
            {
                return RaycastAabb(origin, direction, Min, Max, maxDistance, out t);
            }
        }

        private static Vector3 TransformPoint(float[] transform, Vector3 point)
        {
            return new Vector3(
                transform[0] * point.x + transform[1] * point.y + transform[2] * point.z + transform[3],
                transform[4] * point.x + transform[5] * point.y + transform[6] * point.z + transform[7],
                transform[8] * point.x + transform[9] * point.y + transform[10] * point.z + transform[11]);
        }

        private static bool RaycastCenteredAabb(
            Vector3 localOrigin,
            Vector3 localDirection,
            Vector3 half,
            float maxDistance,
            out float t)
        {
            return RaycastAabb(localOrigin, localDirection, -half, half, maxDistance, out t);
        }

        private static bool RaycastAabb(
            Vector3 origin,
            Vector3 direction,
            Vector3 min,
            Vector3 max,
            float maxDistance,
            out float t)
        {
            float tMin = 0f;
            float tMax = maxDistance;
            if (!RaycastAabbAxis(origin.x, direction.x, min.x, max.x, ref tMin, ref tMax) ||
                !RaycastAabbAxis(origin.y, direction.y, min.y, max.y, ref tMin, ref tMax) ||
                !RaycastAabbAxis(origin.z, direction.z, min.z, max.z, ref tMin, ref tMax))
            {
                t = 0f;
                return false;
            }

            t = tMin;
            return t <= maxDistance + CollisionEpsilon;
        }

        private static bool RaycastAabbAxis(
            float origin,
            float direction,
            float min,
            float max,
            ref float tMin,
            ref float tMax)
        {
            if (Mathf.Abs(direction) <= CollisionEpsilon)
                return origin >= min && origin <= max;

            float inv = 1f / direction;
            float t1 = (min - origin) * inv;
            float t2 = (max - origin) * inv;
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            tMin = Mathf.Max(tMin, t1);
            tMax = Mathf.Min(tMax, t2);
            return tMin <= tMax + CollisionEpsilon;
        }

        private static float? RaycastSweptSphereTriangle(
            Vector3 origin,
            Vector3 direction,
            float radius,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            float maxDistance)
        {
            if (!float.IsFinite(radius) || radius <= CollisionEpsilon)
                return RaycastTriangle(origin, direction, a, b, c, maxDistance);

            float? best = null;
            ConsiderHit(ref best, RaycastSweptSphereTriangleFace(origin, direction, radius, a, b, c, maxDistance), maxDistance);
            ConsiderHit(ref best, RaycastCapsuleSegment(origin, direction, a, b, radius, maxDistance), maxDistance);
            ConsiderHit(ref best, RaycastCapsuleSegment(origin, direction, b, c, radius, maxDistance), maxDistance);
            ConsiderHit(ref best, RaycastCapsuleSegment(origin, direction, c, a, radius, maxDistance), maxDistance);
            return best;
        }

        private static float? RaycastTriangle(
            Vector3 origin,
            Vector3 direction,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            float maxDistance)
        {
            Vector3 edge1 = b - a;
            Vector3 edge2 = c - a;
            Vector3 pvec = Vector3.Cross(direction, edge2);
            float det = Vector3.Dot(edge1, pvec);
            if (!float.IsFinite(det) || Mathf.Abs(det) <= CollisionEpsilon)
                return null;

            float invDet = 1f / det;
            Vector3 tvec = origin - a;
            float u = Vector3.Dot(tvec, pvec) * invDet;
            if (u < 0f || u > 1f)
                return null;

            Vector3 qvec = Vector3.Cross(tvec, edge1);
            float v = Vector3.Dot(direction, qvec) * invDet;
            if (v < 0f || u + v > 1f)
                return null;

            float t = Vector3.Dot(edge2, qvec) * invDet;
            return t >= 0f && t <= maxDistance ? t : null;
        }

        private static float? RaycastSweptSphereTriangleFace(
            Vector3 origin,
            Vector3 direction,
            float radius,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            float maxDistance)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            float normalLength = normal.magnitude;
            if (normalLength <= CollisionEpsilon)
                return null;

            Vector3 n = normal / normalLength;
            float startDistance = Vector3.Dot(origin - a, n);
            float denom = Vector3.Dot(direction, n);
            float? best = null;

            if (Mathf.Abs(startDistance) <= radius + CollisionEpsilon)
                ConsiderHit(ref best, 0f, maxDistance);

            if (Mathf.Abs(denom) > CollisionEpsilon)
            {
                ConsiderFaceCandidate(ref best, origin, direction, radius, a, b, c, n, startDistance, denom, -radius, maxDistance);
                ConsiderFaceCandidate(ref best, origin, direction, radius, a, b, c, n, startDistance, denom, radius, maxDistance);
            }

            return best;
        }

        private static void ConsiderFaceCandidate(
            ref float? best,
            Vector3 origin,
            Vector3 direction,
            float radius,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 normal,
            float startDistance,
            float denom,
            float targetDistance,
            float maxDistance)
        {
            _ = radius;
            float t = (targetDistance - startDistance) / denom;
            if (t < -CollisionEpsilon || t > maxDistance + CollisionEpsilon)
                return;

            t = Mathf.Max(0f, t);
            Vector3 center = origin + direction * t;
            float signedDistance = Vector3.Dot(center - a, normal);
            Vector3 projected = center - normal * signedDistance;
            if (PointInTriangle(projected, a, b, c))
                ConsiderHit(ref best, t, maxDistance);
        }

        private static float? RaycastCapsuleSegment(
            Vector3 origin,
            Vector3 direction,
            Vector3 a,
            Vector3 b,
            float radius,
            float maxDistance)
        {
            Vector3 ba = b - a;
            Vector3 oa = origin - a;
            float baba = Vector3.Dot(ba, ba);
            if (baba <= CollisionEpsilon)
                return RaycastSphere(origin, direction, a, radius, maxDistance);

            float bard = Vector3.Dot(ba, direction);
            float baoa = Vector3.Dot(ba, oa);
            float rdoa = Vector3.Dot(direction, oa);
            float oaoa = Vector3.Dot(oa, oa);
            float cylA = baba - bard * bard;
            float cylB = baba * rdoa - baoa * bard;
            float cylC = baba * oaoa - baoa * baoa - radius * radius * baba;

            float? best = null;
            if (Mathf.Abs(cylA) > CollisionEpsilon)
            {
                float h = cylB * cylB - cylA * cylC;
                if (h >= 0f)
                {
                    float t = (-cylB - Mathf.Sqrt(h)) / cylA;
                    float y = baoa + t * bard;
                    if (y > 0f && y < baba)
                        ConsiderHit(ref best, t, maxDistance);
                }
            }

            ConsiderHit(ref best, RaycastSphere(origin, direction, a, radius, maxDistance), maxDistance);
            ConsiderHit(ref best, RaycastSphere(origin, direction, b, radius, maxDistance), maxDistance);
            return best;
        }

        private static float? RaycastSphere(
            Vector3 origin,
            Vector3 direction,
            Vector3 center,
            float radius,
            float maxDistance)
        {
            Vector3 oc = origin - center;
            float b = Vector3.Dot(oc, direction);
            float c = Vector3.Dot(oc, oc) - radius * radius;
            if (c <= 0f)
                return 0f;

            float h = b * b - c;
            if (h < 0f)
                return null;

            float t = -b - Mathf.Sqrt(h);
            return t >= 0f && t <= maxDistance ? t : null;
        }

        private static bool PointInTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 v0 = c - a;
            Vector3 v1 = b - a;
            Vector3 v2 = p - a;
            float dot00 = Vector3.Dot(v0, v0);
            float dot01 = Vector3.Dot(v0, v1);
            float dot02 = Vector3.Dot(v0, v2);
            float dot11 = Vector3.Dot(v1, v1);
            float dot12 = Vector3.Dot(v1, v2);
            float denom = dot00 * dot11 - dot01 * dot01;
            if (Mathf.Abs(denom) <= CollisionEpsilon)
                return false;

            float invDenom = 1f / denom;
            float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
            float v = (dot00 * dot12 - dot01 * dot02) * invDenom;
            return u >= -CollisionEpsilon && v >= -CollisionEpsilon && u + v <= 1f + CollisionEpsilon;
        }

        private static void ConsiderHit(ref float? best, float? candidate, float maxDistance)
        {
            if (!candidate.HasValue)
                return;

            ConsiderHit(ref best, candidate.Value, maxDistance);
        }

        private static void ConsiderHit(ref float? best, float candidate, float maxDistance)
        {
            if (!float.IsFinite(candidate) || candidate < -CollisionEpsilon || candidate > maxDistance + CollisionEpsilon)
                return;

            candidate = Mathf.Max(0f, candidate);
            if (!best.HasValue || candidate < best.Value)
                best = candidate;
        }

        private sealed class ProbeVisual
        {
            private readonly LineRenderer _primary;

            private ProbeVisual(LineRenderer primary)
            {
                _primary = primary;
            }

            public static ProbeVisual Create(Transform parent, Material material, int index)
            {
                LineRenderer primary = CreateLine(parent, material, $"LineOfSightDebugGuide_Probe_{index}");
                return new ProbeVisual(primary);
            }

            public void Set(Vector3 origin, Vector3 end, LocalProbeHit? hit, Color primaryColor)
            {
                _ = hit;
                SetLine(_primary, origin, end, primaryColor);
                _primary.gameObject.SetActive(true);
            }

            public void SetActive(bool active)
            {
                _primary.gameObject.SetActive(active);
            }

            public static LineRenderer CreateLine(Transform parent, Material material, string name)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                var line = go.AddComponent<LineRenderer>();
                line.sharedMaterial = material;
                line.positionCount = 2;
                line.useWorldSpace = true;
                line.widthMultiplier = 0.035f;
                line.numCapVertices = 4;
                line.numCornerVertices = 2;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                go.SetActive(false);
                return line;
            }

            public static void SetLine(LineRenderer line, Vector3 start, Vector3 end, Color color)
            {
                line.startColor = color;
                line.endColor = color;
                line.SetPosition(0, start);
                line.SetPosition(1, end);
            }
        }
    }
}
#endif
