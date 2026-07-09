#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// One slot's look in a per-school VFX set. Merges the palette (design doc §3.1 —
    /// <c>vfx_id</c> + duration + self-terminating) with the registry data (<c>prefab</c> + <c>scale</c>)
    /// per decision 10, so a school's whole VFX identity lives in one editable asset. The cue generator
    /// reads <see cref="vfxId"/>/<see cref="durationMs"/>/<see cref="selfTerminating"/>; <see cref="prefab"/>
    /// and <see cref="scale"/> document/validate the id→prefab binding the runtime registry still owns.
    /// </summary>
    [Serializable]
    public struct SchoolVfxSlotEntry
    {
        public SpellVfxSlot slot;
        [Tooltip("Catalog vfx_id this slot resolves to, e.g. VFX_FIRE_CAST_HAND_01.")]
        public string vfxId;
        [Tooltip("The prefab is a self-ending particle system → PARTICLE_SYSTEM lifecycle (duration ignored).")]
        public bool selfTerminating;
        [Tooltip("Concrete duration for a ONE_SHOT/DURATION slot (must be > 0 when not self-terminating).")]
        public int durationMs;
        [Tooltip("Registry scale (documentation/validation; the runtime CombatVFXRegistry still owns the live value).")]
        public float scale;
        [Tooltip("The prefab this vfx_id resolves to (documentation/validation; optional).")]
        public GameObject? prefab;
    }

    /// <summary>
    /// The editor-only per-school VFX set (decision 10): a school's <c>slot → look</c> palette,
    /// externalized from the hardcoded dictionaries in the spell authoring window so schools are
    /// edited as assets. The cue generator reads these as its sole school-palette source and surfaces
    /// requested slots that have no school entry or per-spell signature override.
    /// </summary>
    [CreateAssetMenu(menuName = "Arena/School VFX Set", fileName = "SchoolVfxSet")]
    public sealed class SchoolVfxSet : ScriptableObject
    {
        [Tooltip("School id — FIRE / COLD / LIGHTNING / ARCANE / HOLY / SHADOW / VOID / DARK / …")]
        public string schoolId = string.Empty;
        [SerializeField] private List<SchoolVfxSlotEntry> slots = new();

        public IReadOnlyList<SchoolVfxSlotEntry> Slots => slots;

        public string SchoolIdOrEmpty => string.IsNullOrWhiteSpace(schoolId)
            ? string.Empty
            : schoolId.Trim().ToUpperInvariant();

        public bool TryGet(SpellVfxSlot slot, out SchoolVfxSlotEntry entry)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].slot == slot && !string.IsNullOrWhiteSpace(slots[i].vfxId))
                {
                    entry = slots[i];
                    return true;
                }
            }

            entry = default;
            return false;
        }

    }
}
