#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Presentation;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>
    /// Authoring UI for <see cref="SpellCastAnimationMap"/>: pick each spell's flavor family from a
    /// dropdown of the scanned library (no typing base names) and its spell id from the animated
    /// spells, with the optional per-spell overrides inline. Makes the migration grind fast and
    /// typo-free. Falls back gracefully when the library isn't scanned yet.
    /// </summary>
    [CustomEditor(typeof(SpellCastAnimationMap))]
    public sealed class SpellCastAnimationMapEditor : UnityEditor.Editor
    {
        private string[] _baseNames = Array.Empty<string>();
        private string[] _spellIds = Array.Empty<string>();

        private void OnEnable() => RefreshChoices();

        private void RefreshChoices()
        {
            // Flavor families from the scanned library.
            SpellCastAnimationLibrary? library = SpellPresentationEditorData.FindFirstAsset<SpellCastAnimationLibrary>();
            _baseNames = library == null
                ? Array.Empty<string>()
                : library.Families.Select(f => f.BaseNameOrEmpty).Where(n => n.Length > 0)
                    .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();

            // Candidate spell ids from every CombatAnimationSet's authored spell entries, plus any
            // ids already in the map (so migrated-and-deleted spells stay pickable).
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (CombatAnimationSet set in SpellPresentationEditorData.LoadCombatAnimationSets())
            {
                if (set?.spells == null) continue;
                foreach (WeaponSpellAnimationEntry e in set.spells)
                    if (e.SpellIdOrEmpty.Length > 0) ids.Add(e.SpellIdOrEmpty);
            }
            var map = (SpellCastAnimationMap)target;
            foreach (SpellCastAnimationMap.Entry e in map.Entries)
                if (!string.IsNullOrWhiteSpace(e.spellId)) ids.Add(e.spellId.Trim().ToUpperInvariant());
            _spellIds = ids.ToArray();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Families: {_baseNames.Length}   Spell ids: {_spellIds.Length}", EditorStyles.miniLabel);
                if (GUILayout.Button("Refresh choices", GUILayout.Width(120)))
                    RefreshChoices();
            }
            if (_baseNames.Length == 0)
                EditorGUILayout.HelpBox("No families found — run Arena → Spell Animation → Rescan Cast Families first.", MessageType.Warning);

            SerializedProperty entries = serializedObject.FindProperty("entries");
            EditorGUILayout.Space();

            int removeAt = -1;
            for (int i = 0; i < entries.arraySize; i++)
            {
                SerializedProperty entry = entries.GetArrayElementAtIndex(i);
                SerializedProperty spellId = entry.FindPropertyRelative("spellId");
                SerializedProperty baseName = entry.FindPropertyRelative("baseName");

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        DrawStringDropdown("Spell", spellId, _spellIds, editable: true);
                        if (GUILayout.Button("✕", GUILayout.Width(22))) removeAt = i;
                    }
                    DrawStringDropdown("Flavor", baseName, _baseNames, editable: false);
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("playbackLayer"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("combatEntryMode"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("animatedProp"), includeChildren: true);
                }
            }

            if (removeAt >= 0)
                entries.DeleteArrayElementAtIndex(removeAt);

            EditorGUILayout.Space();
            if (GUILayout.Button("Add spell mapping"))
                entries.InsertArrayElementAtIndex(entries.arraySize);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// A popup bound to a string property. <paramref name="editable"/> adds a free-text field next
        /// to the popup (spell ids can be typed; flavor names must come from the scanned set).
        /// </summary>
        private static void DrawStringDropdown(string label, SerializedProperty prop, string[] choices, bool editable)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                int current = Array.IndexOf(choices, prop.stringValue);
                var display = new List<string> { current < 0 ? $"‹{(string.IsNullOrEmpty(prop.stringValue) ? "none" : prop.stringValue)}›" : prop.stringValue };
                display.AddRange(choices);
                int picked = EditorGUILayout.Popup(label, 0, display.ToArray());
                if (picked > 0)
                    prop.stringValue = choices[picked - 1];

                if (editable)
                    prop.stringValue = EditorGUILayout.TextField(prop.stringValue, GUILayout.Width(160));
            }
        }

    }
}
