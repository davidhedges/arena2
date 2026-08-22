#nullable enable
using System;
using System.Collections.Generic;
using Arena.Presentation;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>
    /// Read-only resolved view of spell classification and each CombatAnimationSet's family binding.
    /// Fixed assignments are shown once because they intentionally ignore the active set;
    /// no-animation assignments are listed without per-set resolution rows.
    /// </summary>
    public sealed class SpellCastAnimationResolvedWindow : EditorWindow
    {
        private Vector2 _scroll;
        private SpellCastAnimationMap? _map;
        private Dictionary<string, SpellGameplayAuthoringFacts> _gameplay = new(StringComparer.Ordinal);
        private CombatAnimationSet[] _animationSets = Array.Empty<CombatAnimationSet>();
        private readonly List<ResolvedSpellRow> _rows = new();

        private sealed class ResolvedSpellRow
        {
            public string SpellId = string.Empty;
            public string Assignment = string.Empty;
            public SpellAnimationArchetype Archetype;
            public bool HasGameplay;
            public ResolvedSetRow[] Sets = Array.Empty<ResolvedSetRow>();
        }

        private readonly struct ResolvedSetRow
        {
            public ResolvedSetRow(
                string label,
                string family,
                bool resolved,
                SpellAnimationPresentationMode mode,
                SpellPlaybackLayer layer,
                string release,
                string enter,
                string loop)
            {
                Label = label;
                Family = family;
                Resolved = resolved;
                Mode = mode;
                Layer = layer;
                Release = release;
                Enter = enter;
                Loop = loop;
            }

            public string Label { get; }
            public string Family { get; }
            public bool Resolved { get; }
            public SpellAnimationPresentationMode Mode { get; }
            public SpellPlaybackLayer Layer { get; }
            public string Release { get; }
            public string Enter { get; }
            public string Loop { get; }
        }

        [MenuItem("Arena/Spell Animation/Resolved View")]
        public static void Open() => GetWindow<SpellCastAnimationResolvedWindow>("Cast Anim Resolved").Reload();

        private void OnEnable() => Reload();

        private void Reload()
        {
            SpellCastAnimationResolver.InvalidateCache();
            _map = SpellPresentationEditorData.FindFirstAsset<SpellCastAnimationMap>();
            _animationSets = SpellPresentationEditorData.LoadCombatAnimationSets();
            _gameplay = SpellPresentationEditorData.LoadSpellGameplayByActionId(out string warning);
            if (warning.Length > 0)
                Debug.LogWarning($"[ResolvedView] {warning}");
            BuildResolvedRows();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Cast-Animation Resolved View", EditorStyles.boldLabel);
                if (GUILayout.Button("Reload", GUILayout.Width(80)))
                    Reload();
            }

            if (_map == null)
            {
                EditorGUILayout.HelpBox("No SpellCastAnimationMap asset found.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (ResolvedSpellRow row in _rows)
                DrawSpell(row);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawSpell(ResolvedSpellRow row)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(row.SpellId, EditorStyles.boldLabel);
                string gameplayWarning = row.HasGameplay ? string.Empty : "  ‹NO GAMEPLAY ROW; SHOWING INSTANT›";
                EditorGUILayout.LabelField(
                    $"archetype: {row.Archetype}{gameplayWarning}    assignment: {row.Assignment}",
                    EditorStyles.miniLabel);

                foreach (ResolvedSetRow set in row.Sets)
                {
                    if (!set.Resolved)
                    {
                        EditorGUILayout.HelpBox(
                            $"{set.Label}: family '{set.Family}' did not resolve playable clips.",
                            MessageType.Warning);
                        continue;
                    }

                    EditorGUILayout.LabelField(
                        $"   [{set.Label}] family={set.Family} mode={set.Mode} layer={set.Layer}");
                    EditorGUILayout.LabelField(
                        $"      release={set.Release}  enter={set.Enter}  loop={set.Loop}",
                        EditorStyles.miniLabel);
                }
            }
        }

        private void BuildResolvedRows()
        {
            _rows.Clear();
            if (_map == null)
                return;

            foreach (SpellCastAnimationMap.Entry mapEntry in _map.Entries)
            {
                string spellId = (mapEntry.spellId ?? string.Empty).Trim().ToUpperInvariant();
                if (spellId.Length == 0)
                    continue;

                bool hasGameplay = _gameplay.TryGetValue(spellId, out SpellGameplayAuthoringFacts gameplay);
                SpellAnimationArchetype archetype = hasGameplay
                    ? SpellAnimationArchetypes.Derive(gameplay.CastTimeMs, gameplay.DeliveryKind)
                    : SpellAnimationArchetype.Instant;

                bool fixedAssignment = mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.Fixed;
                bool noAnimationAssignment = mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.NoAnimation;
                var resolvedSets = new List<ResolvedSetRow>();
                if (fixedAssignment)
                {
                    resolvedSets.Add(BuildResolvedSet(null, spellId, "fixed (all sets)", "fixed", archetype));
                }
                else if (!noAnimationAssignment)
                {
                    foreach (CombatAnimationSet set in _animationSets)
                    {
                        string family = set.TryGetSpellCastFamily(
                                mapEntry.motion,
                                out string familyBaseName,
                                out SpellCastMotion resolvedMotion)
                            ? resolvedMotion == mapEntry.motion
                                ? familyBaseName
                                : $"{familyBaseName} ({resolvedMotion} fallback)"
                            : "‹missing binding›";
                        resolvedSets.Add(BuildResolvedSet(set, spellId, set.name, family, archetype));
                    }
                }

                _rows.Add(new ResolvedSpellRow
                {
                    SpellId = spellId,
                    Assignment = fixedAssignment
                        ? "Fixed"
                        : noAnimationAssignment
                            ? "No animation"
                            : mapEntry.motion.ToString(),
                    Archetype = archetype,
                    HasGameplay = hasGameplay,
                    Sets = resolvedSets.ToArray(),
                });
            }
        }

        private static ResolvedSetRow BuildResolvedSet(
            CombatAnimationSet? set,
            string spellId,
            string label,
            string family,
            SpellAnimationArchetype archetype)
        {
            if (!SpellCastAnimationResolver.TryResolve(
                    set,
                    spellId,
                    archetype,
                    out WeaponSpellAnimationEntry entry))
            {
                return new ResolvedSetRow(label, family, false, default, default, "—", "—", "—");
            }

            return new ResolvedSetRow(
                label,
                family,
                true,
                entry.presentationMode,
                entry.playbackLayer,
                entry.ground != null ? entry.ground.name : "—",
                entry.holdOverride.enter != null ? entry.holdOverride.enter.name : "—",
                entry.holdOverride.idleLoop != null ? entry.holdOverride.idleLoop.name : "—");
        }
    }
}
