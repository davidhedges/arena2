#nullable enable
using System;
using UnityEngine;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Shared VFX utility methods for creating procedural meshes,
    /// glow textures, and common materials.
    /// </summary>
    public static class VFXUtils
    {
        private static Texture2D? _glowTexture128;
        private static Texture2D? _glowTexture96;
        private static Shader? _additiveGlowShader;
        private static Shader? _beamShader;
        private static Shader? _fireballShader;
        private static Shader? _shieldShader;
        private static Shader? _negateDomeShader;

        public static void ApplyPrefabPresentationScale(GameObject instance, float scale)
        {
            float normalizedScale = scale > 0f ? scale : 1f;
            if (Mathf.Approximately(normalizedScale, 1f))
                return;

            instance.transform.localScale *= normalizedScale;

            ParticleSystem[] particleSystems = instance.GetComponentsInChildren<ParticleSystem>(true);
            for (int index = 0; index < particleSystems.Length; index++)
            {
                ParticleSystem particleSystem = particleSystems[index];
                var main = particleSystem.main;
                if (main.scalingMode == ParticleSystemScalingMode.Local)
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
        }

        /// <summary>
        /// Procedural radial gradient glow texture matching TS createGlowTexture().
        /// </summary>
        public static Texture2D GetGlowTexture(int size)
        {
            if (size >= 128)
            {
                if (_glowTexture128 != null) return _glowTexture128;
                _glowTexture128 = CreateGlowTexture(128);
                return _glowTexture128;
            }
            if (_glowTexture96 != null) return _glowTexture96;
            _glowTexture96 = CreateGlowTexture(96);
            return _glowTexture96;
        }

        private static Texture2D CreateGlowTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center) / center;
                    float dy = (y - center) / center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    // Match TS gradient stops: 0→white, 0.4→warm, 0.75→orange, 1→transparent
                    float r, g, b, a;
                    if (dist <= 0.4f)
                    {
                        float t = dist / 0.4f;
                        r = Mathf.Lerp(1f, 1f, t);
                        g = Mathf.Lerp(1f, 0.824f, t);
                        b = Mathf.Lerp(1f, 0.549f, t);
                        a = Mathf.Lerp(1f, 0.9f, t);
                    }
                    else if (dist <= 0.75f)
                    {
                        float t = (dist - 0.4f) / 0.35f;
                        r = Mathf.Lerp(1f, 1f, t);
                        g = Mathf.Lerp(0.824f, 0.627f, t);
                        b = Mathf.Lerp(0.549f, 0.314f, t);
                        a = Mathf.Lerp(0.9f, 0.35f, t);
                    }
                    else
                    {
                        float t = (dist - 0.75f) / 0.25f;
                        r = Mathf.Lerp(1f, 1f, t);
                        g = Mathf.Lerp(0.627f, 0.471f, t);
                        b = Mathf.Lerp(0.314f, 0.118f, t);
                        a = Mathf.Lerp(0.35f, 0f, t);
                    }

                    tex.SetPixel(x, y, new Color(r, g, b, a));
                }
            }
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        public static Shader GetAdditiveGlowShader()
        {
            if (_additiveGlowShader == null)
                _additiveGlowShader = FindFirstShader(
                    "Arena/AdditiveGlow",
                    "Particles/Standard Unlit",
                    "Sprites/Default",
                    "Unlit/Color",
                    "Universal Render Pipeline/Unlit",
                    "Standard");
            return _additiveGlowShader;
        }

        public static Shader GetBeamShader()
        {
            if (_beamShader == null)
                _beamShader = FindFirstShader(
                    "Arena/Beam",
                    "Particles/Standard Unlit",
                    "Sprites/Default",
                    "Unlit/Color",
                    "Universal Render Pipeline/Unlit",
                    "Standard");
            return _beamShader;
        }

        public static Shader GetFireballShader()
        {
            if (_fireballShader == null)
                _fireballShader = FindFirstShader(
                    "Arena/Fireball",
                    "Particles/Standard Unlit",
                    "Sprites/Default",
                    "Unlit/Color",
                    "Universal Render Pipeline/Unlit",
                    "Standard");
            return _fireballShader;
        }

        public static Shader GetShieldShader()
        {
            if (_shieldShader == null)
                _shieldShader = FindFirstShader(
                    "Arena/Shield",
                    "Universal Render Pipeline/Unlit",
                    "Sprites/Default",
                    "Unlit/Color",
                    "Standard");
            return _shieldShader;
        }

        public static Shader GetNegateDomeShader()
        {
            if (_negateDomeShader == null)
                _negateDomeShader = FindFirstShader(
                    "Arena/NegateDome",
                    "Universal Render Pipeline/Unlit",
                    "Sprites/Default",
                    "Unlit/Color",
                    "Standard");
            return _negateDomeShader;
        }

        private static Shader FindFirstShader(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Shader shader = Shader.Find(names[i]);
                if (shader != null)
                    return shader;
            }

            throw new InvalidOperationException("No fallback shader available for VFX.");
        }

        /// <summary>
        /// Creates a billboard quad that always faces the camera.
        /// </summary>
        public static GameObject CreateBillboardQuad(string name, Material material, float size)
        {
            var go = new GameObject(name);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.material = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mf.mesh = CreateQuadMesh();
            go.transform.localScale = Vector3.one * size;
            go.AddComponent<BillboardBehavior>();
            return go;
        }

        /// <summary>
        /// Creates a flat ring mesh for impact effects.
        /// </summary>
        public static Mesh CreateRingMesh(float innerRadius, float outerRadius, int segments = 32)
        {
            var mesh = new Mesh();
            int vertCount = (segments + 1) * 2;
            var vertices = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var triangles = new int[segments * 6];

            for (int i = 0; i <= segments; i++)
            {
                float angle = (float)i / segments * Mathf.PI * 2f;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);
                float u = (float)i / segments;

                vertices[i * 2] = new Vector3(cos * innerRadius, 0, sin * innerRadius);
                vertices[i * 2 + 1] = new Vector3(cos * outerRadius, 0, sin * outerRadius);
                uvs[i * 2] = new Vector2(u, 0);
                uvs[i * 2 + 1] = new Vector2(u, 1);
            }

            for (int i = 0; i < segments; i++)
            {
                int idx = i * 6;
                int v = i * 2;
                triangles[idx] = v;
                triangles[idx + 1] = v + 2;
                triangles[idx + 2] = v + 1;
                triangles[idx + 3] = v + 1;
                triangles[idx + 4] = v + 2;
                triangles[idx + 5] = v + 3;
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>
        /// Creates a hemisphere/dome mesh for negate VFX.
        /// </summary>
        public static Mesh CreateDomeMesh(float radius, float height, int segments = 24, int rings = 12)
        {
            var mesh = new Mesh();
            int vertCount = (segments + 1) * (rings + 1);
            var vertices = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];

            for (int r = 0; r <= rings; r++)
            {
                float phi = (float)r / rings * Mathf.PI * 0.5f; // 0 to PI/2 for hemisphere
                float y = Mathf.Sin(phi) * height;
                float ringRadius = Mathf.Cos(phi) * radius;

                for (int s = 0; s <= segments; s++)
                {
                    float theta = (float)s / segments * Mathf.PI * 2f;
                    int idx = r * (segments + 1) + s;

                    vertices[idx] = new Vector3(
                        Mathf.Cos(theta) * ringRadius,
                        y,
                        Mathf.Sin(theta) * ringRadius
                    );
                    normals[idx] = vertices[idx].normalized;
                    uvs[idx] = new Vector2((float)s / segments, (float)r / rings);
                }
            }

            int triCount = segments * rings * 6;
            var triangles = new int[triCount];
            int ti = 0;
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int v = r * (segments + 1) + s;
                    triangles[ti++] = v;
                    triangles[ti++] = v + segments + 1;
                    triangles[ti++] = v + 1;
                    triangles[ti++] = v + 1;
                    triangles[ti++] = v + segments + 1;
                    triangles[ti++] = v + segments + 2;
                }
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            return mesh;
        }

        /// <summary>
        /// When the actual VFX spawn position differs from the server's origin
        /// (e.g., hand bone socket vs server auth position), recompute the
        /// direction so the projectile heads toward the intended target line.
        /// </summary>
        public static Vector3 CorrectedDirection(
            Vector3 actualSpawn,
            Vector3 serverOrigin,
            Vector3 serverDirection,
            float maxDistance)
        {
            float dist = maxDistance > 0f ? maxDistance : 40f;
            Vector3 targetPoint = serverOrigin + serverDirection * dist;
            Vector3 corrected = (targetPoint - actualSpawn).normalized;
            return corrected.sqrMagnitude > 0.0001f ? corrected : serverDirection;
        }

        private static Mesh CreateQuadMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0),
                new Vector3(0.5f, -0.5f, 0),
                new Vector3(0.5f, 0.5f, 0),
                new Vector3(-0.5f, 0.5f, 0),
            };
            mesh.uv = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(1, 1), new Vector2(0, 1),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>
        /// Half-life-based exponential smoothing matching TS reconcileToAuthoritativePosition.
        /// </summary>
        public static float ExponentialAlpha(float dt, float halfLifeSeconds)
        {
            return 1f - Mathf.Pow(2f, -dt / halfLifeSeconds);
        }

        /// <summary>
        /// Legacy spawn-origin helper for unmigrated VFX classes.
        /// Migrated spells resolve anchors through CombatVFXAnchorResolver.
        /// </summary>
        public static Vector3 ResolveSpawnOrigin(SpacetimeDB.Types.CombatEvent castEvent)
        {
            return new Vector3(castEvent.OriginX, castEvent.OriginY, castEvent.OriginZ);
        }

    }

    /// <summary>
    /// Makes a quad always face the camera.
    /// </summary>
    public class BillboardBehavior : MonoBehaviour
    {
        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null)
                transform.rotation = cam.transform.rotation;
        }
    }
}
