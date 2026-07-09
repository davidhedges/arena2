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
    /// Resolved-view for the cast-animation family system: for every mapped spell, shows the derived
    /// archetype, the flavor family, the composed clips per hand, and — the migration-critical part —
    /// which weapon sets still carry an <b>explicit</b> entry that would shadow the map. Also flags a
    /// family missing the clips its archetype needs. Read-only; edit-mode safe (derives the archetype
    /// from the catalog, so no play session needed).
    /// </summary>
    public sealed class SpellCastAnimationResolvedWindow : EditorWindow
    {
        private Vector2 _scroll;
        private SpellCastAnimationMap? _map;
        private SpellCastAnimationLibrary? _library;
        private Dictionary<string, SpellGameplayAuthoringFacts> _gameplay = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _explicitSetsBySpell = new(StringComparer.Ordinal);
        private readonly List<ResolvedSpellRow> _rows = new();

        private sealed class ResolvedSpellRow
        {
            public string SpellId = string.Empty;
            public string BaseName = string.Empty;
            public SpellAnimationArchetype Archetype;
            public bool HasGameplay;
            public bool HasFamily;
            public string[] ShadowSets = Array.Empty<string>();
            public ResolvedHandRow[] Hands = Array.Empty<ResolvedHandRow>();
        }

        private readonly struct ResolvedHandRow
        {
            public ResolvedHandRow(
                string label,
                bool composed,
                SpellAnimationArchetype archetype,
                SpellAnimationPresentationMode mode,
                SpellPlaybackLayer layer,
                string release,
                string enter,
                string loop)
            {
                Label = label;
                Composed = composed;
                Archetype = archetype;
                Mode = mode;
                Layer = layer;
                Release = release;
                Enter = enter;
                Loop = loop;
            }

            public string Label { get; }
            public bool Composed { get; }
            public SpellAnimationArchetype Archetype { get; }
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
            _map = SpellPresentationEditorData.FindFirstAsset<SpellCastAnimationMap>();
            _library = SpellPresentationEditorData.FindFirstAsset<SpellCastAnimationLibrary>();
            LoadGameplay();
            LoadExplicitEntries();
            BuildResolvedRows();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Cast-Animation Resolved View", EditorStyles.boldLabel);
                if (GUILayout.Button("Reload", GUILayout.Width(80))) Reload();
            }

            if (_map == null) { EditorGUILayout.HelpBox("No SpellCastAnimationMap asset found.", MessageType.Info); return; }
            if (_library == null) { EditorGUILayout.HelpBox("No SpellCastAnimationLibrary — run the rescan.", MessageType.Warning); return; }

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
                string familyWarning = row.HasFamily ? string.Empty : "  ‹NOT IN LIBRARY›";
                EditorGUILayout.LabelField(
                    $"archetype: {row.Archetype}{gameplayWarning}    family: {row.BaseName}{familyWarning}",
                    EditorStyles.miniLabel);

                foreach (ResolvedHandRow hand in row.Hands)
                    DrawResolved(hand);

                // Migration-critical: an explicit entry still shadows the map (map has no effect there).
                if (row.ShadowSets.Length > 0)
                    EditorGUILayout.HelpBox($"Still shadowed by explicit entries in: {string.Join(", ", row.ShadowSets)} — delete those to use the family.", MessageType.Warning);
                else
                    EditorGUILayout.LabelField("✓ no explicit entries shadow this (family is live everywhere)", EditorStyles.miniLabel);
            }
        }

        private static void DrawResolved(in ResolvedHandRow hand)
        {
            if (!hand.Composed)
            {
                EditorGUILayout.LabelField($"   [{hand.Label}] — no clips (family missing what {hand.Archetype} needs)");
                return;
            }
            EditorGUILayout.LabelField($"   [{hand.Label}] mode={hand.Mode} layer={hand.Layer}");
            EditorGUILayout.LabelField(
                $"      release={hand.Release}  enter={hand.Enter}  loop={hand.Loop}",
                EditorStyles.miniLabel);
        }

        private void LoadGameplay()
        {
            _gameplay = SpellPresentationEditorData.LoadSpellGameplayByActionId(out string warning);
            if (warning.Length > 0)
                Debug.LogWarning($"[ResolvedView] {warning}");
        }

        private void LoadExplicitEntries()
        {
            var bySpell = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (CombatAnimationSet set in SpellPresentationEditorData.LoadCombatAnimationSets())
            {
                if (set?.spells == null) continue;
                string setName = set.name;
                foreach (WeaponSpellAnimationEntry entry in set.spells)
                {
                    string id = entry.SpellIdOrEmpty;
                    if (id.Length == 0) continue;
                    if (!bySpell.TryGetValue(id, out var list)) bySpell[id] = list = new List<string>();
                    if (!list.Contains(setName)) list.Add(setName);
                }
            }
            _explicitSetsBySpell.Clear();
            foreach (var kvp in bySpell)
                _explicitSetsBySpell[kvp.Key] = kvp.Value.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        private void BuildResolvedRows()
        {
            _rows.Clear();
            if (_map == null || _library == null)
                return;

            foreach (SpellCastAnimationMap.Entry entry in _map.Entries)
            {
                string spellId = (entry.spellId ?? string.Empty).Trim().ToUpperInvariant();
                if (spellId.Length == 0)
                    continue;

                bool hasGameplay = _gameplay.TryGetValue(spellId, out SpellGameplayAuthoringFacts gameplay);
                SpellAnimationArchetype archetype = hasGameplay
                    ? SpellAnimationArchetypes.Derive(gameplay.CastTimeMs, gameplay.DeliveryKind)
                    : SpellAnimationArchetype.Instant;
                bool hasFamily = _library.TryGetFamily(
                    entry.baseName ?? string.Empty,
                    out SpellCastAnimationFamily family);
                ResolvedHandRow[] hands = Array.Empty<ResolvedHandRow>();
                if (hasFamily)
                {
                    hands = family.handStyle == SpellCastHandStyle.OneHand
                        ? new[]
                        {
                            BuildResolvedHand(spellId, family, SpellCastHand.Left, archetype, "left"),
                            BuildResolvedHand(spellId, family, SpellCastHand.Right, archetype, "right"),
                        }
                        : new[]
                        {
                            BuildResolvedHand(spellId, family, SpellCastHand.TwoHand, archetype, "two-hand"),
                        };
                }

                _rows.Add(new ResolvedSpellRow
                {
                    SpellId = spellId,
                    BaseName = entry.baseName ?? string.Empty,
                    Archetype = archetype,
                    HasGameplay = hasGameplay,
                    HasFamily = hasFamily,
                    ShadowSets = _explicitSetsBySpell.TryGetValue(spellId, out string[] shadowSets)
                        ? shadowSets
                        : Array.Empty<string>(),
                    Hands = hands,
                });
            }
        }

        private static ResolvedHandRow BuildResolvedHand(
            string spellId,
            in SpellCastAnimationFamily family,
            SpellCastHand hand,
            SpellAnimationArchetype archetype,
            string label)
        {
            if (!SpellCastAnimationComposer.TryCompose(
                    spellId,
                    family,
                    hand,
                    archetype,
                    out WeaponSpellAnimationEntry entry))
            {
                return new ResolvedHandRow(
                    label, false, archetype, default, default, "—", "—", "—");
            }

            return new ResolvedHandRow(
                label,
                true,
                archetype,
                entry.presentationMode,
                entry.playbackLayer,
                entry.ground != null ? entry.ground.name : "—",
                entry.holdOverride.enter != null ? entry.holdOverride.enter.name : "—",
                entry.holdOverride.idleLoop != null ? entry.holdOverride.idleLoop.name : "—");
        }

    }
}
