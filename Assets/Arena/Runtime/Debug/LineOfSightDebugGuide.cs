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
                ServerLosProbeHit? hit = serverCollisionData?.FindFirstHit(origin, end, ProbeRadius);
                if (hit.HasValue)
                {
                    ServerLosProbeHit localHit = hit.Value;
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
            // ForSceneName falls back to the default profile for unknown scene
            // names; drawing another world's geometry here is worse than
            // drawing nothing (S4 shipped the same guard in the advisory).
            OpenWorldSceneProfile profile = OpenWorldSceneProfile.ForSceneName(sceneName);
            _serverCollisionData = string.Equals(profile.SceneName, sceneName, StringComparison.Ordinal)
                ? ServerLosCollisionData.Load(profile)
                : null;
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

            public void Set(Vector3 origin, Vector3 end, ServerLosProbeHit? hit, Color primaryColor)
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
