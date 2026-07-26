#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace Arena.Interaction
{
    [DisallowMultipleComponent]
    public sealed class WorldInteractionHitbox : MonoBehaviour
    {
        private static readonly List<WorldInteractionHitbox> Registered = new();
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId =
            Shader.PropertyToID("_EmissionColor");
        private static readonly Color HoverTint = new(1f, 0.72f, 0.2f, 1f);
        private static readonly Color HoverEmission =
            new(0.16f, 0.085f, 0.015f, 1f);
        private const string EmissionKeyword = "_EMISSION";

        [SerializeField] private MonoBehaviour? _interactableSource;
        [SerializeField] private Collider? _targetCollider;

        private readonly List<HighlightMaterialState> _highlightStates = new();
        private bool _hovered;
        private int _highlightSlotCount;

        public IWorldInteractable? Interactable => _interactableSource as IWorldInteractable;
        internal static IReadOnlyList<WorldInteractionHitbox> ActiveHitboxes
        {
            get
            {
                for (int i = Registered.Count - 1; i >= 0; i--)
                {
                    if (Registered[i] == null || !Registered[i].isActiveAndEnabled)
                        Registered.RemoveAt(i);
                }

                if (Registered.Count == 0)
                {
                    foreach (WorldInteractionHitbox hitbox in
                             FindObjectsByType<WorldInteractionHitbox>(
                                 FindObjectsInactive.Exclude))
                    {
                        if (hitbox != null && !Registered.Contains(hitbox))
                            Registered.Add(hitbox);
                    }
                }

                return Registered;
            }
        }
        internal Collider? TargetCollider => _targetCollider != null
            ? _targetCollider
            : _targetCollider = GetComponent<Collider>();
        internal Transform? InteractableRoot =>
            (_interactableSource as Component)?.transform;
        internal bool IsHovered => _hovered;
        internal int HighlightSlotCount => _highlightSlotCount;

        public void Configure(MonoBehaviour interactableSource)
        {
            SetHovered(false);
            _interactableSource = interactableSource;
            _targetCollider ??= GetComponent<Collider>();
        }

        private void Reset()
        {
            _targetCollider = GetComponent<Collider>();
            ResolveFromParents();
        }

        private void OnEnable()
        {
            if (!Registered.Contains(this))
                Registered.Add(this);
            Debug.Log(
                $"[WorldInteraction] hitbox enabled object='{name}' "
                + $"source={DescribeSource()} collider={DescribeCollider()}.",
                this);
        }

        private void OnDisable()
        {
            Debug.Log(
                $"[WorldInteraction] hitbox disabled object='{name}' "
                + $"source={DescribeSource()}.",
                this);
            SetHovered(false);
            Registered.Remove(this);
        }

        private void OnValidate()
        {
            _targetCollider ??= GetComponent<Collider>();
            if (_interactableSource != null && _interactableSource is not IWorldInteractable)
            {
                Debug.LogError(
                    $"{name}: assigned source must implement {nameof(IWorldInteractable)}.",
                    this);
            }

            if (_interactableSource == null)
                ResolveFromParents();
        }

        private string DescribeSource()
        {
            if (_interactableSource == null)
                return "<null>";
            if (_interactableSource is not IWorldInteractable interactable)
                return $"'{_interactableSource.GetType().FullName}' (not interactable)";
            return $"'{interactable.StableInteractionId}' "
                + $"({_interactableSource.GetType().Name})";
        }

        private string DescribeCollider()
        {
            Collider? collider = TargetCollider;
            return collider == null
                ? "<null>"
                : $"'{collider.GetType().Name}' enabled={collider.enabled}";
        }

        private void ResolveFromParents()
        {
            MonoBehaviour[] behaviours = GetComponentsInParent<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is IWorldInteractable)
                {
                    _interactableSource = behaviours[i];
                    return;
                }
            }
        }

        internal void SetHovered(bool hovered)
        {
            if (_hovered == hovered)
                return;

            RestoreHighlight();
            _hovered = hovered;
            if (!_hovered)
                return;

            var renderers = new List<Renderer>();
            if (Interactable is IWorldInteractionHighlightSource highlightSource)
            {
                highlightSource.CollectHighlightRenderers(renderers);
            }
            else if (_interactableSource is Component source)
            {
                renderers.AddRange(source.GetComponentsInChildren<Renderer>(true));
            }

            var seen = new HashSet<Renderer>();
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null
                    || !seen.Add(renderer)
                    || renderer is not (MeshRenderer or SkinnedMeshRenderer))
                {
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                Material[] highlightedMaterials =
                    (Material[])materials.Clone();
                var temporaryMaterials = new List<Material>();
                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    Material? material = materials[materialIndex];
                    if (material == null)
                        continue;

                    int colorProperty = material.HasProperty(BaseColorId)
                        ? BaseColorId
                        : material.HasProperty(ColorId)
                            ? ColorId
                            : -1;
                    bool supportsEmission = material.HasProperty(EmissionColorId);
                    if (colorProperty < 0 && !supportsEmission)
                        continue;

                    var highlighted = new Material(material)
                    {
                        name = $"{material.name} (Interaction Hover)",
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    if (colorProperty >= 0)
                    {
                        Color baseColor = material.GetColor(colorProperty);
                        Color tint = HoverTint;
                        tint.a = baseColor.a;
                        highlighted.SetColor(
                            colorProperty,
                            Color.Lerp(baseColor, tint, 0.38f));
                    }
                    if (supportsEmission)
                    {
                        Color emission = material.GetColor(EmissionColorId)
                            + HoverEmission;
                        emission.a = 1f;
                        highlighted.SetColor(EmissionColorId, emission);
                        highlighted.EnableKeyword(EmissionKeyword);
                    }

                    highlightedMaterials[materialIndex] = highlighted;
                    temporaryMaterials.Add(highlighted);
                    _highlightSlotCount++;
                }

                if (temporaryMaterials.Count > 0)
                {
                    renderer.sharedMaterials = highlightedMaterials;
                    _highlightStates.Add(new HighlightMaterialState(
                        renderer,
                        materials,
                        temporaryMaterials.ToArray()));
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry()
        {
            Registered.Clear();
        }

        private void RestoreHighlight()
        {
            foreach (HighlightMaterialState state in _highlightStates)
            {
                if (state.Renderer != null)
                    state.Renderer.sharedMaterials = state.OriginalMaterials;

                foreach (Material temporary in state.TemporaryMaterials)
                    DestroyTemporaryMaterial(temporary);
            }
            _highlightStates.Clear();
            _highlightSlotCount = 0;
        }

        private static void DestroyTemporaryMaterial(Material material)
        {
            if (material == null)
                return;
            if (Application.isPlaying)
                Destroy(material);
            else
                DestroyImmediate(material);
        }

        private readonly struct HighlightMaterialState
        {
            public HighlightMaterialState(
                Renderer renderer,
                Material[] originalMaterials,
                Material[] temporaryMaterials)
            {
                Renderer = renderer;
                OriginalMaterials = originalMaterials;
                TemporaryMaterials = temporaryMaterials;
            }

            public Renderer Renderer { get; }
            public Material[] OriginalMaterials { get; }
            public Material[] TemporaryMaterials { get; }
        }
    }
}
