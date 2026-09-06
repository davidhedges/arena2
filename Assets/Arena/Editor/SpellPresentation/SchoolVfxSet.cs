#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// One slot's generated look. Prefab and scale are resolved from the runtime registry in the
    /// inspector; palettes and spell overrides never serialize another copy of those bindings.
    /// </summary>
    [Serializable]
    public struct SchoolVfxSlotEntry
    {
        public SpellVfxSlot slot;
        [Tooltip("Stable variant id for repeatable slots such as CharacterFx (for example BODY_RINGS or SHOULDER_FLAMES). Leave blank for non-repeatable slots or a single CharacterFx entry.")]
        public string variantId;
        [Tooltip("Catalog vfx_id this slot resolves to, e.g. VFX_FIRE_CAST_HAND_01.")]
        public string vfxId;
        [Tooltip("The prefab is a self-ending particle system → PARTICLE_SYSTEM lifecycle (duration ignored).")]
        public bool selfTerminating;
        [Tooltip("Concrete duration for a ONE_SHOT/DURATION slot (must be > 0 when not self-terminating).")]
        public int durationMs;
    }

    [CustomPropertyDrawer(typeof(SchoolVfxSlotEntry))]
    internal sealed class SchoolVfxSlotEntryDrawer : PropertyDrawer
    {
        private static readonly string[] AuthoredFields = { "slot", "variantId", "vfxId", "selfTerminating", "durationMs" };
        private static float RowHeight => EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => property.isExpanded ? RowHeight * (AuthoredFields.Length + 3) : RowHeight;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            property.isExpanded = EditorGUI.Foldout(row, property.isExpanded, label, true);
            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                foreach (string field in AuthoredFields)
                {
                    row.y += RowHeight;
                    EditorGUI.PropertyField(row, property.FindPropertyRelative(field));
                }
                string id = property.FindPropertyRelative("vfxId").stringValue;
                bool scripted = CombatVFXTemplateRegistry.IsScriptedTemplate(id);
                var template = scripted ? null : CombatVFXTemplateRegistry.ResolveTemplate(id);
                row.y += RowHeight;
                using (new EditorGUI.DisabledScope(true))
                {
                    if (scripted)
                        EditorGUI.LabelField(row, "Runtime binding", "Scripted effect");
                    else
                        EditorGUI.ObjectField(row, "Runtime prefab", template?.Prefab, typeof(GameObject), false);
                    row.y += RowHeight;
                    if (scripted)
                        EditorGUI.LabelField(row, "Runtime scale", "Owned by scripted effect");
                    else if (template != null)
                        EditorGUI.FloatField(row, "Runtime scale", template.Scale);
                    else
                        EditorGUI.LabelField(row, "Runtime binding", "No registry binding resolved");
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }
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
        [Tooltip("School id — FIRE / COLD / AIR / LIGHTNING / ARCANE / HOLY / SHADOW / VOID / DARK / …")]
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
