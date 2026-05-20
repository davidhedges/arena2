#nullable enable

using UnityEngine;
using UnityEngine.Rendering;

namespace Arena.Presentation.Targeting
{
    /// <summary>
    /// Local-only selected-target marker. The indicator is rendered as terrain-conforming
    /// presentation geometry so character receivers, physics, and render-pipeline receiver buffers are not involved.
    /// </summary>
    public sealed class SelectedTargetIndicator : MonoBehaviour
    {
        public const float RadiusMeters = 1.0f;

        // Controls how finely the arc is tessellated around its circumference; higher values make the curve smoother.
        private const int SegmentCount = 144;
        // Controls how many radial alpha steps are available between the transparent center and brighter outer edge.
        private const int RadialBandCount = 32;
        // Controls how much of the circle is visible; 360f would draw a complete ring.
        private const float ArcAngleDegrees = 260f;
        // Controls where the visible mesh starts from the center, leaving the middle fully transparent.
        private const float InnerFadeRadius = RadiusMeters * 0.20f;
        // Controls where the sharper outer-edge glow begins.
        private const float EdgeStartRadius = RadiusMeters * 0.96f;
        // Controls how much of each arc end is used to fade smoothly to fully transparent.
        private const float EndFadeArcFraction = 0.18f;
        // Lifts the mesh slightly above sampled terrain to avoid z-fighting with the ground.
        private const float SurfaceOffsetMeters = 0.04f;
        // Minimum target movement before rebuilding the terrain-conforming mesh.
        private const float RebuildPositionEpsilonSquared = 0.000025f;
        // Minimum camera-facing direction change before rotating/rebuilding the arc.
        private const float RebuildArcDirectionDot = 0.9995f;

        private static readonly Color DefaultIndicatorColor = new(1f, 0.02f, 0.015f, 1f);
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private GameObject? _root;
        private Mesh? _mesh;
        private Material? _material;
        private Color _indicatorColor = DefaultIndicatorColor;
        private Vector3 _lastCenter = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        private Vector3 _lastArcDirection = Vector3.forward;

        public bool IsVisible => _root != null && _root.activeSelf;

        public void SetColor(Color color)
        {
            color.a = 1f;
            if (_indicatorColor.ApproximatelyEquals(color))
                return;

            _indicatorColor = color;
            if (_mesh != null)
                RefreshWorldPlacement(forceRebuild: true);
        }

        public void SetVisible(bool visible)
        {
            if (visible)
                EnsureInstance();

            if (_root != null)
                _root.SetActive(visible);
        }

        private void LateUpdate()
        {
            if (IsVisible)
                RefreshWorldPlacement();
        }

        private void EnsureInstance()
        {
            if (_root != null)
                return;

            _root = new GameObject("SelectedTargetIndicator");
            _root.transform.SetParent(null, true);

            _mesh = CreateSurface("SelectedTargetIndicator_Arc", _root.transform, IndicatorMaterial);

            RefreshWorldPlacement(forceRebuild: true);
        }

        private Material IndicatorMaterial
        {
            get
            {
                if (_material == null)
                    _material = CreateTransparentMaterial();

                return _material;
            }
        }

        private static Mesh CreateSurface(string name, Transform parent, Material material)
        {
            var surface = new GameObject(name);
            surface.transform.SetParent(parent, false);

            Mesh mesh = new() { name = name };
            var filter = surface.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            var renderer = surface.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return mesh;
        }

        private static Material CreateTransparentMaterial()
        {
            Shader shader = Shader.Find("Arena/Presentation/TargetIndicatorVertexColor");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            var material = new Material(shader)
            {
                name = "SelectedTargetIndicatorMaterial",
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

        private void RefreshWorldPlacement(bool forceRebuild = false)
        {
            if (_root == null || _mesh == null)
                return;

            Vector3 targetPosition = transform.position;
            Vector3 center = new(targetPosition.x, SampleSurfaceY(targetPosition), targetPosition.z);
            Vector3 arcDirection = ResolveCameraFacingDirection(center);

            if (!forceRebuild &&
                (center - _lastCenter).sqrMagnitude < RebuildPositionEpsilonSquared &&
                Vector3.Dot(_lastArcDirection, arcDirection) > RebuildArcDirectionDot)
            {
                return;
            }

            _lastCenter = center;
            _lastArcDirection = arcDirection;
            _root.transform.position = center;
            _root.transform.rotation = Quaternion.identity;
            _root.transform.localScale = Vector3.one;

            RebuildArcMesh(_mesh, center, arcDirection);
        }

        private static Vector3 ResolveCameraFacingDirection(Vector3 center)
        {
            Camera? camera = Camera.main;
            Vector3 direction;
            if (camera != null)
            {
                direction = camera.transform.position - center;
                direction.y = 0f;
                if (direction.sqrMagnitude > 0.0001f)
                    return direction.normalized;

                direction = -camera.transform.forward;
                direction.y = 0f;
                if (direction.sqrMagnitude > 0.0001f)
                    return direction.normalized;
            }

            return Vector3.forward;
        }

        private void RebuildArcMesh(Mesh mesh, Vector3 center, Vector3 arcDirection)
        {
            int verticesPerRing = SegmentCount + 1;
            int vertexCount = verticesPerRing * (RadialBandCount + 1);
            var vertices = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var colors = new Color[vertexCount];
            var triangles = new int[RadialBandCount * SegmentCount * 6];

            float centerAngle = Mathf.Atan2(arcDirection.z, arcDirection.x);
            float arcRadians = ArcAngleDegrees * Mathf.Deg2Rad;
            float startAngle = centerAngle - arcRadians * 0.5f;

            for (int ring = 0; ring <= RadialBandCount; ring++)
            {
                float radialT = ring / (float)RadialBandCount;
                float radius = Mathf.Lerp(InnerFadeRadius, RadiusMeters, radialT);
                float radialAlpha = EvaluateRadialAlpha(radialT);

                for (int segment = 0; segment <= SegmentCount; segment++)
                {
                    float arcT = segment / (float)SegmentCount;
                    float radians = startAngle + arcRadians * arcT;
                    int vertexIndex = ring * verticesPerRing + segment;
                    WriteArcVertex(vertices, uvs, vertexIndex, center, radians, radius);
                    colors[vertexIndex] = _indicatorColor.WithAlpha(radialAlpha * EvaluateArcAlpha(arcT));
                }
            }

            WriteArcTriangles(triangles);
            ApplyMesh(mesh, vertices, uvs, colors, triangles);
        }

        private static float EvaluateRadialAlpha(float radialT)
        {
            float edgeStartT = Mathf.InverseLerp(InnerFadeRadius, RadiusMeters, EdgeStartRadius);
            float fill = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.08f, 0.76f, radialT)) * 0.40f;
            float edge = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(edgeStartT, 0.98f, radialT)) * 0.68f;
            return Mathf.Clamp01(fill + edge);
        }

        private static float EvaluateArcAlpha(float arcT)
        {
            float fadeIn = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, EndFadeArcFraction, arcT));
            float fadeOut = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, EndFadeArcFraction, 1f - arcT));
            return fadeIn * fadeOut;
        }

        private static void WriteArcVertex(Vector3[] vertices, Vector2[] uvs, int index, Vector3 center, float radians, float radius)
        {
            float localX = Mathf.Cos(radians) * radius;
            float localZ = Mathf.Sin(radians) * radius;
            var world = new Vector3(center.x + localX, center.y, center.z + localZ);
            float localY = SampleSurfaceY(world) - center.y + SurfaceOffsetMeters;

            vertices[index] = new Vector3(localX, localY, localZ);
            uvs[index] = new Vector2(
                0.5f + localX / (RadiusMeters * 2f),
                0.5f + localZ / (RadiusMeters * 2f));
        }

        private static void WriteArcTriangles(int[] triangles)
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

        private static void ApplyMesh(Mesh mesh, Vector3[] vertices, Vector2[] uvs, Color[] colors, int[] triangles)
        {
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
        }

        private static float SampleSurfaceY(Vector3 world)
        {
            Terrain[] terrains = Terrain.activeTerrains;
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain terrain = terrains[i];
                if (terrain == null || terrain.terrainData == null)
                    continue;

                Vector3 terrainPosition = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                if (world.x < terrainPosition.x ||
                    world.z < terrainPosition.z ||
                    world.x > terrainPosition.x + size.x ||
                    world.z > terrainPosition.z + size.z)
                {
                    continue;
                }

                return terrain.SampleHeight(world) + terrainPosition.y;
            }

            return world.y;
        }

        private void OnDestroy()
        {
            if (_root != null)
                Destroy(_root);

            if (_mesh != null)
                Destroy(_mesh);

            if (_material != null)
                Destroy(_material);
        }
    }

    internal static class SelectedTargetIndicatorColorExtensions
    {
        public static bool ApproximatelyEquals(this Color left, Color right)
        {
            return Mathf.Abs(left.r - right.r) < 0.001f
                   && Mathf.Abs(left.g - right.g) < 0.001f
                   && Mathf.Abs(left.b - right.b) < 0.001f
                   && Mathf.Abs(left.a - right.a) < 0.001f;
        }

        public static Color WithAlpha(this Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }
    }
}
