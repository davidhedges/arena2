#nullable enable
using UnityEngine;
using UnityEngine.Rendering;
using Arena.Entity;
using Arena.Input;

namespace Arena.Presentation
{
    /// <summary>
    /// Ground-plane aim indicator for point-targeted spells (e.g. METEOR).
    /// Shows a circle where the cursor ray intersects the client movement surface.
    /// Singleton — managed by SpellInputHandler.
    /// </summary>
    public class AimIndicator : MonoBehaviour
    {
        public static AimIndicator Instance { get; private set; } = null!;

        private GameObject? _circle;
        private Mesh? _circleMesh;
        private Material? _circleMaterial;
        private Color _color = new(1f, 0.02f, 0.015f, 0.72f);
        private Color _lastColor = new(float.NaN, float.NaN, float.NaN, float.NaN);
        private float _radius = 1f;
        private Vector3 _lastCenter = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        private float _lastRadius = -1f;
        private bool _hasAimPoint;
        private const float GroundOffset = 0.05f;
        private const int SegmentCount = 144;
        private const int RadialBandCount = 32;
        private const float InnerFadeRadiusFraction = 0.20f;
        private const float EdgeStartRadiusFraction = 0.96f;
        private const float MaxAimRayDistance = 1000f;
        private const float AimRayStepMeters = 0.5f;
        private const int AimRayRefinementSteps = 16;
        private const float AimRayCeilingOffset = 1.2f;
        private const float RebuildPositionEpsilonSquared = 0.000025f;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>Current aim point on the ground plane. Valid only when active.</summary>
        public Vector3 AimPoint { get; private set; }
        public bool IsActive => _circle != null && _circle.activeSelf;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("AimIndicator");
            DontDestroyOnLoad(go);
            go.AddComponent<AimIndicator>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        /// <summary>Show a circle indicator with the given radius.</summary>
        public void ShowCircle(float radius, Color color)
        {
            if (_circle == null)
                _circle = CreateCircle();

            _radius = Mathf.Max(0.01f, radius);
            _color = color;

            if (!_hasAimPoint)
            {
                _circle.SetActive(false);
                return;
            }

            _circle.SetActive(true);
            RefreshCircleMesh(forceRebuild:
                Mathf.Abs(_lastRadius - _radius) > 0.0001f ||
                !ApproximatelySameColor(_lastColor, _color));
        }

        public void Hide()
        {
            if (_circle != null)
                _circle.SetActive(false);
            _hasAimPoint = false;
            _lastCenter = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            _lastRadius = -1f;
            _lastColor = new Color(float.NaN, float.NaN, float.NaN, float.NaN);
        }

        private void Update()
        {
            if (_circle == null || !_circle.activeSelf) return;

            LocalPlayerInputSource? input = EntityRegistry.Instance?.LocalPlayerEntity?.GetLocalInputSource();
            if (input == null) return;

            RefreshFromCursor(input.MousePosition);
        }

        public bool RefreshFromCursor(Vector2 mousePosition)
        {
            var cam = Camera.main;
            if (cam == null) return false;

            var ray = cam.ScreenPointToRay(mousePosition);
            if (!TryResolveAimPoint(ray, out Vector3 aimPoint))
                return false;

            AimPoint = aimPoint;
            _hasAimPoint = true;
            RefreshCircleMesh();
            return true;
        }

        private static bool TryResolveAimPoint(Ray ray, out Vector3 aimPoint)
        {
            if (TryRaycastMovementSurface(ray, out aimPoint))
                return true;

            var groundPlane = new Plane(Vector3.up, Vector3.zero);
            if (!groundPlane.Raycast(ray, out float distance))
            {
                aimPoint = default;
                return false;
            }

            Vector3 point = ray.GetPoint(distance);
            float surfaceY = SampleSurfaceY(point);
            aimPoint = new Vector3(point.x, surfaceY, point.z);
            return true;
        }

        private static bool TryRaycastMovementSurface(Ray ray, out Vector3 aimPoint)
        {
            aimPoint = default;
            if (!TryGetMovementEnvironment(out IMovementEnvironment? environment) || environment == null)
                return false;

            float previousDistance = 0f;
            if (HeightDelta(ray.GetPoint(previousDistance), environment) <= 0f)
            {
                Vector3 point = ray.GetPoint(previousDistance);
                aimPoint = new Vector3(point.x, SampleSurfaceY(point, environment), point.z);
                return true;
            }

            for (float distance = AimRayStepMeters;
                 distance <= MaxAimRayDistance;
                 distance += AimRayStepMeters)
            {
                Vector3 point = ray.GetPoint(distance);
                float delta = HeightDelta(point, environment);
                if (delta > 0f)
                {
                    previousDistance = distance;
                    continue;
                }

                float low = previousDistance;
                float high = distance;
                for (int i = 0; i < AimRayRefinementSteps; i++)
                {
                    float mid = (low + high) * 0.5f;
                    Vector3 midPoint = ray.GetPoint(mid);
                    if (HeightDelta(midPoint, environment) > 0f)
                        low = mid;
                    else
                        high = mid;
                }

                Vector3 hit = ray.GetPoint(high);
                aimPoint = new Vector3(hit.x, SampleSurfaceY(hit, environment), hit.z);
                return true;
            }

            return false;
        }

        private static float HeightDelta(Vector3 point, IMovementEnvironment environment)
        {
            return point.y - SampleSurfaceY(point, environment);
        }

        private static bool TryGetMovementEnvironment(out IMovementEnvironment? environment)
        {
            var registry = EntityRegistry.Instance;
            if (registry != null && registry.TryGetLocalPredictionEnvironment(out environment))
                return environment != null;

            environment = null;
            return false;
        }

        private GameObject CreateCircle()
        {
            var go = new GameObject("AimCircle");
            go.transform.SetParent(transform, false);

            var mf = go.AddComponent<MeshFilter>();
            _circleMesh = new Mesh { name = "AimCircle_Surface" };
            mf.sharedMesh = _circleMesh;

            var mr = go.AddComponent<MeshRenderer>();
            _circleMaterial = CreateTransparentMaterial();
            mr.sharedMaterial = _circleMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            return go;
        }

        private static Material CreateTransparentMaterial()
        {
            Shader shader = Shader.Find("Arena/Presentation/TargetIndicatorVertexColor");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            var material = new Material(shader)
            {
                name = "AimIndicatorMaterial",
                renderQueue = (int)RenderQueue.Transparent,
            };

            if (material.HasProperty(BaseColorId))
                material.SetColor(BaseColorId, Color.white);

            if (material.HasProperty(ColorId))
                material.SetColor(ColorId, Color.white);
            SetFloatIfPresent(material, "_Surface", 1f);
            SetFloatIfPresent(material, "_Blend", 0f);
            SetFloatIfPresent(material, "_Cull", 0f);
            SetFloatIfPresent(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
            SetFloatIfPresent(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, "_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            return material;
        }

        private static void SetFloatIfPresent(Material material, string propertyName, float value)
        {
            if (material.HasProperty(propertyName))
                material.SetFloat(propertyName, value);
        }

        private void RefreshCircleMesh(bool forceRebuild = false)
        {
            if (_circle == null || _circleMesh == null)
                return;

            Vector3 center = new(AimPoint.x, AimPoint.y + GroundOffset, AimPoint.z);
            if (!forceRebuild
                && Mathf.Abs(_lastRadius - _radius) < 0.0001f
                && (center - _lastCenter).sqrMagnitude < RebuildPositionEpsilonSquared)
            {
                return;
            }

            _lastCenter = center;
            _lastRadius = _radius;
            _lastColor = _color;
            _circle.transform.position = center;
            _circle.transform.rotation = Quaternion.identity;
            _circle.transform.localScale = Vector3.one;
            RebuildCircleMesh(_circleMesh, center, _radius, _color);
        }

        private static bool ApproximatelySameColor(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.001f
                && Mathf.Abs(a.g - b.g) < 0.001f
                && Mathf.Abs(a.b - b.b) < 0.001f
                && Mathf.Abs(a.a - b.a) < 0.001f;
        }

        private static void RebuildCircleMesh(Mesh mesh, Vector3 center, float radius, Color color)
        {
            int verticesPerRing = SegmentCount + 1;
            int vertexCount = verticesPerRing * (RadialBandCount + 1);
            var vertices = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var colors = new Color[vertexCount];
            var triangles = new int[RadialBandCount * SegmentCount * 6];

            float innerFadeRadius = radius * InnerFadeRadiusFraction;
            float edgeStartRadius = radius * EdgeStartRadiusFraction;
            for (int ring = 0; ring <= RadialBandCount; ring++)
            {
                float radialT = ring / (float)RadialBandCount;
                float ringRadius = Mathf.Lerp(innerFadeRadius, radius, radialT);
                float radialAlpha = EvaluateRadialAlpha(radialT, innerFadeRadius, radius, edgeStartRadius);

                for (int segment = 0; segment <= SegmentCount; segment++)
                {
                    float radians = (segment / (float)SegmentCount) * Mathf.PI * 2f;
                    int vertexIndex = ring * verticesPerRing + segment;
                    WriteCircleVertex(vertices, uvs, vertexIndex, center, radians, ringRadius, radius);
                    colors[vertexIndex] = new Color(color.r, color.g, color.b, color.a * radialAlpha);
                }
            }

            WriteCircleTriangles(triangles);
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
        }

        private static float EvaluateRadialAlpha(
            float radialT,
            float innerFadeRadius,
            float radius,
            float edgeStartRadius)
        {
            float edgeStartT = Mathf.InverseLerp(innerFadeRadius, radius, edgeStartRadius);
            float fill = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.08f, 0.76f, radialT)) * 0.34f;
            float edge = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(edgeStartT, 0.98f, radialT)) * 0.72f;
            return Mathf.Clamp01(fill + edge);
        }

        private static void WriteCircleVertex(
            Vector3[] vertices,
            Vector2[] uvs,
            int index,
            Vector3 center,
            float radians,
            float ringRadius,
            float outerRadius)
        {
            float localX = Mathf.Cos(radians) * ringRadius;
            float localZ = Mathf.Sin(radians) * ringRadius;
            var world = new Vector3(center.x + localX, center.y, center.z + localZ);
            float localY = SampleSurfaceY(world) - center.y + GroundOffset;

            vertices[index] = new Vector3(localX, localY, localZ);
            uvs[index] = new Vector2(
                0.5f + localX / (outerRadius * 2f),
                0.5f + localZ / (outerRadius * 2f));
        }

        private static void WriteCircleTriangles(int[] triangles)
        {
            int verticesPerRing = SegmentCount + 1;
            for (int ring = 0; ring < RadialBandCount; ring++)
            {
                for (int segment = 0; segment < SegmentCount; segment++)
                {
                    int inner = ring * verticesPerRing + segment;
                    int innerNext = inner + 1;
                    int outer = (ring + 1) * verticesPerRing + segment;
                    int outerNext = outer + 1;
                    int triangleIndex = (ring * SegmentCount + segment) * 6;

                    triangles[triangleIndex++] = inner;
                    triangles[triangleIndex++] = innerNext;
                    triangles[triangleIndex++] = outer;
                    triangles[triangleIndex++] = outer;
                    triangles[triangleIndex++] = innerNext;
                    triangles[triangleIndex++] = outerNext;
                }
            }
        }

        private static float SampleSurfaceY(Vector3 world)
        {
            if (TryGetMovementEnvironment(out IMovementEnvironment? environment) && environment != null)
                return SampleSurfaceY(world, environment);

            return world.y;
        }

        private static float SampleSurfaceY(Vector3 world, IMovementEnvironment environment)
        {
            return environment.SampleGroundHeight(world.x, world.z, world.y - AimRayCeilingOffset);
        }
    }
}
