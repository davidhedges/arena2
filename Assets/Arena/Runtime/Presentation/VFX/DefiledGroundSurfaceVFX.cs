#nullable enable

using System.Collections.Generic;
using Arena.Entity;
using Arena.Input;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arena.Presentation.VFX
{
    [DisallowMultipleComponent]
    public sealed class DefiledGroundSurfaceVFX : MonoBehaviour, ICombatVFXGracefulEnd
    {
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
        private static readonly int MetallicGlossMapId = Shader.PropertyToID("_MetallicGlossMap");
        private static readonly int OcclusionMapId = Shader.PropertyToID("_OcclusionMap");
        private static readonly int ParallaxMapId = Shader.PropertyToID("_ParallaxMap");
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");

        [SerializeField] private Material sourceMaterial = null!;
        [SerializeField] private Texture2D fallbackBaseMap = null!;
        [SerializeField] private Shader dissolveShader = null!;
        [SerializeField, Min(0.1f)] private float radiusMeters = 4.6f;
        [SerializeField, Min(0.1f)] private float uvTiling = 1f;
        [SerializeField, Range(0f, 1f)] private float opacity = 1f;
        [SerializeField, Min(0.05f)] private float dissolveSeconds = 0.7f;
        [SerializeField, Range(12, 128)] private int angularSegments = 96;
        [SerializeField, Range(2, 32)] private int radialSegments = 18;

        private Mesh? _mesh;
        private Material? _runtimeMaterial;
        private bool _ending;
        private float _endElapsed;

        private void Awake()
        {
            BuildSurface();
        }

        private void Start()
        {
            // The movement environment can become available one frame after a
            // reconstructed persistent-area row. Rebuild once so uneven ground
            // receives the same authored surface instead of a hovering flat disc.
            RebuildMesh();
        }

        private void Update()
        {
            if (!_ending || _runtimeMaterial == null)
                return;

            _endElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_endElapsed / Mathf.Max(0.05f, dissolveSeconds));
            _runtimeMaterial.SetFloat(DissolveId, Mathf.SmoothStep(0f, 1f, t));
            if (t >= 1f)
                Destroy(gameObject);
        }

        public bool BeginGracefulEnd()
        {
            if (_ending)
                return true;

            _ending = true;
            _endElapsed = 0f;
            return true;
        }

        private void BuildSurface()
        {
            var filter = gameObject.AddComponent<MeshFilter>();
            var renderer = gameObject.AddComponent<MeshRenderer>();

            _mesh = new Mesh { name = "DefiledGround_SkullSurface" };
            _mesh.MarkDynamic();
            filter.sharedMesh = _mesh;

            _runtimeMaterial = CreateRuntimeMaterial();
            renderer.sharedMaterial = _runtimeMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            renderer.sortingOrder = 1;

            RebuildMesh();
        }

        private Material CreateRuntimeMaterial()
        {
            var material = new Material(dissolveShader)
            {
                name = "M_Deadlands_SkullWall_DefiledGround_Runtime",
                renderQueue = (int)RenderQueue.Transparent,
            };

            if (sourceMaterial != null)
            {
                CopyTexture(sourceMaterial, material, BaseMapId, "_BaseMap", fallbackBaseMap);
                CopyTexture(sourceMaterial, material, BumpMapId, "_BumpMap");
                CopyTexture(sourceMaterial, material, MetallicGlossMapId, "_MetallicGlossMap");
                CopyTexture(sourceMaterial, material, OcclusionMapId, "_OcclusionMap");
                CopyTexture(sourceMaterial, material, ParallaxMapId, "_ParallaxMap");

                CopyFloat(sourceMaterial, material, "_BumpScale");
                CopyFloat(sourceMaterial, material, "_Metallic");
                CopyFloat(sourceMaterial, material, "_Smoothness");
                CopyFloat(sourceMaterial, material, "_OcclusionStrength");
                CopyFloat(sourceMaterial, material, "_Parallax");

                if (sourceMaterial.HasProperty(BaseColorId))
                    material.SetColor(BaseColorId, sourceMaterial.GetColor(BaseColorId));
                else if (sourceMaterial.HasProperty("_Color"))
                    material.SetColor(BaseColorId, sourceMaterial.GetColor("_Color"));
            }
            else if (fallbackBaseMap != null)
            {
                material.SetTexture(BaseMapId, fallbackBaseMap);
            }

            material.SetFloat(OpacityId, opacity);
            material.SetFloat(DissolveId, 0f);
            return material;
        }

        private static void CopyTexture(
            Material source,
            Material destination,
            int destinationPropertyId,
            string sourceProperty,
            Texture? fallback = null)
        {
            Texture? texture = source.HasProperty(sourceProperty)
                ? source.GetTexture(sourceProperty)
                : null;
            texture ??= fallback;
            if (texture == null)
                return;

            destination.SetTexture(destinationPropertyId, texture);
            if (!source.HasProperty(sourceProperty))
                return;

            destination.SetTextureScale(destinationPropertyId, source.GetTextureScale(sourceProperty));
            destination.SetTextureOffset(destinationPropertyId, source.GetTextureOffset(sourceProperty));
        }

        private static void CopyFloat(Material source, Material destination, string property)
        {
            if (source.HasProperty(property) && destination.HasProperty(property))
                destination.SetFloat(property, source.GetFloat(property));
        }

        private void RebuildMesh()
        {
            if (_mesh == null)
                return;

            int segmentCount = Mathf.Clamp(angularSegments, 12, 128);
            int ringCount = Mathf.Clamp(radialSegments, 2, 32);
            var vertices = new List<Vector3>(1 + segmentCount * ringCount);
            var uvs = new List<Vector2>(vertices.Capacity);
            var triangles = new List<int>(segmentCount * (1 + (ringCount - 1) * 2) * 3);

            vertices.Add(SurfaceVertex(Vector3.zero));
            uvs.Add(new Vector2(0.5f, 0.5f));

            for (int ring = 1; ring <= ringCount; ring++)
            {
                float ringFraction = ring / (float)ringCount;
                float distance = radiusMeters * ringFraction;
                for (int segment = 0; segment < segmentCount; segment++)
                {
                    float angle = segment / (float)segmentCount * Mathf.PI * 2f;
                    var local = new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
                    vertices.Add(SurfaceVertex(local));
                    uvs.Add(new Vector2(
                        0.5f + local.x / (radiusMeters * 2f) * uvTiling,
                        0.5f + local.z / (radiusMeters * 2f) * uvTiling));
                }
            }

            for (int segment = 0; segment < segmentCount; segment++)
            {
                int next = (segment + 1) % segmentCount;
                triangles.Add(0);
                triangles.Add(1 + next);
                triangles.Add(1 + segment);
            }

            for (int ring = 2; ring <= ringCount; ring++)
            {
                int previousStart = 1 + (ring - 2) * segmentCount;
                int currentStart = 1 + (ring - 1) * segmentCount;
                for (int segment = 0; segment < segmentCount; segment++)
                {
                    int next = (segment + 1) % segmentCount;
                    int previous = previousStart + segment;
                    int previousNext = previousStart + next;
                    int current = currentStart + segment;
                    int currentNext = currentStart + next;

                    triangles.Add(previous);
                    triangles.Add(currentNext);
                    triangles.Add(current);
                    triangles.Add(previous);
                    triangles.Add(previousNext);
                    triangles.Add(currentNext);
                }
            }

            _mesh.Clear();
            _mesh.SetVertices(vertices);
            _mesh.SetUVs(0, uvs);
            _mesh.SetTriangles(triangles, 0, true);
            _mesh.RecalculateNormals();
            _mesh.RecalculateTangents();
            _mesh.RecalculateBounds();
        }

        private Vector3 SurfaceVertex(Vector3 local)
        {
            Vector3 world = transform.TransformPoint(local);
            if (EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetLocalPredictionEnvironment(out IMovementEnvironment? environment)
                && environment != null)
            {
                float groundY = environment.SampleGroundHeight(world.x, world.z, world.y + 2f);
                local.y = groundY - transform.position.y + 0.035f;
            }
            else
            {
                local.y = 0.035f;
            }

            return local;
        }

        private void OnDestroy()
        {
            if (_runtimeMaterial != null)
                Destroy(_runtimeMaterial);
            if (_mesh != null)
                Destroy(_mesh);
        }
    }
}
