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
        private readonly List<(string spellId, string[] sets)> _explicitBySpell = new();

        [MenuItem("Arena/Spell Animation/Resolved View")]
        public static void Open() => GetWindow<SpellCastAnimationResolvedWindow>("Cast Anim Resolved").Reload();

        private void OnEnable() => Reload();

        private void Reload()
        {
            _map = SpellPresentationEditorData.FindFirstAsset<SpellCastAnimationMap>();
            _library = SpellPresentationEditorData.FindFirstAsset<SpellCastAnimationLibrary>();
            LoadGameplay();
            LoadExplicitEntries();
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
            foreach (SpellCastAnimationMap.Entry e in _map.Entries)
                DrawSpell(e);
            EditorGUILayout.EndScrollView();
        }

        private void DrawSpell(in SpellCastAnimationMap.Entry e)
        {
            string spellId = (e.spellId ?? string.Empty).Trim().ToUpperInvariant();
            if (spellId.Length == 0) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool hasFamily = _library!.TryGetFamily(e.baseName ?? string.Empty, out SpellCastAnimationFamily family);
                SpellAnimationArchetype archetype = _gameplay.TryGetValue(spellId, out SpellGameplayAuthoringFacts gp)
                    ? SpellAnimationArchetypes.Derive(gp.CastTimeMs, gp.DeliveryKind)
                    : SpellAnimationArchetype.Instant;

                EditorGUILayout.LabelField($"{spellId}", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"archetype: {archetype}    family: {e.baseName}{(hasFamily ? string.Empty : "  ‹NOT IN LIBRARY›")}", EditorStyles.miniLabel);

                if (hasFamily)
                {
                    if (family.handStyle == SpellCastHandStyle.OneHand)
                    {
                        DrawResolved(spellId, family, SpellCastHand.Left, archetype, "left");
                        DrawResolved(spellId, family, SpellCastHand.Right, archetype, "right");
                    }
                    else
                    {
                        DrawResolved(spellId, family, SpellCastHand.TwoHand, archetype, "two-hand");
                    }
                }

                // Migration-critical: an explicit entry still shadows the map (map has no effect there).
                string[] shadow = _explicitBySpell.FirstOrDefault(x => x.spellId == spellId).sets ?? Array.Empty<string>();
                if (shadow.Length > 0)
                    EditorGUILayout.HelpBox($"Still shadowed by explicit entries in: {string.Join(", ", shadow)} — delete those to use the family.", MessageType.Warning);
                else
                    EditorGUILayout.LabelField("✓ no explicit entries shadow this (family is live everywhere)", EditorStyles.miniLabel);
            }
        }

        private void DrawResolved(string spellId, in SpellCastAnimationFamily family, SpellCastHand hand, SpellAnimationArchetype archetype, string handLabel)
        {
            if (!SpellCastAnimationComposer.TryCompose(spellId, family, hand, archetype, out WeaponSpellAnimationEntry entry))
            {
                EditorGUILayout.LabelField($"   [{handLabel}] — no clips (family missing what {archetype} needs)");
                return;
            }
            string ground = entry.ground != null ? entry.ground.name : "—";
            string en = entry.holdOverride.enter != null ? entry.holdOverride.enter.name : "—";
            string loop = entry.holdOverride.idleLoop != null ? entry.holdOverride.idleLoop.name : "—";
            EditorGUILayout.LabelField($"   [{handLabel}] mode={entry.presentationMode} layer={entry.playbackLayer}");
            EditorGUILayout.LabelField($"      release={ground}  enter={en}  loop={loop}", EditorStyles.miniLabel);
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
            _explicitBySpell.Clear();
            foreach (var kvp in bySpell) _explicitBySpell.Add((kvp.Key, kvp.Value.ToArray()));
        }

    }
}
