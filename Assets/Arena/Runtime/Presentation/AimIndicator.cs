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
        private float _yaw;
        private float _lastYaw = float.NaN;
        private bool _triangle;
        private bool _lastTriangle;
        private Vector3 _lastCenter = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        private float _lastRadius = -1f;
        private bool _hasAimPoint;
        private const float GroundOffset = 0.05f;
        private const int SegmentCount = 144;
        private const int RadialBandCount = 32;
        private const int SurfaceSampleRingCount = 4;
        private const float InnerFadeRadiusFraction = 0.20f;
        private const float EdgeStartRadiusFraction = 0.96f;
        private const float MaxAimRayDistance = 1000f;
        private const float AimRayStepMeters = 0.5f;
        private const int AimRayRefinementSteps = 16;
        private const float AimRayCeilingOffset = 1.2f;
        private const float RebuildPositionEpsilonSquared = 0.000025f;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private Vector3[]? _vertices;
        private Vector2[]? _uvs;
        private Color[]? _colors;
        private int[]? _triangles;
        private float[]? _surfaceHeights;
        private bool _meshTopologyInitialized;

        /// <summary>Current aim point on the ground plane. Valid only when active.</summary>
        public Vector3 AimPoint { get; private set; }
        public bool IsActive => _circle != null && _circle.activeSelf;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

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
            ShowShape(radius, 0f, triangle: false, color);
        }

        /// <summary>Show an equilateral triangle using a circumradius and world yaw.</summary>
        public void ShowTriangle(float radius, float yaw, Color color)
        {
            ShowShape(radius, yaw, triangle: true, color);
        }

        private void ShowShape(float radius, float yaw, bool triangle, Color color)
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                Hide();
                return;
            }

            if (_circle == null)
                _circle = CreateCircle();

            _radius = Mathf.Max(0.01f, radius);
            _yaw = yaw;
            _triangle = triangle;
            _color = color;

            if (!_hasAimPoint)
            {
                _circle.SetActive(false);
                return;
            }

            _circle.SetActive(true);
            RefreshCircleMesh(forceRebuild:
                Mathf.Abs(_lastRadius - _radius) > 0.0001f ||
                Mathf.Abs(Mathf.DeltaAngle(_lastYaw * Mathf.Rad2Deg, _yaw * Mathf.Rad2Deg)) > 0.01f ||
                _lastTriangle != _triangle ||
                !ApproximatelySameColor(_lastColor, _color));
        }

        public void Hide()
        {
            if (_circle != null)
                _circle.SetActive(false);
            _hasAimPoint = false;
            _lastCenter = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            _lastRadius = -1f;
            _lastYaw = float.NaN;
            _lastTriangle = false;
            _lastColor = new Color(float.NaN, float.NaN, float.NaN, float.NaN);
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
            _circleMesh.MarkDynamic();
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
            _lastYaw = _yaw;
            _lastTriangle = _triangle;
            _lastColor = _color;
            _circle.transform.position = center;
            _circle.transform.rotation = Quaternion.Euler(0f, _yaw * Mathf.Rad2Deg, 0f);
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

        private void RebuildCircleMesh(Mesh mesh, Vector3 center, float radius, Color color)
        {
            EnsureMeshBuffers();
            if (_vertices == null
                || _uvs == null
                || _colors == null
                || _triangles == null
                || _surfaceHeights == null)
            {
                return;
            }

            int verticesPerRing = SegmentCount + 1;
            float innerFadeRadius = radius * InnerFadeRadiusFraction;
            float edgeStartRadius = radius * EdgeStartRadiusFraction;
            TryGetMovementEnvironment(out IMovementEnvironment? environment);
            SampleSurfaceRings(
                _surfaceHeights,
                center,
                innerFadeRadius,
                radius,
                _triangle,
                _yaw,
                environment);

            for (int ring = 0; ring <= RadialBandCount; ring++)
            {
                float radialT = ring / (float)RadialBandCount;
                float ringRadius = Mathf.Lerp(innerFadeRadius, radius, radialT);
                float radialAlpha = EvaluateRadialAlpha(radialT, innerFadeRadius, radius, edgeStartRadius);
                float samplePosition = radialT * (SurfaceSampleRingCount - 1);
                int lowerSampleRing = Mathf.FloorToInt(samplePosition);
                int upperSampleRing = Mathf.Min(lowerSampleRing + 1, SurfaceSampleRingCount - 1);
                float sampleBlend = samplePosition - lowerSampleRing;

                for (int segment = 0; segment <= SegmentCount; segment++)
                {
                    int vertexIndex = ring * verticesPerRing + segment;
                    float localY = Mathf.Lerp(
                        _surfaceHeights[lowerSampleRing * verticesPerRing + segment],
                        _surfaceHeights[upperSampleRing * verticesPerRing + segment],
                        sampleBlend);
                    WriteIndicatorVertex(
                        _vertices,
                        vertexIndex,
                        segment,
                        ringRadius,
                        _triangle,
                        localY);
                    _colors[vertexIndex] = new Color(color.r, color.g, color.b, color.a * radialAlpha);
                }
            }

            mesh.vertices = _vertices;
            mesh.colors = _colors;
            if (!_meshTopologyInitialized)
            {
                mesh.uv = _uvs;
                mesh.triangles = _triangles;
                _meshTopologyInitialized = true;
            }
            mesh.RecalculateBounds();
        }

        private void EnsureMeshBuffers()
        {
            if (_vertices != null)
                return;

            int verticesPerRing = SegmentCount + 1;
            int vertexCount = verticesPerRing * (RadialBandCount + 1);
            _vertices = new Vector3[vertexCount];
            _uvs = new Vector2[vertexCount];
            _colors = new Color[vertexCount];
            _triangles = new int[RadialBandCount * SegmentCount * 6];
            _surfaceHeights = new float[SurfaceSampleRingCount * verticesPerRing];

            for (int ring = 0; ring <= RadialBandCount; ring++)
            {
                float radialT = ring / (float)RadialBandCount;
                float normalizedRadius = Mathf.Lerp(InnerFadeRadiusFraction, 1f, radialT);
                for (int segment = 0; segment <= SegmentCount; segment++)
                {
                    int vertexIndex = ring * verticesPerRing + segment;
                    Vector2 boundary = IndicatorBoundaryPoint(segment, 1f, _triangle);
                    _uvs[vertexIndex] = new Vector2(
                        0.5f + boundary.x * normalizedRadius * 0.5f,
                        0.5f + boundary.y * normalizedRadius * 0.5f);
                }
            }

            WriteCircleTriangles(_triangles);
        }

        private static void SampleSurfaceRings(
            float[] surfaceHeights,
            Vector3 center,
            float innerRadius,
            float outerRadius,
            bool triangle,
            float yaw,
            IMovementEnvironment? environment)
        {
            int verticesPerRing = SegmentCount + 1;
            for (int sampleRing = 0; sampleRing < SurfaceSampleRingCount; sampleRing++)
            {
                float radialT = sampleRing / (float)(SurfaceSampleRingCount - 1);
                float ringRadius = Mathf.Lerp(innerRadius, outerRadius, radialT);
                int ringStart = sampleRing * verticesPerRing;
                for (int segment = 0; segment < SegmentCount; segment++)
                {
                    Vector2 local = IndicatorBoundaryPoint(segment, ringRadius, triangle);
                    float sin = Mathf.Sin(yaw);
                    float cos = Mathf.Cos(yaw);
                    float worldX = center.x + local.x * cos + local.y * sin;
                    float worldZ = center.z - local.x * sin + local.y * cos;
                    float surfaceY = environment != null
                        ? SampleSurfaceY(new Vector3(worldX, center.y, worldZ), environment)
                        : center.y;
                    surfaceHeights[ringStart + segment] = surfaceY - center.y + GroundOffset;
                }

                surfaceHeights[ringStart + SegmentCount] = surfaceHeights[ringStart];
            }
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

        private static void WriteIndicatorVertex(
            Vector3[] vertices,
            int index,
            int segment,
            float ringRadius,
            bool triangle,
            float localY)
        {
            Vector2 boundary = IndicatorBoundaryPoint(segment, ringRadius, triangle);
            vertices[index] = new Vector3(boundary.x, localY, boundary.y);
        }

        private static Vector2 IndicatorBoundaryPoint(int segment, float radius, bool triangle)
        {
            if (!triangle)
            {
                float radians = (segment / (float)SegmentCount) * Mathf.PI * 2f;
                return new Vector2(Mathf.Cos(radians) * radius, Mathf.Sin(radians) * radius);
            }

            Vector2 rear = new(0f, -radius);
            Vector2 frontRight = new(radius * 0.8660254f, radius * 0.5f);
            Vector2 frontLeft = new(-radius * 0.8660254f, radius * 0.5f);
            if (segment >= SegmentCount)
                return rear;
            float perimeter = segment / (float)SegmentCount * 3f;
            int edge = Mathf.FloorToInt(perimeter);
            float edgeT = perimeter - edge;
            return edge switch
            {
                0 => Vector2.Lerp(rear, frontRight, edgeT),
                1 => Vector2.Lerp(frontRight, frontLeft, edgeT),
                _ => Vector2.Lerp(frontLeft, rear, edgeT),
            };
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
