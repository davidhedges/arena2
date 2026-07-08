#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Arena.Presentation;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>
    /// Auto-scans the Kevin Iglesias MagicAttacks pack into a <see cref="SpellCastAnimationLibrary"/>
    /// by naming convention: every base is a triple <c>HumanM@{Base}{Hand}.anim</c> (one-shot) /
    /// <c>… - Load.anim</c> (loop) / <c>… - Cast.anim</c> (final cast), where <c>{Hand}</c> is
    /// <c>_L</c>/<c>_R</c> on one-handed bases and absent on two-handed bases. This is the "owner
    /// barely touches the library" tool (design doc §5); it never edits gameplay or spell mappings.
    /// Non-destructive: nothing consumes the library until the resolver compose step (phase 2).
    /// </summary>
    public static class SpellCastAnimationLibraryBuilder
    {
        private const string ScanRoot =
            "Assets/Arena/Content/Animation/Extracted/KevinIglesias/Human Animations/Animations/Male/Combat/Spellcasting/MagicAttacks";
        private const string LibraryPath = "Assets/Arena/Resources/SpellCastAnimationLibrary.asset";
        private const string HumanPrefix = "HumanM@";

        private enum Variant { OneShot, Load, Cast }

        private readonly struct ParsedClip
        {
            public ParsedClip(string baseName, SpellCastHand? hand, Variant variant, bool prefixed, AnimationClip clip, string path)
            {
                BaseName = baseName;
                Hand = hand;
                Variant = variant;
                Prefixed = prefixed;
                Clip = clip;
                Path = path;
            }

            public string BaseName { get; }
            /// <summary>Left/Right for a one-handed clip; null for a two-handed (no-suffix) clip.</summary>
            public SpellCastHand? Hand { get; }
            public Variant Variant { get; }
            /// <summary>Carries the canonical <c>HumanM@</c> prefix — preferred over stray copies.</summary>
            public bool Prefixed { get; }
            public AnimationClip Clip { get; }
            public string Path { get; }
        }

        [MenuItem("Arena/Spell Animation/Rescan Cast Families")]
        public static void Rescan()
        {
            if (!AssetDatabase.IsValidFolder(ScanRoot))
            {
                EditorUtility.DisplayDialog(
                    "Rescan Cast Families",
                    $"MagicAttacks folder not found:\n{ScanRoot}",
                    "OK");
                return;
            }

            var parsed = new List<ParsedClip>();
            var skipped = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { ScanRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null)
                    continue;

                if (TryParse(path, clip, out ParsedClip pc))
                    parsed.Add(pc);
                else
                    skipped.Add(Path.GetFileName(path));
            }

            List<SpellCastAnimationFamily> families = BuildFamilies(parsed, out List<string> notes);
            WriteLibrary(families);

            var summary = new StringBuilder();
            summary.AppendLine($"Scanned {parsed.Count} clips → {families.Count} families.");
            int oneHand = families.Count(f => f.handStyle == SpellCastHandStyle.OneHand);
            summary.AppendLine($"  {oneHand} one-handed (L/R), {families.Count - oneHand} two-handed.");
            if (skipped.Count > 0)
                summary.AppendLine($"  {skipped.Count} unparseable clips skipped: {string.Join(", ", skipped.Take(8))}{(skipped.Count > 8 ? " …" : string.Empty)}");
            foreach (string note in notes.Take(20))
                summary.AppendLine($"  ! {note}");
            if (notes.Count > 20)
                summary.AppendLine($"  … +{notes.Count - 20} more notes");

            Debug.Log($"[SpellCastAnimationLibraryBuilder] {summary}");
            EditorUtility.DisplayDialog("Rescan Cast Families", summary.ToString(), "OK");
        }

        /// <summary>
        /// Parses one clip path into (base, hand, variant). Filename shape after the optional
        /// <c>HumanM@</c> prefix: <c>{Base}{_L|_R|""}{" - Load"|" - Cast"|""}</c>. Variant suffix is
        /// stripped first (it is last in the name), then the hand suffix, leaving the base.
        /// </summary>
        private static bool TryParse(string path, AnimationClip clip, out ParsedClip parsed)
        {
            parsed = default;
            string name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name))
                return false;

            bool prefixed = name.StartsWith(HumanPrefix, StringComparison.Ordinal);
            if (prefixed)
                name = name.Substring(HumanPrefix.Length);

            Variant variant = Variant.OneShot;
            if (EndsWith(name, " - Load")) { variant = Variant.Load; name = name[..^" - Load".Length]; }
            else if (EndsWith(name, " - Cast")) { variant = Variant.Cast; name = name[..^" - Cast".Length]; }

            SpellCastHand? hand = null;
            if (name.EndsWith("_L", StringComparison.Ordinal)) { hand = SpellCastHand.Left; name = name[..^2]; }
            else if (name.EndsWith("_R", StringComparison.Ordinal)) { hand = SpellCastHand.Right; name = name[..^2]; }

            name = name.Trim();
            if (name.Length == 0)
                return false;

            parsed = new ParsedClip(name, hand, variant, prefixed, clip, path);
            return true;
        }

        private static List<SpellCastAnimationFamily> BuildFamilies(List<ParsedClip> parsed, out List<string> notes)
        {
            notes = new List<string>();

            // Group by base name. A base is OneHand iff any of its clips carried an _L/_R suffix.
            var byBase = new Dictionary<string, List<ParsedClip>>(StringComparer.OrdinalIgnoreCase);
            foreach (ParsedClip pc in parsed)
            {
                if (!byBase.TryGetValue(pc.BaseName, out List<ParsedClip>? clips))
                    byBase[pc.BaseName] = clips = new List<ParsedClip>();
                clips.Add(pc);
            }

            var families = new List<SpellCastAnimationFamily>(byBase.Count);
            foreach (KeyValuePair<string, List<ParsedClip>> kvp in byBase.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string baseName = kvp.Key;
                List<ParsedClip> clips = kvp.Value;
                bool oneHand = clips.Any(c => c.Hand.HasValue);

                var family = new SpellCastAnimationFamily
                {
                    baseName = baseName,
                    handStyle = oneHand ? SpellCastHandStyle.OneHand : SpellCastHandStyle.TwoHand,
                };

                var left = new SpellCastClipTriple();
                var right = new SpellCastClipTriple();
                var twoHand = new SpellCastClipTriple();
                foreach (ParsedClip c in clips)
                {
                    if (oneHand && !c.Hand.HasValue)
                    {
                        notes.Add($"{baseName}: no-hand clip in a one-handed base ignored ({Path.GetFileName(c.Path)})");
                        continue;
                    }

                    SpellCastHand slot = c.Hand ?? SpellCastHand.TwoHand;
                    ref SpellCastClipTriple triple = ref twoHand;
                    if (slot == SpellCastHand.Left) triple = ref left;
                    else if (slot == SpellCastHand.Right) triple = ref right;

                    AssignVariant(ref triple, c, baseName, slot, notes);
                }

                family.left = left;
                family.right = right;
                family.twoHand = twoHand;

                // Coverage notes — surfaced, never fatal (a base missing a Cast clip just can't be a
                // charged release; a base missing Load can't hold).
                NoteCoverage(baseName, family, notes);
                families.Add(family);
            }

            return families;
        }

        private static void AssignVariant(ref SpellCastClipTriple triple, in ParsedClip c, string baseName, SpellCastHand slot, List<string> notes)
        {
            ref AnimationClip? existing = ref triple.oneShot;
            if (c.Variant == Variant.Load) existing = ref triple.load;
            else if (c.Variant == Variant.Cast) existing = ref triple.cast;

            // Prefer the canonical HumanM@ clip when a stray unprefixed copy collides on the same slot.
            if (existing != null)
            {
                if (c.Prefixed && !IsPrefixed(existing))
                    existing = c.Clip; // upgrade to the canonical copy
                else
                    notes.Add($"{baseName} [{slot}/{c.Variant}]: duplicate clip, kept {existing.name}, ignored {c.Clip.name}");
                return;
            }

            existing = c.Clip;
        }

        private static bool IsPrefixed(AnimationClip clip)
            => clip != null && clip.name.StartsWith(HumanPrefix, StringComparison.Ordinal);

        private static void NoteCoverage(string baseName, in SpellCastAnimationFamily family, List<string> notes)
        {
            if (family.handStyle == SpellCastHandStyle.OneHand)
            {
                NoteTriple(baseName, "Left", family.left, notes);
                NoteTriple(baseName, "Right", family.right, notes);
            }
            else
            {
                NoteTriple(baseName, "TwoHand", family.twoHand, notes);
            }
        }

        private static void NoteTriple(string baseName, string label, in SpellCastClipTriple triple, List<string> notes)
        {
            if (triple.IsEmpty)
                return; // a one-handed base with only one hand populated is legitimate
            if (!triple.HasOneShot) notes.Add($"{baseName} [{label}]: missing one-shot clip");
            if (!triple.HasCast) notes.Add($"{baseName} [{label}]: missing '- Cast' clip (no charged release)");
            if (triple.load == null) notes.Add($"{baseName} [{label}]: missing '- Load' clip (no hold loop)");
        }

        private static void WriteLibrary(List<SpellCastAnimationFamily> families)
        {
            string dir = Path.GetDirectoryName(LibraryPath)!;
            if (!AssetDatabase.IsValidFolder(dir))
                Directory.CreateDirectory(dir);

            var library = AssetDatabase.LoadAssetAtPath<SpellCastAnimationLibrary>(LibraryPath);
            bool created = library == null;
            if (created)
            {
                library = ScriptableObject.CreateInstance<SpellCastAnimationLibrary>();
                AssetDatabase.CreateAsset(library, LibraryPath);
            }

            library!.EditorReplaceFamilies(families);
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(LibraryPath);
        }

        private static bool EndsWith(string s, string suffix)
            => s.EndsWith(suffix, StringComparison.Ordinal);
    }
}
