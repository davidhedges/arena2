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
    /// Authoring UI for global spell-to-recipe selection, with legacy motion and fixed migration
    /// paths retained for existing content.
    /// </summary>
    [CustomEditor(typeof(SpellCastAnimationMap))]
    public sealed class SpellCastAnimationMapEditor : UnityEditor.Editor
    {
        private string[] _spellIds = Array.Empty<string>();
        private string[] _animationIds = Array.Empty<string>();
        private string[] _animationLabels = Array.Empty<string>();

        private void OnEnable() => RefreshChoices();

        private void RefreshChoices()
        {
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string spellId in SpellPresentationEditorData.LoadSpellGameplayByActionId(out _).Keys)
                ids.Add(spellId);
            var map = (SpellCastAnimationMap)target;
            foreach (SpellCastAnimationMap.Entry e in map.Entries)
                if (!string.IsNullOrWhiteSpace(e.spellId)) ids.Add(e.spellId.Trim().ToUpperInvariant());
            _spellIds = ids.ToArray();

            SpellCastAnimationCatalog? catalog = SpellPresentationEditorData.FindFirstAsset<SpellCastAnimationCatalog>();
            SpellCastAnimationRecipe[] recipes = catalog?.Recipes
                .OrderBy(recipe => recipe.CategoryOrDefault, StringComparer.OrdinalIgnoreCase)
                .ThenBy(recipe => recipe.DisplayNameOrId, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<SpellCastAnimationRecipe>();
            _animationIds = recipes.Select(recipe => recipe.AnimationIdOrEmpty).ToArray();
            _animationLabels = recipes.Select(recipe => recipe.PickerLabel).ToArray();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Spell ids: {_spellIds.Length}", EditorStyles.miniLabel);
                if (GUILayout.Button("Refresh choices", GUILayout.Width(120)))
                    RefreshChoices();
            }
            SerializedProperty entries = serializedObject.FindProperty("entries");
            EditorGUILayout.Space();

            int removeAt = -1;
            for (int i = 0; i < entries.arraySize; i++)
            {
                SerializedProperty entry = entries.GetArrayElementAtIndex(i);
                SerializedProperty spellId = entry.FindPropertyRelative("spellId");
                SerializedProperty assignmentKind = entry.FindPropertyRelative("assignmentKind");

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        DrawStringDropdown("Spell", spellId, _spellIds, editable: true);
                        if (GUILayout.Button("✕", GUILayout.Width(22))) removeAt = i;
                    }
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(assignmentKind);
                    if (EditorGUI.EndChangeCheck())
                    {
                        SpellCastAnimationAssignmentKind kind =
                            (SpellCastAnimationAssignmentKind)assignmentKind.enumValueIndex;
                        if (kind == SpellCastAnimationAssignmentKind.Fixed)
                        {
                            entry.FindPropertyRelative("animationId").stringValue = string.Empty;
                            entry.FindPropertyRelative("motion").enumValueIndex = (int)SpellCastMotion.None;
                            entry.FindPropertyRelative("playbackLayer").enumValueIndex = (int)SpellCastLayerOverride.Auto;
                            entry.FindPropertyRelative("combatEntryMode").enumValueIndex = (int)SpellCastEntryModeOverride.Auto;
                            entry.FindPropertyRelative("animatedProp").boxedValue = default(SpellAnimatedPropHandoff);
                        }
                        else if (kind == SpellCastAnimationAssignmentKind.NoAnimation)
                        {
                            entry.FindPropertyRelative("animationId").stringValue = string.Empty;
                            entry.FindPropertyRelative("motion").enumValueIndex = (int)SpellCastMotion.None;
                            entry.FindPropertyRelative("fixedAnimation").boxedValue = default(WeaponSpellAnimationEntry);
                            entry.FindPropertyRelative("playbackLayer").enumValueIndex = (int)SpellCastLayerOverride.Auto;
                            entry.FindPropertyRelative("combatEntryMode").enumValueIndex = (int)SpellCastEntryModeOverride.Auto;
                            entry.FindPropertyRelative("animatedProp").boxedValue = default(SpellAnimatedPropHandoff);
                        }
                        else if (kind == SpellCastAnimationAssignmentKind.Catalog)
                        {
                            entry.FindPropertyRelative("motion").enumValueIndex = (int)SpellCastMotion.None;
                            entry.FindPropertyRelative("fixedAnimation").boxedValue = default(WeaponSpellAnimationEntry);
                        }
                        else
                        {
                            entry.FindPropertyRelative("animationId").stringValue = string.Empty;
                            entry.FindPropertyRelative("fixedAnimation").boxedValue = default(WeaponSpellAnimationEntry);
                        }
                    }
                    SpellCastAnimationAssignmentKind assignment =
                        (SpellCastAnimationAssignmentKind)assignmentKind.enumValueIndex;
                    if (assignment == SpellCastAnimationAssignmentKind.Fixed)
                    {
                        EditorGUILayout.PropertyField(entry.FindPropertyRelative("fixedAnimation"), includeChildren: true);
                    }
                    else if (assignment == SpellCastAnimationAssignmentKind.NoAnimation)
                    {
                        EditorGUILayout.HelpBox(
                            "This spell intentionally plays no cast animation.",
                            MessageType.Info);
                    }
                    else if (assignment == SpellCastAnimationAssignmentKind.Catalog)
                    {
                        DrawAnimationDropdown(entry.FindPropertyRelative("animationId"));
                        EditorGUILayout.PropertyField(entry.FindPropertyRelative("playbackLayer"));
                        EditorGUILayout.PropertyField(entry.FindPropertyRelative("combatEntryMode"));
                        EditorGUILayout.PropertyField(entry.FindPropertyRelative("animatedProp"), includeChildren: true);
                    }
                    else
                    {
                        EditorGUILayout.PropertyField(entry.FindPropertyRelative("motion"));
                        EditorGUILayout.PropertyField(entry.FindPropertyRelative("playbackLayer"));
                        EditorGUILayout.PropertyField(entry.FindPropertyRelative("combatEntryMode"));
                        EditorGUILayout.PropertyField(entry.FindPropertyRelative("animatedProp"), includeChildren: true);
                    }
                }
            }

            if (removeAt >= 0)
                entries.DeleteArrayElementAtIndex(removeAt);

            EditorGUILayout.Space();
            if (GUILayout.Button("Add spell mapping"))
            {
                int newIndex = entries.arraySize;
                entries.InsertArrayElementAtIndex(newIndex);
                entries.GetArrayElementAtIndex(newIndex).boxedValue = default(SpellCastAnimationMap.Entry);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawAnimationDropdown(SerializedProperty property)
        {
            int currentIndex = Array.IndexOf(_animationIds, property.stringValue);
            var options = new string[_animationLabels.Length + 1];
            options[0] = currentIndex >= 0
                ? _animationLabels[currentIndex]
                : $"‹{(string.IsNullOrWhiteSpace(property.stringValue) ? "none" : property.stringValue)}›";
            Array.Copy(_animationLabels, 0, options, 1, _animationLabels.Length);
            int selected = EditorGUILayout.Popup("Animation", 0, options);
            if (selected > 0)
                property.stringValue = _animationIds[selected - 1];
        }

        /// <summary>
        /// A popup bound to a string property. <paramref name="editable"/> adds a free-text field next
        /// to the popup.
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
