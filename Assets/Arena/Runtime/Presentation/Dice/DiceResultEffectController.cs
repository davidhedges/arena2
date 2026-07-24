#nullable enable
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arena.Presentation.Dice
{
    [DisallowMultipleComponent]
    internal sealed class DiceResultEffectController : MonoBehaviour
    {
        private const int MaximumParticles = 24;
        private static readonly int TintId = Shader.PropertyToID("_Tint");

        private readonly Spark[] _sparks = new Spark[MaximumParticles];
        private readonly Vector3[] _sparkVertices = new Vector3[MaximumParticles * 4];
        private readonly Color[] _sparkColors = new Color[MaximumParticles * 4];
        private readonly int[] _sparkTriangles = new int[MaximumParticles * 6];

        private Transform? _effectRoot;
        private Transform? _pulseTransform;
        private Transform? _haloTransform;
        private MeshRenderer? _pulseRenderer;
        private MeshRenderer? _haloRenderer;
        private MeshRenderer? _sparkRenderer;
        private Mesh? _pulseMesh;
        private Mesh? _haloMesh;
        private Mesh? _sparkMesh;
        private Material? _material;
        private readonly MaterialPropertyBlock _pulseProperties = new();
        private readonly MaterialPropertyBlock _haloProperties = new();
        private readonly MaterialPropertyBlock _sparkProperties = new();
        private DiceResultClass _resultClass;
        private float _effectElapsed;
        private bool _playing;
        private int _activeSparkCount;
        private int _overlayLayer;

        public void Initialize(int overlayLayer)
        {
            _overlayLayer = overlayLayer;
            Shader? shader = Shader.Find("Arena/Dice/ResultVfx");
            if (shader == null)
            {
                Debug.LogError("[DiceResultEffectController] Missing Arena/Dice/ResultVfx shader.");
                enabled = false;
                return;
            }

            _material = new Material(shader)
            {
                name = "Dice Result VFX Material",
                renderQueue = (int)RenderQueue.Transparent + 40
            };

            GameObject effectObject = new("DiceResultEffects");
            effectObject.layer = overlayLayer;
            _effectRoot = effectObject.transform;
            _effectRoot.SetParent(transform, false);

            _pulseMesh = BuildAnnulusMesh("Dice Settle Pulse", 0.79f, 1f, 64);
            _haloMesh = BuildRuneHaloMesh();
            _sparkMesh = BuildSparkMesh();

            (_pulseTransform, _pulseRenderer) = CreateRenderer("SettlePulse", _pulseMesh);
            (_haloTransform, _haloRenderer) = CreateRenderer("RuneHalo", _haloMesh);
            (_, _sparkRenderer) = CreateRenderer("ResultMotes", _sparkMesh);
            _sparkRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _sparkRenderer.receiveShadows = false;
            _sparkRenderer.sharedMaterial = _material;
            _sparkProperties.SetColor(TintId, Color.white);
            _sparkRenderer.SetPropertyBlock(_sparkProperties);

            for (int i = 0; i < MaximumParticles; i++)
            {
                int vertex = i * 4;
                int triangle = i * 6;
                _sparkTriangles[triangle] = vertex;
                _sparkTriangles[triangle + 1] = vertex + 1;
                _sparkTriangles[triangle + 2] = vertex + 2;
                _sparkTriangles[triangle + 3] = vertex;
                _sparkTriangles[triangle + 4] = vertex + 2;
                _sparkTriangles[triangle + 5] = vertex + 3;
            }

            HideImmediate();
        }

        public void Play(
            DiceResultClass resultClass,
            Vector3 anchor,
            Vector3 towardCamera,
            Vector3 cameraUp,
            uint cosmeticSeed)
        {
            if (!enabled || _effectRoot == null)
                return;

            _resultClass = resultClass;
            _effectElapsed = 0f;
            _playing = true;
            SetAnchor(anchor, towardCamera, cameraUp);
            ClearSparks();

            if (_pulseRenderer != null)
                _pulseRenderer.enabled = true;
            if (_haloRenderer != null)
                _haloRenderer.enabled = resultClass != DiceResultClass.Ordinary;

            switch (resultClass)
            {
                case DiceResultClass.Positive:
                    EmitPositiveSparks(cosmeticSeed);
                    break;
                case DiceResultClass.Negative:
                    EmitNegativeMotes(cosmeticSeed);
                    break;
            }
        }

        public void SetAnchor(Vector3 anchor, Vector3 towardCamera, Vector3 cameraUp)
        {
            if (_effectRoot == null)
                return;

            Vector3 facing = towardCamera.sqrMagnitude > 0.001f
                ? towardCamera.normalized
                : Vector3.back;
            Vector3 up = Vector3.ProjectOnPlane(cameraUp, facing).normalized;
            if (up.sqrMagnitude < 0.001f)
                up = Vector3.up;

            // Sit just behind the die so the pulse wraps it without obscuring the result.
            _effectRoot.position = anchor - facing * 0.17f;
            _effectRoot.rotation = Quaternion.LookRotation(facing, up);
        }

        public void HideImmediate()
        {
            _playing = false;
            _effectElapsed = 0f;
            ClearSparks();
            if (_pulseRenderer != null)
                _pulseRenderer.enabled = false;
            if (_haloRenderer != null)
                _haloRenderer.enabled = false;
            if (_sparkRenderer != null)
                _sparkRenderer.enabled = false;
        }

        private void Update()
        {
            if (!_playing)
                return;

            float deltaTime = Time.unscaledDeltaTime;
            _effectElapsed += deltaTime;
            UpdateRings();
            UpdateSparks(deltaTime);

            float ringDuration = _resultClass switch
            {
                DiceResultClass.Positive => 1.05f,
                DiceResultClass.Negative => 0.82f,
                _ => 0.52f
            };
            if (_effectElapsed >= ringDuration)
            {
                if (_pulseRenderer != null)
                    _pulseRenderer.enabled = false;
                if (_haloRenderer != null)
                    _haloRenderer.enabled = false;
            }

            if (_effectElapsed >= 1.35f && _activeSparkCount == 0)
                _playing = false;
        }

        private void UpdateRings()
        {
            switch (_resultClass)
            {
                case DiceResultClass.Positive:
                {
                    float pulseTime = Mathf.Clamp01(_effectElapsed / 0.62f);
                    float pulseEase = 1f - Mathf.Pow(1f - pulseTime, 3f);
                    SetRing(
                        _pulseTransform,
                        _pulseRenderer,
                        _pulseProperties,
                        Mathf.Lerp(0.42f, 1.34f, pulseEase),
                        new Color(1f, 0.48f, 0.08f, (1f - pulseTime) * 0.72f));

                    float haloTime = Mathf.Clamp01(_effectElapsed / 1.05f);
                    if (_haloTransform != null)
                    {
                        _haloTransform.localScale = Vector3.one * Mathf.Lerp(0.70f, 1.14f, haloTime);
                        _haloTransform.localRotation = Quaternion.Euler(0f, 0f, haloTime * 34f);
                    }
                    SetTint(
                        _haloRenderer,
                        _haloProperties,
                        new Color(1f, 0.69f, 0.17f, Mathf.Sin(haloTime * Mathf.PI) * 0.66f));
                    break;
                }
                case DiceResultClass.Negative:
                {
                    float pulseTime = Mathf.Clamp01(_effectElapsed / 0.72f);
                    float inward = 1f - Mathf.Pow(pulseTime, 2f);
                    SetRing(
                        _pulseTransform,
                        _pulseRenderer,
                        _pulseProperties,
                        Mathf.Lerp(0.58f, 1.34f, inward),
                        new Color(0.74f, 0.015f, 0.035f, (1f - pulseTime) * 0.72f));

                    float haloTime = Mathf.Clamp01(_effectElapsed / 0.82f);
                    if (_haloTransform != null)
                    {
                        _haloTransform.localScale = Vector3.one * Mathf.Lerp(1.18f, 0.72f, haloTime);
                        _haloTransform.localRotation = Quaternion.Euler(0f, 0f, -haloTime * 22f);
                    }
                    SetTint(
                        _haloRenderer,
                        _haloProperties,
                        new Color(0.48f, 0.006f, 0.02f, Mathf.Sin(haloTime * Mathf.PI) * 0.46f));
                    break;
                }
                default:
                {
                    float pulseTime = Mathf.Clamp01(_effectElapsed / 0.52f);
                    float pulseEase = 1f - Mathf.Pow(1f - pulseTime, 3f);
                    SetRing(
                        _pulseTransform,
                        _pulseRenderer,
                        _pulseProperties,
                        Mathf.Lerp(0.64f, 1.10f, pulseEase),
                        new Color(1f, 0.54f, 0.16f, (1f - pulseTime) * 0.28f));
                    break;
                }
            }
        }

        private static void SetRing(
            Transform? ringTransform,
            MeshRenderer? renderer,
            MaterialPropertyBlock properties,
            float scale,
            Color tint)
        {
            if (ringTransform != null)
                ringTransform.localScale = Vector3.one * scale;
            SetTint(renderer, properties, tint);
        }

        private static void SetTint(
            MeshRenderer? renderer,
            MaterialPropertyBlock properties,
            Color tint)
        {
            if (renderer == null)
                return;
            properties.SetColor(TintId, tint);
            renderer.SetPropertyBlock(properties);
        }

        private void EmitPositiveSparks(uint seed)
        {
            LocalRandom random = new(seed ^ 0x9e3779b9u);
            const int count = 20;
            for (int i = 0; i < count; i++)
            {
                float angle = random.Range(0f, Mathf.PI * 2f);
                float radius = random.Range(0.18f, 0.78f);
                Vector2 radial = new(Mathf.Cos(angle), Mathf.Sin(angle));
                _sparks[i] = new Spark(
                    new Vector3(radial.x * radius, radial.y * radius - 0.12f, -0.025f),
                    new Vector3(radial.x * random.Range(0.08f, 0.25f), random.Range(0.72f, 1.42f), 0f),
                    random.Range(0.045f, 0.095f),
                    random.Range(0.58f, 1.04f),
                    random.Range(-150f, 150f),
                    new Color(1f, random.Range(0.42f, 0.76f), 0.08f, 1f));
            }

            _activeSparkCount = count;
            if (_sparkRenderer != null)
                _sparkRenderer.enabled = true;
        }

        private void EmitNegativeMotes(uint seed)
        {
            LocalRandom random = new(seed ^ 0x85ebca6bu);
            const int count = 16;
            for (int i = 0; i < count; i++)
            {
                float angle = random.Range(0f, Mathf.PI * 2f);
                Vector2 radial = new(Mathf.Cos(angle), Mathf.Sin(angle));
                float radius = random.Range(0.76f, 1.04f);
                _sparks[i] = new Spark(
                    new Vector3(radial.x * radius, radial.y * radius + 0.08f, -0.02f),
                    new Vector3(
                        -radial.x * random.Range(0.34f, 0.64f),
                        -radial.y * random.Range(0.24f, 0.48f) - random.Range(0.20f, 0.46f),
                        0f),
                    random.Range(0.055f, 0.105f),
                    random.Range(0.54f, 0.86f),
                    random.Range(-100f, 100f),
                    new Color(random.Range(0.55f, 0.88f), 0.008f, 0.025f, 0.86f));
            }

            _activeSparkCount = count;
            if (_sparkRenderer != null)
                _sparkRenderer.enabled = true;
        }

        private void UpdateSparks(float deltaTime)
        {
            if (_sparkMesh == null || _activeSparkCount == 0)
                return;

            int writeIndex = 0;
            for (int i = 0; i < _activeSparkCount; i++)
            {
                Spark spark = _sparks[i];
                spark.Age += deltaTime;
                if (spark.Age >= spark.Lifetime)
                    continue;

                spark.Position += spark.Velocity * deltaTime;
                spark.Velocity *= Mathf.Pow(0.28f, deltaTime);
                spark.Rotation += spark.AngularVelocity * deltaTime;
                _sparks[writeIndex++] = spark;
            }

            _activeSparkCount = writeIndex;
            _sparkMesh.Clear(keepVertexLayout: false);
            if (_activeSparkCount == 0)
            {
                if (_sparkRenderer != null)
                    _sparkRenderer.enabled = false;
                return;
            }

            int vertexCount = _activeSparkCount * 4;
            for (int i = 0; i < _activeSparkCount; i++)
            {
                Spark spark = _sparks[i];
                float life = Mathf.Clamp01(spark.Age / spark.Lifetime);
                float alpha = Mathf.Sin(life * Mathf.PI) * spark.Color.a;
                Color color = new(spark.Color.r, spark.Color.g, spark.Color.b, alpha);
                float halfWidth = spark.Size * Mathf.Lerp(0.38f, 0.12f, life);
                float halfHeight = spark.Size * Mathf.Lerp(1.25f, 0.55f, life);
                Quaternion rotation = Quaternion.Euler(0f, 0f, spark.Rotation);
                Vector3 right = rotation * new Vector3(halfWidth, 0f, 0f);
                Vector3 up = rotation * new Vector3(0f, halfHeight, 0f);
                int vertex = i * 4;
                _sparkVertices[vertex] = spark.Position - right - up;
                _sparkVertices[vertex + 1] = spark.Position + right - up;
                _sparkVertices[vertex + 2] = spark.Position + right + up;
                _sparkVertices[vertex + 3] = spark.Position - right + up;
                _sparkColors[vertex] = color;
                _sparkColors[vertex + 1] = color;
                _sparkColors[vertex + 2] = color;
                _sparkColors[vertex + 3] = color;
            }

            _sparkMesh.SetVertices(_sparkVertices, 0, vertexCount);
            _sparkMesh.SetColors(_sparkColors, 0, vertexCount);
            _sparkMesh.SetTriangles(_sparkTriangles, 0, _activeSparkCount * 6, 0);
            _sparkMesh.RecalculateBounds();
        }

        private void ClearSparks()
        {
            _activeSparkCount = 0;
            if (_sparkMesh != null)
                _sparkMesh.Clear(keepVertexLayout: false);
            if (_sparkRenderer != null)
                _sparkRenderer.enabled = false;
        }

        private (Transform transform, MeshRenderer renderer) CreateRenderer(string name, Mesh mesh)
        {
            GameObject rendererObject = new(name);
            rendererObject.layer = _overlayLayer;
            Transform rendererTransform = rendererObject.transform;
            rendererTransform.SetParent(_effectRoot, false);
            MeshFilter filter = rendererObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = rendererObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return (rendererTransform, renderer);
        }

        private static Mesh BuildAnnulusMesh(string name, float innerRadius, float outerRadius, int segments)
        {
            Vector3[] vertices = new Vector3[segments * 2];
            Color[] colors = new Color[vertices.Length];
            int[] triangles = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                Vector3 radial = new(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                vertices[i * 2] = radial * innerRadius;
                vertices[i * 2 + 1] = radial * outerRadius;
                colors[i * 2] = Color.white;
                colors[i * 2 + 1] = Color.white;

                int next = (i + 1) % segments;
                int triangle = i * 6;
                triangles[triangle] = i * 2;
                triangles[triangle + 1] = next * 2 + 1;
                triangles[triangle + 2] = i * 2 + 1;
                triangles[triangle + 3] = i * 2;
                triangles[triangle + 4] = next * 2;
                triangles[triangle + 5] = next * 2 + 1;
            }

            Mesh mesh = new() { name = name };
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildRuneHaloMesh()
        {
            const int runeCount = 12;
            const int ringSegments = 64;
            Mesh ring = BuildAnnulusMesh("Dice Rune Halo", 0.965f, 1f, ringSegments);
            Vector3[] ringVertices = ring.vertices;
            int[] ringTriangles = ring.triangles;
            Vector3[] vertices = new Vector3[ringVertices.Length + runeCount * 4];
            Color[] colors = new Color[vertices.Length];
            int[] triangles = new int[ringTriangles.Length + runeCount * 6];
            Array.Copy(ringVertices, vertices, ringVertices.Length);
            Array.Copy(ringTriangles, triangles, ringTriangles.Length);
            for (int i = 0; i < colors.Length; i++)
                colors[i] = Color.white;

            for (int i = 0; i < runeCount; i++)
            {
                float angle = i / (float)runeCount * Mathf.PI * 2f;
                Vector3 radial = new(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                Vector3 tangent = new(-radial.y, radial.x, 0f);
                Vector3 center = radial * 1.075f;
                float length = i % 3 == 0 ? 0.13f : 0.085f;
                int vertex = ringVertices.Length + i * 4;
                vertices[vertex] = center - tangent * 0.022f - radial * length;
                vertices[vertex + 1] = center + tangent * 0.022f - radial * length;
                vertices[vertex + 2] = center + tangent * 0.022f + radial * length;
                vertices[vertex + 3] = center - tangent * 0.022f + radial * length;

                int triangle = ringTriangles.Length + i * 6;
                triangles[triangle] = vertex;
                triangles[triangle + 1] = vertex + 1;
                triangles[triangle + 2] = vertex + 2;
                triangles[triangle + 3] = vertex;
                triangles[triangle + 4] = vertex + 2;
                triangles[triangle + 5] = vertex + 3;
            }

            Mesh mesh = new() { name = "Dice Rune Halo" };
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            UnityEngine.Object.Destroy(ring);
            return mesh;
        }

        private static Mesh BuildSparkMesh()
        {
            Mesh mesh = new()
            {
                name = "Dice Result Motes",
                indexFormat = IndexFormat.UInt16
            };
            mesh.MarkDynamic();
            return mesh;
        }

        private void OnDestroy()
        {
            if (_material != null)
                Destroy(_material);
            if (_pulseMesh != null)
                Destroy(_pulseMesh);
            if (_haloMesh != null)
                Destroy(_haloMesh);
            if (_sparkMesh != null)
                Destroy(_sparkMesh);
        }

        private struct Spark
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float Size;
            public float Lifetime;
            public float AngularVelocity;
            public Color Color;
            public float Rotation;
            public float Age;

            public Spark(
                Vector3 position,
                Vector3 velocity,
                float size,
                float lifetime,
                float angularVelocity,
                Color color)
            {
                Position = position;
                Velocity = velocity;
                Size = size;
                Lifetime = lifetime;
                AngularVelocity = angularVelocity;
                Color = color;
                Rotation = 0f;
                Age = 0f;
            }
        }

        private struct LocalRandom
        {
            private uint _state;

            public LocalRandom(uint seed)
            {
                _state = seed != 0 ? seed : 0x6d2b79f5u;
            }

            public float Range(float minimum, float maximum)
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return Mathf.Lerp(minimum, maximum, (_state & 0x00ffffffu) / 16777215f);
            }
        }
    }
}
