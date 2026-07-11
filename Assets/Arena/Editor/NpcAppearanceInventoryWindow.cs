#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    [Serializable]
    internal sealed class NpcAppearanceInventoryDocument
    {
        public string schema = "ARENA_NPC_APPEARANCE_INVENTORY_DRAFT_V1";
        public int expected_appearance_count = 146;
        public int appearance_count;
        public int family_count;
        public List<string> global_warnings = new();
        public List<NpcAppearanceInventoryEntry> appearances = new();
    }

    [Serializable]
    internal sealed class NpcVector3Draft
    {
        public float x;
        public float y;
        public float z;

        public static NpcVector3Draft From(Vector3 value)
            => new() { x = value.x, y = value.y, z = value.z };
    }

    [Serializable]
    internal sealed class NpcAppearanceInventoryEntry
    {
        public string source_package = string.Empty;
        public string family_name = string.Empty;
        public string template_id_candidate = string.Empty;
        public string appearance_id_candidate = string.Empty;
        public string prefab_path = string.Empty;
        public int animator_count;
        public string primary_animator_path_candidate = string.Empty;
        public string animator_controller_path = string.Empty;
        public string avatar_kind = string.Empty;
        public bool root_motion_enabled;
        public List<string> animator_candidates = new();
        public List<string> animation_clips = new();
        public int renderer_count;
        public NpcVector3Draft renderer_bounds_center = new();
        public NpcVector3Draft renderer_bounds_size = new();
        public float ground_offset_candidate;
        public List<string> controller_states = new();
        public List<string> idle_candidates = new();
        public List<string> ready_candidates = new();
        public List<string> walk_candidates = new();
        public List<string> run_candidates = new();
        public List<string> attack_candidates = new();
        public List<string> spell_candidates = new();
        public List<string> hit_candidates = new();
        public List<string> death_candidates = new();
        public List<string> status_reaction_candidates = new();
        public List<string> review_warnings = new();
    }

    /// <summary>
    /// Read-only inventory and draft-authoring surface for licensed vendor NPC
    /// prefabs. It deliberately emits candidates for review and never writes a
    /// runtime catalog or visual profile automatically.
    /// </summary>
    public sealed class NpcAppearanceInventoryWindow : EditorWindow
    {
        private const string DefaultOutputPath = "Logs/npc-appearance-inventory-draft.json";

        private readonly struct PackageRoot
        {
            public PackageRoot(string packageId, string assetPath)
            {
                PackageId = packageId;
                AssetPath = assetPath;
            }

            public string PackageId { get; }
            public string AssetPath { get; }
        }

        private static readonly PackageRoot[] PackageRoots =
        {
            new(
                "KOBOLD_PACK",
                "Assets/ThirdParty/AssetStore/Characters/KoboldPack/Prefabs"),
            new(
                "STYLIZED_FANTASY_ENEMY_NPC_BUNDLE",
                "Assets/ThirdParty/AssetStore/Characters/StylizedFantasyEnemyNPCBundle/Prefabs"),
            new(
                "STYLIZED_FANTASY_ENEMY_NPC_BUNDLE_2",
                "Assets/ThirdParty/AssetStore/Characters/StylizedFantasyEnemyNPCBundle2/Prefabs"),
        };

        private NpcAppearanceInventoryDocument? _document;
        private Vector2 _scroll;
        private string _search = string.Empty;

        [MenuItem("Arena/NPC/Open Appearance Inventory Draft")]
        private static void Open()
        {
            var window = GetWindow<NpcAppearanceInventoryWindow>("NPC Appearance Inventory");
            window.minSize = new Vector2(720f, 420f);
            window.Show();
        }

        [MenuItem("Arena/NPC/Export Appearance Inventory Draft")]
        public static void ExportDraft()
        {
            NpcAppearanceInventoryDocument document = ScanInventory();
            WriteDocument(document, DefaultOutputPath);
            Debug.Log(
                $"[NPC Appearance Inventory] Exported {document.appearance_count} appearances "
                + $"across {document.family_count} families to {DefaultOutputPath}.");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("NPC Appearance Inventory Draft", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Scans vendor prefab assets and proposes review data only. It does not modify the runtime NPC catalog or create visual profiles.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan Vendor Prefabs", GUILayout.Width(170f)))
                    _document = ScanInventory();
                using (new EditorGUI.DisabledScope(_document == null))
                {
                    if (GUILayout.Button("Export Review JSON", GUILayout.Width(160f)))
                    {
                        string path = EditorUtility.SaveFilePanel(
                            "Export NPC Appearance Inventory Draft",
                            Path.Combine(Directory.GetCurrentDirectory(), "Logs"),
                            "npc-appearance-inventory-draft",
                            "json");
                        if (!string.IsNullOrWhiteSpace(path))
                            WriteDocument(_document!, path);
                    }
                }
                GUILayout.FlexibleSpace();
                _search = EditorGUILayout.TextField("Search", _search, GUILayout.MinWidth(260f));
            }

            if (_document == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                $"Appearances: {_document.appearance_count}/{_document.expected_appearance_count}    "
                + $"Families: {_document.family_count}");
            foreach (string warning in _document.global_warnings)
                EditorGUILayout.HelpBox(warning, MessageType.Warning);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (NpcAppearanceInventoryEntry entry in _document.appearances)
            {
                if (!MatchesSearch(entry, _search))
                    continue;

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        $"{entry.template_id_candidate}  /  {entry.appearance_id_candidate}",
                        EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(entry.prefab_path, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(
                        $"Package: {entry.source_package}    Animators: {entry.animator_count}    "
                        + $"Avatar: {entry.avatar_kind}    States: {entry.controller_states.Count}");
                    EditorGUILayout.LabelField(
                        $"Renderers: {entry.renderer_count}    Bounds: "
                        + $"{entry.renderer_bounds_size.x:F2} x {entry.renderer_bounds_size.y:F2} x {entry.renderer_bounds_size.z:F2}    "
                        + $"Ground offset: {entry.ground_offset_candidate:F3}");
                    EditorGUILayout.LabelField(
                        $"Animator: {DisplayOrReview(entry.primary_animator_path_candidate)}    "
                        + $"Controller: {DisplayOrReview(entry.animator_controller_path)}");
                    if (entry.animator_candidates.Count > 1)
                    {
                        foreach (string candidate in entry.animator_candidates)
                            EditorGUILayout.LabelField($"Candidate: {candidate}", EditorStyles.miniLabel);
                    }
                    foreach (string warning in entry.review_warnings)
                        EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        internal static NpcAppearanceInventoryDocument ScanInventory()
        {
            var document = new NpcAppearanceInventoryDocument();
            var familyIds = new HashSet<string>(StringComparer.Ordinal);
            var appearanceIds = new Dictionary<string, List<NpcAppearanceInventoryEntry>>(StringComparer.Ordinal);

            foreach (PackageRoot package in PackageRoots)
            {
                if (!AssetDatabase.IsValidFolder(package.AssetPath))
                {
                    document.global_warnings.Add(
                        $"Licensed package root is unavailable: {package.AssetPath}");
                    continue;
                }

                string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { package.AssetPath });
                var paths = new List<string>(guids.Length);
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                        && path.StartsWith(package.AssetPath + "/", StringComparison.Ordinal))
                    {
                        paths.Add(path);
                    }
                }
                paths.Sort(StringComparer.Ordinal);

                foreach (string path in paths)
                {
                    NpcAppearanceInventoryEntry entry = InspectPrefab(package, path);
                    document.appearances.Add(entry);
                    familyIds.Add(entry.template_id_candidate);
                    if (!appearanceIds.TryGetValue(entry.appearance_id_candidate, out List<NpcAppearanceInventoryEntry>? duplicates))
                    {
                        duplicates = new List<NpcAppearanceInventoryEntry>();
                        appearanceIds.Add(entry.appearance_id_candidate, duplicates);
                    }
                    duplicates.Add(entry);
                }
            }

            document.appearances.Sort(CompareEntries);
            foreach (KeyValuePair<string, List<NpcAppearanceInventoryEntry>> pair in appearanceIds)
            {
                if (pair.Value.Count <= 1)
                    continue;
                foreach (NpcAppearanceInventoryEntry entry in pair.Value)
                    entry.review_warnings.Add($"Appearance ID candidate '{pair.Key}' is not unique.");
            }

            document.appearance_count = document.appearances.Count;
            document.family_count = familyIds.Count;
            if (document.appearance_count != document.expected_appearance_count)
            {
                document.global_warnings.Add(
                    $"Expected {document.expected_appearance_count} imported appearances but found {document.appearance_count}.");
            }
            return document;
        }

        internal static string CandidateId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var output = new StringBuilder(value.Length + 8);
            char previousSource = '\0';
            foreach (char current in value.Trim())
            {
                if (!char.IsLetterOrDigit(current))
                {
                    AppendSeparator(output);
                    previousSource = current;
                    continue;
                }

                bool camelBoundary = output.Length > 0
                    && char.IsUpper(current)
                    && char.IsLetter(previousSource)
                    && char.IsLower(previousSource);
                bool numericSuffixBoundary = output.Length > 0
                    && char.IsDigit(current)
                    && char.IsLetter(previousSource);
                if (camelBoundary || numericSuffixBoundary)
                    AppendSeparator(output);
                output.Append(char.ToUpperInvariant(current));
                previousSource = current;
            }

            return output.ToString().Trim('_');
        }

        private static NpcAppearanceInventoryEntry InspectPrefab(PackageRoot package, string path)
        {
            string relative = path.Substring(package.AssetPath.Length + 1);
            int slash = relative.IndexOf('/');
            string familyName = slash > 0 ? relative.Substring(0, slash) : Path.GetFileNameWithoutExtension(path);
            string prefabName = Path.GetFileNameWithoutExtension(path);
            var entry = new NpcAppearanceInventoryEntry
            {
                source_package = package.PackageId,
                family_name = familyName,
                template_id_candidate = CandidateId(familyName),
                appearance_id_candidate = CandidateId(prefabName),
                prefab_path = path,
            };

            GameObject? prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                entry.review_warnings.Add("Prefab could not be loaded.");
                return entry;
            }

            InspectRendererBounds(prefab, entry);

            Animator[] animators = prefab.GetComponentsInChildren<Animator>(includeInactive: true);
            entry.animator_count = animators.Length;
            bool anyRootMotion = false;
            bool anyInvalidAvatar = false;
            foreach (Animator candidate in animators)
            {
                string candidatePath = AnimatorPath(prefab, candidate);
                string candidateController = ControllerPath(candidate);
                string candidateAvatar = AvatarKind(candidate);
                entry.animator_candidates.Add(
                    $"{candidatePath} | controller={DisplayOrReview(candidateController)} "
                    + $"| avatar={candidateAvatar} | root_motion={candidate.applyRootMotion.ToString().ToLowerInvariant()}");
                anyRootMotion |= candidate.applyRootMotion;
                anyInvalidAvatar |= candidateAvatar == "MISSING_OR_INVALID";
            }
            entry.animator_candidates.Sort(StringComparer.Ordinal);
            if (anyRootMotion)
                entry.review_warnings.Add("At least one Animator candidate has root motion enabled and requires explicit review.");
            if (anyInvalidAvatar)
                entry.review_warnings.Add("At least one Animator candidate has no valid avatar.");
            if (animators.Length != 1)
            {
                entry.review_warnings.Add(
                    $"Primary Animator requires explicit review because the prefab contains {animators.Length} Animators.");
                return entry;
            }

            Animator animator = animators[0];
            entry.primary_animator_path_candidate = AnimatorPath(prefab, animator);
            entry.root_motion_enabled = animator.applyRootMotion;
            entry.avatar_kind = AvatarKind(animator);
            entry.animator_controller_path = ControllerPath(animator);
            if (animator.runtimeAnimatorController == null)
                entry.review_warnings.Add("Primary Animator has no runtime controller.");

            if (animator.runtimeAnimatorController != null)
            {
                var clipRows = new HashSet<string>(StringComparer.Ordinal);
                foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
                {
                    if (clip != null)
                        clipRows.Add($"{clip.name} | seconds={clip.length:F3}");
                }
                entry.animation_clips.AddRange(clipRows);
                entry.animation_clips.Sort(StringComparer.Ordinal);
            }

            HashSet<string> states = NpcVisualProfileEditor.CollectStateNames(animator.runtimeAnimatorController);
            entry.controller_states.AddRange(states);
            entry.controller_states.Sort(StringComparer.Ordinal);
            entry.idle_candidates = SuggestStates(entry.controller_states, "idle");
            entry.ready_candidates = SuggestStates(entry.controller_states, "ready");
            entry.walk_candidates = SuggestStates(entry.controller_states, "walk");
            entry.run_candidates = SuggestStates(entry.controller_states, "run");
            entry.attack_candidates = SuggestStates(entry.controller_states, "attack", "shot", "shoot");
            entry.spell_candidates = SuggestStates(entry.controller_states, "spell", "cast", "channel");
            entry.hit_candidates = SuggestStates(entry.controller_states, "hit");
            entry.death_candidates = SuggestStates(entry.controller_states, "death", "die");
            entry.status_reaction_candidates = SuggestStates(
                entry.controller_states,
                "stun",
                "fear",
                "knockdown",
                "stagger");

            if (entry.idle_candidates.Count == 0)
                entry.review_warnings.Add("No idle state candidate was inferred.");
            if (entry.walk_candidates.Count == 0)
                entry.review_warnings.Add("No walk state candidate was inferred.");
            if (entry.attack_candidates.Count == 0 && entry.spell_candidates.Count == 0)
                entry.review_warnings.Add("No attack or spell state candidate was inferred.");
            if (entry.death_candidates.Count == 0)
                entry.review_warnings.Add("No death state candidate was inferred; this appearance cannot ship without a death policy.");
            return entry;
        }

        private static void InspectRendererBounds(GameObject prefab, NpcAppearanceInventoryEntry entry)
        {
            GameObject? instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null)
            {
                entry.review_warnings.Add("Prefab could not be instantiated for renderer-bounds review.");
                return;
            }

            try
            {
                instance.hideFlags = HideFlags.HideAndDontSave;
                Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(includeInactive: true);
                entry.renderer_count = renderers.Length;
                bool hasBounds = false;
                Bounds combined = default;
                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null)
                        continue;
                    if (!hasBounds)
                    {
                        combined = renderer.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        combined.Encapsulate(renderer.bounds);
                    }
                }

                if (!hasBounds)
                {
                    entry.review_warnings.Add("Prefab contains no renderers for bounds review.");
                    return;
                }

                entry.renderer_bounds_center = NpcVector3Draft.From(combined.center - instance.transform.position);
                entry.renderer_bounds_size = NpcVector3Draft.From(combined.size);
                entry.ground_offset_candidate = combined.min.y - instance.transform.position.y;
            }
            finally
            {
                DestroyImmediate(instance);
            }
        }

        private static string AnimatorPath(GameObject prefab, Animator animator)
            => animator.transform == prefab.transform
                ? "."
                : AnimationUtility.CalculateTransformPath(animator.transform, prefab.transform);

        private static string ControllerPath(Animator animator)
            => animator.runtimeAnimatorController == null
                ? string.Empty
                : AssetDatabase.GetAssetPath(animator.runtimeAnimatorController);

        private static string AvatarKind(Animator animator)
            => animator.avatar == null || !animator.avatar.isValid
                ? "MISSING_OR_INVALID"
                : animator.avatar.isHuman ? "HUMANOID" : "GENERIC";

        private static List<string> SuggestStates(List<string> states, params string[] tokens)
        {
            var matches = new List<string>();
            foreach (string state in states)
            {
                foreach (string token in tokens)
                {
                    if (state.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    matches.Add(state);
                    break;
                }
            }
            return matches;
        }

        private static int CompareEntries(NpcAppearanceInventoryEntry left, NpcAppearanceInventoryEntry right)
        {
            int package = string.Compare(left.source_package, right.source_package, StringComparison.Ordinal);
            if (package != 0)
                return package;
            int family = string.Compare(left.template_id_candidate, right.template_id_candidate, StringComparison.Ordinal);
            if (family != 0)
                return family;
            int appearance = string.Compare(left.appearance_id_candidate, right.appearance_id_candidate, StringComparison.Ordinal);
            return appearance != 0
                ? appearance
                : string.Compare(left.prefab_path, right.prefab_path, StringComparison.Ordinal);
        }

        private static bool MatchesSearch(NpcAppearanceInventoryEntry entry, string search)
        {
            if (string.IsNullOrWhiteSpace(search))
                return true;
            string value = search.Trim();
            return entry.template_id_candidate.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.appearance_id_candidate.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.prefab_path.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DisplayOrReview(string value)
            => string.IsNullOrEmpty(value) ? "<review required>" : value;

        private static void AppendSeparator(StringBuilder output)
        {
            if (output.Length > 0 && output[output.Length - 1] != '_')
                output.Append('_');
        }

        private static void WriteDocument(NpcAppearanceInventoryDocument document, string path)
        {
            string fullPath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(Directory.GetCurrentDirectory(), path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(fullPath, JsonUtility.ToJson(document, prettyPrint: true) + Environment.NewLine);
        }
    }
}
