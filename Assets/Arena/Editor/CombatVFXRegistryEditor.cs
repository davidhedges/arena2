#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Arena.Combat;
using Arena.Presentation.VFX;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    [CustomEditor(typeof(CombatVFXRegistry))]
    public sealed class CombatVFXRegistryEditor : UnityEditor.Editor
    {
        private const string SharedGroupKey = "__SHARED";
        private const string NonDisciplineGroupKey = "__NON_DISCIPLINE";
        private const string NoCatalogCueGroupKey = "__NO_CATALOG_CUE";

        private readonly Dictionary<string, bool> _expandedByGroup = new(StringComparer.Ordinal);
        private Dictionary<string, CombatVfxDisciplineUsage> _usageByVfxId = new(StringComparer.Ordinal);
        private string _catalogWarning = string.Empty;
        private long _catalogWriteTicks = long.MinValue;

        private sealed class EntryGroup
        {
            public EntryGroup(string key, string displayName, int sortOrder)
            {
                Key = key;
                DisplayName = displayName;
                SortOrder = sortOrder;
            }

            public string Key { get; }
            public string DisplayName { get; }
            public int SortOrder { get; }
            public List<int> EntryIndices { get; } = new();
        }

        private void OnEnable()
        {
            ReloadSchoolGroups();
        }

        public override void OnInspectorGUI()
        {
            ReloadSchoolGroupsIfChanged();
            serializedObject.Update();

            var registry = (CombatVFXRegistry)target;
            DrawScriptReference();
            EditorGUILayout.HelpBox(
                "Entries are grouped from authoritative ability disciplines and stored alphabetically by normalized VFX ID.",
                MessageType.Info);
            if (!string.IsNullOrWhiteSpace(_catalogWarning))
                EditorGUILayout.HelpBox(_catalogWarning, MessageType.Warning);

            SerializedProperty entries = serializedObject.FindProperty("entries");
            List<EntryGroup> groups = BuildEntryGroups(entries);
            DrawGroupingToolbar(groups);
            if (DrawRegistryToolbar(entries, registry))
                return;

            int removeIndex = DrawEntryGroups(entries, groups);
            if (removeIndex >= 0)
            {
                RemoveEntry(entries, removeIndex, registry);
                return;
            }

            if (serializedObject.ApplyModifiedProperties())
            {
                if (registry.SortEntriesAlphabetically())
                {
                    EditorUtility.SetDirty(registry);
                    serializedObject.Update();
                }
            }

            DrawRuntimeDiagnostics(registry);
        }

        private void DrawScriptReference()
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
        }

        private void DrawGroupingToolbar(IReadOnlyList<EntryGroup> groups)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh School Groups"))
                    ReloadSchoolGroups();
                if (GUILayout.Button("Expand All"))
                    SetAllGroupsExpanded(groups, true);
                if (GUILayout.Button("Collapse All"))
                    SetAllGroupsExpanded(groups, false);
            }
        }

        private bool DrawRegistryToolbar(SerializedProperty entries, CombatVFXRegistry registry)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add VFX Entry"))
                {
                    AddEntry(entries, registry);
                    return true;
                }

                if (GUILayout.Button("Sort Entries Alphabetically"))
                    return SortEntriesAlphabetically(registry);
            }

            return false;
        }

        private int DrawEntryGroups(SerializedProperty entries, IReadOnlyList<EntryGroup> groups)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Combat VFX Entries by Discipline", EditorStyles.boldLabel);

            foreach (EntryGroup group in groups)
            {
                bool expanded = _expandedByGroup.TryGetValue(group.Key, out bool current) && current;
                expanded = EditorGUILayout.Foldout(
                    expanded,
                    $"{group.DisplayName} ({group.EntryIndices.Count})",
                    true);
                _expandedByGroup[group.Key] = expanded;
                if (!expanded)
                    continue;

                EditorGUI.indentLevel++;
                foreach (int entryIndex in group.EntryIndices)
                {
                    SerializedProperty entry = entries.GetArrayElementAtIndex(entryIndex);
                    if (DrawEntry(entry))
                    {
                        EditorGUI.indentLevel--;
                        return entryIndex;
                    }
                }
                EditorGUI.indentLevel--;
            }

            return -1;
        }

        private bool DrawEntry(SerializedProperty entry)
        {
            SerializedProperty vfxIdProperty = entry.FindPropertyRelative("vfxId");
            string vfxId = WireIdentifier.Normalize(vfxIdProperty.stringValue);
            string label = vfxId.Length == 0 ? "<New VFX Entry>" : vfxId;
            if (_usageByVfxId.TryGetValue(vfxId, out CombatVfxDisciplineUsage usage)
                && usage.Disciplines.Count > 1)
            {
                var disciplineNames = new string[usage.Disciplines.Count];
                for (int index = 0; index < usage.Disciplines.Count; index++)
                    disciplineNames[index] = usage.Disciplines[index].DisplayName;
                label += $" — {string.Join(", ", disciplineNames)}";
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    entry.isExpanded = EditorGUILayout.Foldout(entry.isExpanded, label, true);
                    if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(58f)))
                        return true;
                }

                if (entry.isExpanded)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(vfxIdProperty);
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("prefab"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("scale"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("localPositionOffset"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("localEulerAngles"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("scaleMultiplierAtLifetimeEnd"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("followAuthoritativeProjectileMotion"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("lockProjectileRootToSpawn"));
                    EditorGUI.indentLevel--;
                }
            }

            return false;
        }

        private List<EntryGroup> BuildEntryGroups(SerializedProperty entries)
        {
            var groupsByKey = new Dictionary<string, EntryGroup>(StringComparer.Ordinal);
            for (int index = 0; index < entries.arraySize; index++)
            {
                SerializedProperty entry = entries.GetArrayElementAtIndex(index);
                string vfxId = WireIdentifier.Normalize(entry.FindPropertyRelative("vfxId").stringValue);
                ResolveEntryGroup(vfxId, out string key, out string displayName, out int sortOrder);
                if (!groupsByKey.TryGetValue(key, out EntryGroup group))
                {
                    group = new EntryGroup(key, displayName, sortOrder);
                    groupsByKey[key] = group;
                }

                group.EntryIndices.Add(index);
            }

            var groups = new List<EntryGroup>(groupsByKey.Values);
            groups.Sort((left, right) =>
            {
                int byOrder = left.SortOrder.CompareTo(right.SortOrder);
                return byOrder != 0
                    ? byOrder
                    : string.CompareOrdinal(left.DisplayName, right.DisplayName);
            });
            return groups;
        }

        private void ResolveEntryGroup(
            string vfxId,
            out string key,
            out string displayName,
            out int sortOrder)
        {
            if (!_usageByVfxId.TryGetValue(vfxId, out CombatVfxDisciplineUsage usage))
            {
                key = NoCatalogCueGroupKey;
                displayName = "Other / No Catalog Cue";
                sortOrder = int.MaxValue;
                return;
            }

            if (usage.Disciplines.Count == 0)
            {
                key = NonDisciplineGroupKey;
                displayName = "NPC / Non-Discipline";
                sortOrder = int.MaxValue - 1;
                return;
            }

            if (usage.Disciplines.Count > 1)
            {
                key = SharedGroupKey;
                displayName = "Shared Across Disciplines";
                sortOrder = int.MaxValue - 2;
                return;
            }

            CombatDisciplineAuthoringFacts discipline = usage.Disciplines[0];
            key = discipline.DisciplineId;
            displayName = discipline.DisplayName;
            sortOrder = discipline.SortOrder;
        }

        private void ReloadSchoolGroupsIfChanged()
        {
            string path = SpellPresentationEditorData.AbsoluteProgressionCatalogPath;
            long writeTicks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
            if (writeTicks != _catalogWriteTicks)
                ReloadSchoolGroups();
        }

        private void ReloadSchoolGroups()
        {
            _usageByVfxId = SpellPresentationEditorData.LoadCombatVfxDisciplineUsage(
                out _,
                out _catalogWarning);
            string path = SpellPresentationEditorData.AbsoluteProgressionCatalogPath;
            _catalogWriteTicks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
            Repaint();
        }

        private void SetAllGroupsExpanded(IEnumerable<EntryGroup> groups, bool expanded)
        {
            foreach (EntryGroup group in groups)
                _expandedByGroup[group.Key] = expanded;
            Repaint();
        }

        private void AddEntry(SerializedProperty entries, CombatVFXRegistry registry)
        {
            Undo.RecordObject(registry, "Add Combat VFX Registry Entry");
            int index = entries.arraySize;
            entries.InsertArrayElementAtIndex(index);
            SerializedProperty entry = entries.GetArrayElementAtIndex(index);
            entry.FindPropertyRelative("vfxId").stringValue = string.Empty;
            entry.FindPropertyRelative("prefab").objectReferenceValue = null;
            entry.FindPropertyRelative("scale").floatValue = 1f;
            entry.FindPropertyRelative("localPositionOffset").vector3Value = Vector3.zero;
            entry.FindPropertyRelative("localEulerAngles").vector3Value = Vector3.zero;
            entry.FindPropertyRelative("scaleMultiplierAtLifetimeEnd").floatValue = 1f;
            entry.FindPropertyRelative("followAuthoritativeProjectileMotion").boolValue = false;
            entry.FindPropertyRelative("lockProjectileRootToSpawn").boolValue = false;
            entry.isExpanded = true;
            serializedObject.ApplyModifiedProperties();
            registry.SortEntriesAlphabetically();
            EditorUtility.SetDirty(registry);
            serializedObject.Update();
            _expandedByGroup[NoCatalogCueGroupKey] = true;
            Repaint();
        }

        private void RemoveEntry(SerializedProperty entries, int index, CombatVFXRegistry registry)
        {
            Undo.RecordObject(registry, "Remove Combat VFX Registry Entry");
            entries.DeleteArrayElementAtIndex(index);
            serializedObject.ApplyModifiedProperties();
            registry.SortEntriesAlphabetically();
            EditorUtility.SetDirty(registry);
            serializedObject.Update();
            Repaint();
        }

        private bool SortEntriesAlphabetically(CombatVFXRegistry registry)
        {
            Undo.RecordObject(registry, "Sort Combat VFX Registry Alphabetically");
            if (!registry.SortEntriesAlphabetically())
                return false;

            EditorUtility.SetDirty(registry);
            serializedObject.Update();
            Repaint();
            return true;
        }

        private static void DrawRuntimeDiagnostics(CombatVFXRegistry registry)
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Runtime Diagnostics", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Clear Runtime Cache"))
                {
                    registry.InvalidateIndex();
                    CombatVFXRegistry.ReloadShared();
                    Debug.Log("CombatVFXRegistry runtime cache cleared and shared registry reloaded.", registry);
                }

                if (GUILayout.Button("Validate Registry"))
                    ValidateRegistry(registry);
            }

            if (GUILayout.Button("Log Resolved VFX Bindings"))
                LogResolvedBindings(registry);
        }

        private static void ValidateRegistry(CombatVFXRegistry registry)
        {
            var errors = new List<string>();
            registry.CollectAuthoringErrors(errors);
            if (errors.Count == 0)
            {
                Debug.Log("CombatVFXRegistry validation passed.", registry);
                return;
            }

            foreach (string error in errors)
                Debug.LogError(error, registry);
        }

        private static void LogResolvedBindings(CombatVFXRegistry registry)
        {
            registry.InvalidateIndex();
            foreach (CombatVFXRegistry.Entry entry in registry.Entries)
            {
                string vfxId = WireIdentifier.Normalize(entry.vfxId);
                GameObject? prefab = registry.ResolvePrefab(vfxId);
                string prefabPath = prefab != null ? AssetDatabase.GetAssetPath(prefab) : "<unresolved>";
                string prefabName = prefab != null ? prefab.name : "<null>";
                Debug.Log(
                    $"{vfxId} -> {prefabName} ({prefabPath}) scale={entry.scale:0.###} "
                    + $"localPositionOffset={entry.localPositionOffset} localEulerAngles={entry.localEulerAngles}",
                    registry);
            }
        }
    }
}
