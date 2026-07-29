#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Presentation-only afterimage layer for melee animations that get visually preempted.
    /// Option 1: bake the current renderer pose into static meshes and fade them out.
    /// </summary>
    public sealed class MeleeAnimationGhostLayer : MonoBehaviour
    {
        private const string GhostRootName = "InterruptedMeleeGhost";
        private const float DefaultFadeSeconds = 0.75f;
        private const float DefaultStartAlpha = 0.60f;
        private const int DefaultMaxGhosts = 3;

        [SerializeField] private bool enabledForInterruptedMelee = true;
        [SerializeField] [Range(0.05f, 1.5f)] private float fadeSeconds = DefaultFadeSeconds;
        [SerializeField] [Range(0.02f, 0.8f)] private float startAlpha = DefaultStartAlpha;
        [SerializeField] [Range(1, 8)] private int maxGhosts = DefaultMaxGhosts;
        [SerializeField] private Color tint = new(0.45f, 0.95f, 1f, 1f);

        private readonly List<GhostInstance> _activeGhosts = new();
        private readonly List<Renderer> _rendererScratch = new();
        private Transform? _sourceRootOverride;

        public void SetSourceRoot(Transform? sourceRoot)
        {
            _sourceRootOverride = sourceRoot;
        }

        public void CaptureFrozenPose()
        {
            if (!enabledForInterruptedMelee || !isActiveAndEnabled)
                return;

            Transform sourceRoot = ResolveSourceRoot();
            if (!TryCreateGhost(sourceRoot, sourceRoot.rotation, useSourceRootLocalSpace: false, out GhostInstance ghost))
                return;

            _activeGhosts.Add(ghost);
            TrimOldestGhosts();
        }

        public void CaptureFrozenPoseFacing(Vector3 targetPoint)
        {
            if (!enabledForInterruptedMelee || !isActiveAndEnabled)
                return;

            Transform sourceRoot = ResolveSourceRoot();
            if (!TryResolveFacing(sourceRoot.position, targetPoint, out Quaternion facing))
                return;

            if (!TryCreateGhost(sourceRoot, facing, useSourceRootLocalSpace: true, out GhostInstance ghost))
                return;

            _activeGhosts.Add(ghost);
            TrimOldestGhosts();
        }

        private void LateUpdate()
        {
            for (int i = _activeGhosts.Count - 1; i >= 0; i--)
            {
                GhostInstance ghost = _activeGhosts[i];
                ghost.Elapsed += Time.deltaTime;

                float fadeDuration = Mathf.Max(0.01f, ghost.FadeSeconds);
                float alpha = ghost.StartAlpha * Mathf.Clamp01(1f - (ghost.Elapsed / fadeDuration));
                ghost.ApplyAlpha(alpha);

                if (ghost.Elapsed >= fadeDuration)
                {
                    ghost.Destroy();
                    _activeGhosts.RemoveAt(i);
                    continue;
                }

                _activeGhosts[i] = ghost;
            }
        }

        private void OnDisable()
        {
            ClearGhosts();
        }

        private void OnDestroy()
        {
            ClearGhosts();
        }

        private Transform ResolveSourceRoot()
        {
            return _sourceRootOverride != null ? _sourceRootOverride : transform;
        }

        private bool TryCreateGhost(
            Transform sourceRoot,
            Quaternion rootRotation,
            bool useSourceRootLocalSpace,
            out GhostInstance ghost)
        {
            ghost = new GhostInstance(fadeSeconds, startAlpha);
            GameObject root = new(GhostRootName);
            Transform ghostRoot = root.transform;
            ghostRoot.SetPositionAndRotation(sourceRoot.position, rootRotation);
            ghostRoot.localScale = Vector3.one;
            ghost.Root = root;

            _rendererScratch.Clear();
            sourceRoot.GetComponentsInChildren(includeInactive: false, _rendererScratch);

            foreach (Renderer renderer in _rendererScratch)
            {
                if (renderer == null)
                    continue;
                if (IsRuntimeOverlayRenderer(renderer))
                    continue;

                switch (renderer)
                {
                    case SkinnedMeshRenderer skinned:
                        CaptureSkinnedRenderer(skinned, sourceRoot, ghostRoot, ghost, useSourceRootLocalSpace);
                        break;
                    case MeshRenderer meshRenderer:
                        CaptureMeshRenderer(meshRenderer, sourceRoot, ghostRoot, ghost, useSourceRootLocalSpace);
                        break;
                }
            }

            int discoveredRendererCount = _rendererScratch.Count;
            _rendererScratch.Clear();
            if (ghost.RendererCount > 0)
                return true;

            Debug.LogWarning(
                $"[{nameof(MeleeAnimationGhostLayer)}] Capture requested on {name}, found {discoveredRendererCount} renderers under {sourceRoot.name}, but captured none.");
            ghost.Destroy();
            return false;
        }

        private void CaptureSkinnedRenderer(
            SkinnedMeshRenderer source,
            Transform sourceRoot,
            Transform ghostRoot,
            GhostInstance ghost,
            bool useSourceRootLocalSpace)
        {
            if (source.sharedMesh == null || !source.enabled)
                return;

            Mesh bakedMesh = new();
            source.BakeMesh(bakedMesh, useScale: false);
            bakedMesh.name = $"{source.sharedMesh.name}_ghost_pose";

            GameObject child = useSourceRootLocalSpace
                ? CreateGhostChildFromSourceRootLocal(source.transform, sourceRoot, ghostRoot)
                : CreateGhostChildWorldLocked(source.transform, ghostRoot);
            child.AddComponent<MeshFilter>().sharedMesh = bakedMesh;
            var ghostRenderer = child.AddComponent<MeshRenderer>();
            ghostRenderer.sharedMaterials = CreateGhostMaterials(source.sharedMaterials);
            ghostRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ghostRenderer.receiveShadows = false;

            ghost.Add(child, bakedMesh, ghostRenderer.sharedMaterials);
        }

        private void CaptureMeshRenderer(
            MeshRenderer source,
            Transform sourceRoot,
            Transform ghostRoot,
            GhostInstance ghost,
            bool useSourceRootLocalSpace)
        {
            if (!source.enabled || !source.TryGetComponent(out MeshFilter sourceFilter) || sourceFilter.sharedMesh == null)
                return;

            GameObject child = useSourceRootLocalSpace
                ? CreateGhostChildFromSourceRootLocal(source.transform, sourceRoot, ghostRoot)
                : CreateGhostChildWorldLocked(source.transform, ghostRoot);
            child.AddComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;
            var ghostRenderer = child.AddComponent<MeshRenderer>();
            ghostRenderer.sharedMaterials = CreateGhostMaterials(source.sharedMaterials);
            ghostRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ghostRenderer.receiveShadows = false;

            ghost.Add(child, null, ghostRenderer.sharedMaterials);
        }

        private static GameObject CreateGhostChildWorldLocked(Transform source, Transform ghostRoot)
        {
            GameObject child = new($"{source.name}_ghost");
            Transform childTransform = child.transform;
            childTransform.SetPositionAndRotation(source.position, source.rotation);
            childTransform.localScale = source.lossyScale;
            childTransform.SetParent(ghostRoot, worldPositionStays: true);
            return child;
        }

        private static GameObject CreateGhostChildFromSourceRootLocal(
            Transform source,
            Transform sourceRoot,
            Transform ghostRoot)
        {
            GameObject child = new($"{source.name}_ghost");
            Transform childTransform = child.transform;
            childTransform.SetParent(ghostRoot, worldPositionStays: false);
            childTransform.localPosition = sourceRoot.InverseTransformPoint(source.position);
            childTransform.localRotation = Quaternion.Inverse(sourceRoot.rotation) * source.rotation;
            childTransform.localScale = source.lossyScale;
            return child;
        }

        private static bool TryResolveFacing(Vector3 origin, Vector3 targetPoint, out Quaternion rotation)
        {
            Vector3 toTarget = targetPoint - origin;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f)
            {
                rotation = Quaternion.identity;
                return false;
            }

            rotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
            return true;
        }

        private static bool IsRuntimeOverlayRenderer(Renderer renderer)
        {
            Transform? current = renderer.transform;
            while (current != null)
            {
                if (current.name == "NameTag" || current.name == "WorldHealthBar")
                    return true;

                current = current.parent;
            }

            return false;
        }

        private Material[] CreateGhostMaterials(Material[] sourceMaterials)
        {
            if (sourceMaterials == null || sourceMaterials.Length == 0)
                return new[] { CreateGhostMaterial(null) };

            Material[] materials = new Material[sourceMaterials.Length];
            for (int i = 0; i < sourceMaterials.Length; i++)
                materials[i] = CreateGhostMaterial(sourceMaterials[i]);
            return materials;
        }

        private Material CreateGhostMaterial(Material? source)
        {
            Material material = source != null
                ? new Material(source)
                : new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));

            material.name = $"{(source != null ? source.name : "Default")}_interrupted_melee_ghost";
            ConfigureTransparentMaterial(material);
            ApplyMaterialAlpha(material, startAlpha);
            return material;
        }

        private void ConfigureTransparentMaterial(Material material)
        {
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            if (material.HasProperty("_Surface"))
                material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend"))
                material.SetFloat("_Blend", 0f);
            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 0f);

            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ALPHABLEND_ON");
        }

        private void ApplyMaterialAlpha(Material material, float alpha)
        {
            Color color = tint;
            if (material.HasProperty("_BaseColor"))
                color *= material.GetColor("_BaseColor");
            else if (material.HasProperty("_Color"))
                color *= material.GetColor("_Color");

            color.a = Mathf.Clamp01(alpha);

            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
        }

        private void TrimOldestGhosts()
        {
            int capacity = Mathf.Max(1, maxGhosts);
            while (_activeGhosts.Count > capacity)
            {
                GhostInstance oldest = _activeGhosts[0];
                oldest.Destroy();
                _activeGhosts.RemoveAt(0);
            }
        }

        private void ClearGhosts()
        {
            foreach (GhostInstance ghost in _activeGhosts)
                ghost.Destroy();
            _activeGhosts.Clear();
        }

        private sealed class GhostInstance
        {
            public GameObject? Root;
            public float Elapsed;
            public readonly float FadeSeconds;
            public readonly float StartAlpha;
            private readonly List<Object> _ownedObjects;
            private readonly List<Material> _materials;

            public GhostInstance(float fadeSeconds, float startAlpha)
            {
                Root = null;
                Elapsed = 0f;
                FadeSeconds = fadeSeconds;
                StartAlpha = startAlpha;
                _ownedObjects = new List<Object>();
                _materials = new List<Material>();
                RendererCount = 0;
            }

            public int RendererCount { get; private set; }

            public void Add(GameObject gameObject, Mesh? ownedMesh, Material[] materials)
            {
                RendererCount++;
                _ownedObjects.Add(gameObject);
                if (ownedMesh != null)
                    _ownedObjects.Add(ownedMesh);

                foreach (Material material in materials)
                {
                    if (material == null)
                        continue;
                    _materials.Add(material);
                    _ownedObjects.Add(material);
                }
            }

            public void ApplyAlpha(float alpha)
            {
                for (int i = 0; i < _materials.Count; i++)
                {
                    Material material = _materials[i];
                    Color color = material.HasProperty("_BaseColor")
                        ? material.GetColor("_BaseColor")
                        : material.HasProperty("_Color")
                            ? material.GetColor("_Color")
                            : Color.white;
                    color.a = Mathf.Clamp01(alpha);

                    if (material.HasProperty("_BaseColor"))
                        material.SetColor("_BaseColor", color);
                    if (material.HasProperty("_Color"))
                        material.SetColor("_Color", color);
                }
            }

            public void Destroy()
            {
                if (Root != null)
                    Object.Destroy(Root);

                for (int i = 0; i < _ownedObjects.Count; i++)
                {
                    Object owned = _ownedObjects[i];
                    if (owned != null)
                        Object.Destroy(owned);
                }

                _ownedObjects.Clear();
                _materials.Clear();
                Root = null;
                RendererCount = 0;
            }
        }
    }
}
