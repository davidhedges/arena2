#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    internal sealed class CoreAbilityAuthoringWindow : EditorWindow
    {
        private const string ProgressionCatalogPath = "server/src/progression_catalog.shared.json";

        private readonly Dictionary<string, bool> _coreByAbilityId = new(StringComparer.Ordinal);
        private readonly List<string> _loadErrors = new();

        private ProgressionCoreAbilityCatalog? _catalog;
        private Vector2 _scroll;
        private int _selectedClassIndex;
        private bool _dirty;

        [MenuItem("Arena/Progression/Core Ability Authoring", false, 480)]
        public static void Open()
        {
            var window = GetWindow<CoreAbilityAuthoringWindow>("Core Abilities");
            window.minSize = new Vector2(680f, 520f);
            window.Load();
        }

        private void OnEnable()
        {
            Load();
        }

        private void OnGUI()
        {
            DrawToolbar();

            foreach (string error in _loadErrors)
                EditorGUILayout.HelpBox(error, MessageType.Error);

            if (_catalog == null)
                return;

            if (_catalog.Classes.Count == 0)
            {
                EditorGUILayout.HelpBox("No classes found in progression_catalog.shared.json.", MessageType.Warning);
                return;
            }

            string[] classLabels = _catalog.Classes
                .Select(row => $"{row.DisplayName} ({row.ClassId})")
                .ToArray();
            _selectedClassIndex = Mathf.Clamp(_selectedClassIndex, 0, classLabels.Length - 1);
            _selectedClassIndex = EditorGUILayout.Popup("Class", _selectedClassIndex, classLabels);

            ProgressionClassSummary selectedClass = _catalog.Classes[_selectedClassIndex];
            List<ProgressionAbilitySummary> abilities = _catalog.Abilities
                .Where(ability => string.Equals(ability.ClassId, selectedClass.ClassId, StringComparison.Ordinal))
                .OrderBy(ability => ability.SortOrder)
                .ThenBy(ability => ability.DisplayName, StringComparer.Ordinal)
                .ToList();

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                "CORE_ABILITY marks class-defining starter abilities. New loadouts seed default_loadout_assignments onto the bar; Dodge and Parry can be defaults without being core abilities.",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (ProgressionAbilitySummary ability in abilities)
                DrawAbilityRow(ability);
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(80f)))
                    Load();

                using (new EditorGUI.DisabledScope(!_dirty || _catalog == null))
                {
                    if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(80f)))
                        Save();
                }

                GUILayout.FlexibleSpace();
                if (_dirty)
                    GUILayout.Label("Unsaved changes", EditorStyles.miniLabel);
            }
        }

        private void DrawAbilityRow(ProgressionAbilitySummary ability)
        {
            string abilityId = ability.AbilityId;
            bool current = _coreByAbilityId.TryGetValue(abilityId, out bool isCore) && isCore;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool next = EditorGUILayout.ToggleLeft("Core", current, GUILayout.Width(70f));
                    if (next != current)
                    {
                        _coreByAbilityId[abilityId] = next;
                        _dirty = true;
                    }

                    EditorGUILayout.LabelField(ability.DisplayName, EditorStyles.boldLabel, GUILayout.Width(190f));
                    EditorGUILayout.LabelField(abilityId, GUILayout.MinWidth(220f));
                    EditorGUILayout.LabelField(ability.Kind, GUILayout.Width(150f));
                }

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("Action Id", ability.ActionId);
                    EditorGUILayout.TextField("Default Slots", string.Join(", ", ability.DefaultSlots));
                }

                if (nextCoreNeedsLoadoutWarning(abilityId))
                {
                    EditorGUILayout.HelpBox(
                        "This ability is marked core but has no default loadout assignment yet.",
                        MessageType.Warning);
                }
            }
        }

        private bool nextCoreNeedsLoadoutWarning(string abilityId)
        {
            return _coreByAbilityId.TryGetValue(abilityId, out bool isCore)
                && isCore
                && _catalog != null
                && !_catalog.DefaultAssignedAbilityIds.Contains(abilityId);
        }

        private void Load()
        {
            _loadErrors.Clear();
            _coreByAbilityId.Clear();
            _dirty = false;

            string absolutePath = AbsoluteCatalogPath();
            if (!File.Exists(absolutePath))
            {
                _loadErrors.Add($"Progression catalog not found at '{ProgressionCatalogPath}'.");
                _catalog = null;
                return;
            }

            try
            {
                string json = File.ReadAllText(absolutePath);
                _catalog = CoreAbilityCatalogJson.ReadCatalog(json);
                foreach (ProgressionAbilitySummary ability in _catalog.Abilities)
                    _coreByAbilityId[ability.AbilityId] = ability.IsCore;
                _selectedClassIndex = Mathf.Clamp(_selectedClassIndex, 0, Math.Max(0, _catalog.Classes.Count - 1));
            }
            catch (Exception ex)
            {
                _catalog = null;
                _loadErrors.Add($"Failed to parse '{ProgressionCatalogPath}': {ex.Message}");
            }
        }

        private void Save()
        {
            if (_catalog == null)
                return;

            string absolutePath = AbsoluteCatalogPath();
            try
            {
                string currentJson = File.ReadAllText(absolutePath);
                string updatedJson = CoreAbilityCatalogJson.ApplyCoreAbilityTags(currentJson, _coreByAbilityId);
                File.WriteAllText(absolutePath, updatedJson);
                AssetDatabase.Refresh();
                Load();
            }
            catch (Exception ex)
            {
                _loadErrors.Add($"Failed to save '{ProgressionCatalogPath}': {ex.Message}");
            }
        }

        private static string AbsoluteCatalogPath()
        {
            return Path.Combine(Directory.GetCurrentDirectory(), ProgressionCatalogPath);
        }
    }

    public static class CoreAbilityCatalogJson
    {
        private const string CoreAbilityTag = "CORE_ABILITY";

        public static ProgressionCoreAbilityCatalog ReadCatalog(string json)
        {
            ProgressionCoreAbilityDocument document = JsonUtility.FromJson<ProgressionCoreAbilityDocument>(json)
                ?? new ProgressionCoreAbilityDocument();
            var defaultSlotsByAbilityId = BuildDefaultSlotsByAbilityId(document.default_loadout_assignments);
            var defaultAssignedAbilityIds = new HashSet<string>(defaultSlotsByAbilityId.Keys, StringComparer.Ordinal);

            List<ProgressionClassSummary> classes = document.classes
                .Select(row => new ProgressionClassSummary(
                    Normalize(row.class_id),
                    string.IsNullOrWhiteSpace(row.display_name) ? Normalize(row.class_id) : row.display_name,
                    row.sort_order))
                .Where(row => !string.IsNullOrWhiteSpace(row.ClassId))
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.DisplayName, StringComparer.Ordinal)
                .ToList();

            List<ProgressionAbilitySummary> abilities = document.abilities
                .Select(row =>
                {
                    string abilityId = Normalize(row.ability_id);
                    defaultSlotsByAbilityId.TryGetValue(abilityId, out List<string>? defaultSlots);
                    return new ProgressionAbilitySummary(
                        abilityId,
                        Normalize(row.class_id),
                        Normalize(row.action_id),
                        string.IsNullOrWhiteSpace(row.display_name) ? abilityId : row.display_name,
                        Normalize(row.gameplay.kind),
                        row.sort_order,
                        row.ability_tags.Select(Normalize).Where(tag => !string.IsNullOrWhiteSpace(tag)).ToList(),
                        defaultSlots ?? new List<string>());
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.AbilityId))
                .ToList();

            return new ProgressionCoreAbilityCatalog(classes, abilities, defaultAssignedAbilityIds);
        }

        public static string ApplyCoreAbilityTags(
            string json,
            IReadOnlyDictionary<string, bool> coreByAbilityId)
        {
            if (!TryFindPropertyArrayRange(json, "abilities", out int abilitiesArrayStart, out int abilitiesArrayEnd))
                throw new InvalidOperationException("Could not find top-level abilities[] array.");

            List<TextRange> abilityRanges = ExtractObjectRanges(json, abilitiesArrayStart + 1, abilitiesArrayEnd);
            var replacements = new List<(TextRange Range, string Text)>();

            foreach (TextRange range in abilityRanges)
            {
                string objectJson = json.Substring(range.Start, range.Length);
                AbilityTagPatchRow row = JsonUtility.FromJson<AbilityTagPatchRow>(objectJson)
                    ?? new AbilityTagPatchRow();
                string abilityId = Normalize(row.ability_id);
                if (string.IsNullOrWhiteSpace(abilityId) || !coreByAbilityId.TryGetValue(abilityId, out bool shouldBeCore))
                    continue;

                string updatedObject = ApplyCoreAbilityTagToObject(objectJson, shouldBeCore);
                if (!string.Equals(updatedObject, objectJson, StringComparison.Ordinal))
                    replacements.Add((range, updatedObject));
            }

            if (replacements.Count == 0)
                return json;

            var builder = new StringBuilder(json);
            foreach ((TextRange range, string text) in replacements.OrderByDescending(item => item.Range.Start))
            {
                builder.Remove(range.Start, range.Length);
                builder.Insert(range.Start, text);
            }

            return builder.ToString();
        }

        private static string ApplyCoreAbilityTagToObject(string objectJson, bool shouldBeCore)
        {
            AbilityTagPatchRow row = JsonUtility.FromJson<AbilityTagPatchRow>(objectJson)
                ?? new AbilityTagPatchRow();
            List<string> tags = row.ability_tags
                .Select(Normalize)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.Ordinal)
                .Where(tag => !string.Equals(tag, CoreAbilityTag, StringComparison.Ordinal))
                .ToList();

            if (shouldBeCore)
                tags.Add(CoreAbilityTag);

            if (TryFindTopLevelPropertyArrayRange(objectJson, "ability_tags", out int tagArrayStart, out int tagArrayEnd))
            {
                string formattedArray = FormatTagArray(tags, DetectArrayValueIndent(objectJson, tagArrayStart));
                return objectJson.Substring(0, tagArrayStart)
                    + formattedArray
                    + objectJson.Substring(tagArrayEnd + 1);
            }

            if (!shouldBeCore)
                return objectJson;

            int insertAt = FindTopLevelPropertyStart(objectJson, "sort_order");
            if (insertAt < 0)
                insertAt = FindTopLevelPropertyStart(objectJson, "gameplay");
            if (insertAt < 0)
                insertAt = objectJson.LastIndexOf('}');
            if (insertAt < 0)
                return objectJson;

            string propertyIndent = DetectPropertyIndent(objectJson);
            string valueIndent = propertyIndent + "  ";
            string propertyText = $"{propertyIndent}\"ability_tags\": {FormatTagArray(tags, valueIndent)},\n";
            return objectJson.Substring(0, insertAt) + propertyText + objectJson.Substring(insertAt);
        }

        private static Dictionary<string, List<string>> BuildDefaultSlotsByAbilityId(
            IEnumerable<DefaultLoadoutAssignmentRow> assignments)
        {
            var slotsByAbilityId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (DefaultLoadoutAssignmentRow assignment in assignments)
            {
                string abilityId = Normalize(DefaultAbilityId(assignment));
                if (string.IsNullOrWhiteSpace(abilityId))
                    continue;

                if (!slotsByAbilityId.TryGetValue(abilityId, out List<string> slots))
                {
                    slots = new List<string>();
                    slotsByAbilityId.Add(abilityId, slots);
                }

                slots.Add(Normalize(assignment.slot_id));
            }

            foreach (List<string> slots in slotsByAbilityId.Values)
                slots.Sort(StringComparer.Ordinal);

            return slotsByAbilityId;
        }

        private static string DefaultAbilityId(DefaultLoadoutAssignmentRow assignment)
        {
            string actionKind = Normalize(assignment.action_kind);
            string actionId = Normalize(assignment.action_id);
            if (!string.IsNullOrWhiteSpace(actionKind) || !string.IsNullOrWhiteSpace(actionId))
                return string.Equals(actionKind, "ABILITY", StringComparison.Ordinal) ? actionId : string.Empty;

            return assignment.ability_id;
        }

        private static bool TryFindPropertyArrayRange(
            string json,
            string propertyName,
            out int arrayStart,
            out int arrayEnd)
        {
            int propertyStart = FindPropertyName(json, propertyName, 0, json.Length);
            if (propertyStart < 0)
            {
                arrayStart = -1;
                arrayEnd = -1;
                return false;
            }

            int colon = json.IndexOf(':', propertyStart);
            arrayStart = colon < 0 ? -1 : json.IndexOf('[', colon);
            arrayEnd = arrayStart < 0 ? -1 : FindMatchingBracket(json, arrayStart, '[', ']');
            return arrayStart >= 0 && arrayEnd >= 0;
        }

        private static bool TryFindTopLevelPropertyArrayRange(
            string objectJson,
            string propertyName,
            out int arrayStart,
            out int arrayEnd)
        {
            int propertyStart = FindTopLevelPropertyStart(objectJson, propertyName);
            if (propertyStart < 0)
            {
                arrayStart = -1;
                arrayEnd = -1;
                return false;
            }

            int colon = objectJson.IndexOf(':', propertyStart);
            arrayStart = colon < 0 ? -1 : objectJson.IndexOf('[', colon);
            arrayEnd = arrayStart < 0 ? -1 : FindMatchingBracket(objectJson, arrayStart, '[', ']');
            return arrayStart >= 0 && arrayEnd >= 0;
        }

        private static int FindTopLevelPropertyStart(string objectJson, string propertyName)
        {
            bool inString = false;
            bool escape = false;
            int depth = 0;
            string quoted = $"\"{propertyName}\"";

            for (int i = 0; i < objectJson.Length; i++)
            {
                char ch = objectJson[i];
                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                    }
                    else if (ch == '\\')
                    {
                        escape = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (ch == '"')
                {
                    if (depth == 1 && StartsWith(objectJson, i, quoted))
                        return LineStart(objectJson, i);
                    inString = true;
                }
                else if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                }
            }

            return -1;
        }

        private static int FindPropertyName(string json, string propertyName, int start, int end)
        {
            string quoted = $"\"{propertyName}\"";
            int index = start;
            while (index >= 0 && index < end)
            {
                index = json.IndexOf(quoted, index, StringComparison.Ordinal);
                if (index < 0 || index >= end)
                    return -1;
                return index;
            }

            return -1;
        }

        private static List<TextRange> ExtractObjectRanges(string json, int start, int end)
        {
            var ranges = new List<TextRange>();
            bool inString = false;
            bool escape = false;
            int depth = 0;
            int objectStart = -1;

            for (int i = start; i < end; i++)
            {
                char ch = json[i];
                if (inString)
                {
                    if (escape)
                        escape = false;
                    else if (ch == '\\')
                        escape = true;
                    else if (ch == '"')
                        inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '{')
                {
                    if (depth == 0)
                        objectStart = i;
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        ranges.Add(new TextRange(objectStart, i + 1));
                        objectStart = -1;
                    }
                }
            }

            return ranges;
        }

        private static int FindMatchingBracket(string text, int openIndex, char open, char close)
        {
            bool inString = false;
            bool escape = false;
            int depth = 0;

            for (int i = openIndex; i < text.Length; i++)
            {
                char ch = text[i];
                if (inString)
                {
                    if (escape)
                        escape = false;
                    else if (ch == '\\')
                        escape = true;
                    else if (ch == '"')
                        inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == open)
                {
                    depth++;
                }
                else if (ch == close)
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static string FormatTagArray(IReadOnlyList<string> tags, string valueIndent)
        {
            if (tags.Count == 0)
                return "[]";

            var builder = new StringBuilder();
            builder.AppendLine("[");
            for (int i = 0; i < tags.Count; i++)
            {
                builder.Append(valueIndent);
                builder.Append('"').Append(tags[i]).Append('"');
                if (i < tags.Count - 1)
                    builder.Append(',');
                builder.AppendLine();
            }
            builder.Append(valueIndent.Length >= 2 ? valueIndent.Substring(2) : string.Empty);
            builder.Append(']');
            return builder.ToString();
        }

        private static string DetectArrayValueIndent(string objectJson, int arrayStart)
        {
            int lineStart = LineStart(objectJson, arrayStart);
            int propertyIndentLen = 0;
            while (lineStart + propertyIndentLen < objectJson.Length
                && objectJson[lineStart + propertyIndentLen] == ' ')
            {
                propertyIndentLen++;
            }

            return new string(' ', propertyIndentLen + 2);
        }

        private static string DetectPropertyIndent(string objectJson)
        {
            int abilityIdStart = FindTopLevelPropertyStart(objectJson, "ability_id");
            if (abilityIdStart >= 0)
            {
                int count = 0;
                while (abilityIdStart + count < objectJson.Length && objectJson[abilityIdStart + count] == ' ')
                    count++;
                return new string(' ', count);
            }

            return "      ";
        }

        private static int LineStart(string text, int index)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, index));
            return lineStart < 0 ? 0 : lineStart + 1;
        }

        private static bool StartsWith(string text, int index, string value)
        {
            return index + value.Length <= text.Length
                && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant().Replace('-', '_');
        }

        private readonly struct TextRange
        {
            public readonly int Start;
            public readonly int End;
            public int Length => End - Start;

            public TextRange(int start, int end)
            {
                Start = start;
                End = end;
            }
        }
    }

    public sealed class ProgressionCoreAbilityCatalog
    {
        public readonly List<ProgressionClassSummary> Classes;
        public readonly List<ProgressionAbilitySummary> Abilities;
        public readonly HashSet<string> DefaultAssignedAbilityIds;

        public ProgressionCoreAbilityCatalog(
            List<ProgressionClassSummary> classes,
            List<ProgressionAbilitySummary> abilities,
            HashSet<string> defaultAssignedAbilityIds)
        {
            Classes = classes;
            Abilities = abilities;
            DefaultAssignedAbilityIds = defaultAssignedAbilityIds;
        }
    }

    public sealed class ProgressionClassSummary
    {
        public readonly string ClassId;
        public readonly string DisplayName;
        public readonly int SortOrder;

        public ProgressionClassSummary(string classId, string displayName, int sortOrder)
        {
            ClassId = classId;
            DisplayName = displayName;
            SortOrder = sortOrder;
        }
    }

    public sealed class ProgressionAbilitySummary
    {
        public readonly string AbilityId;
        public readonly string ClassId;
        public readonly string ActionId;
        public readonly string DisplayName;
        public readonly string Kind;
        public readonly int SortOrder;
        public readonly List<string> Tags;
        public readonly List<string> DefaultSlots;

        public bool IsCore => Tags.Any(tag => string.Equals(tag, "CORE_ABILITY", StringComparison.Ordinal));

        public ProgressionAbilitySummary(
            string abilityId,
            string classId,
            string actionId,
            string displayName,
            string kind,
            int sortOrder,
            List<string> tags,
            List<string> defaultSlots)
        {
            AbilityId = abilityId;
            ClassId = classId;
            ActionId = actionId;
            DisplayName = displayName;
            Kind = kind;
            SortOrder = sortOrder;
            Tags = tags;
            DefaultSlots = defaultSlots;
        }
    }

    [Serializable]
    internal sealed class ProgressionCoreAbilityDocument
    {
        public List<ClassDefinitionRow> classes = new();
        public List<AbilityDefinitionRow> abilities = new();
        public List<DefaultLoadoutAssignmentRow> default_loadout_assignments = new();
    }

    [Serializable]
    internal sealed class ClassDefinitionRow
    {
        public string class_id = string.Empty;
        public string display_name = string.Empty;
        public int sort_order;
    }

    [Serializable]
    internal sealed class AbilityDefinitionRow
    {
        public string ability_id = string.Empty;
        public string class_id = string.Empty;
        public string action_id = string.Empty;
        public string display_name = string.Empty;
        public int sort_order;
        public List<string> ability_tags = new();
        public AbilityGameplayRow gameplay = new();
    }

    [Serializable]
    internal sealed class AbilityGameplayRow
    {
        public string kind = string.Empty;
    }

    [Serializable]
    internal sealed class DefaultLoadoutAssignmentRow
    {
        public string slot_id = string.Empty;
        public string action_kind = string.Empty;
        public string action_id = string.Empty;
        public string ability_id = string.Empty;
    }

    [Serializable]
    internal sealed class AbilityTagPatchRow
    {
        public string ability_id = string.Empty;
        public List<string> ability_tags = new();
    }
}
