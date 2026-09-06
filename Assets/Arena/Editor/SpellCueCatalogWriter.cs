#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Arena.Editor
{
    /// <summary>
    /// Generated fields for one cue slot. SortOrder identifies the existing ABILITY row; ownership
    /// metadata, owner identity, ordering, and unrelated fields are preserved during materialization.
    /// ProjectileSequenceIndex is null when the generated cue has no sequence condition.
    /// </summary>
    public readonly struct SpellCueRow
    {
        public SpellCueRow(
            string slot,
            string trigger,
            string anchor,
            string vfxId,
            string attachMode,
            string vfxRole,
            string lifecycle,
            int? projectileSequenceIndex,
            int durationMs,
            int sortOrder)
        {
            Slot = slot;
            Trigger = trigger;
            Anchor = anchor;
            VfxId = vfxId;
            AttachMode = attachMode;
            VfxRole = vfxRole;
            Lifecycle = lifecycle;
            ProjectileSequenceIndex = projectileSequenceIndex;
            DurationMs = durationMs;
            SortOrder = sortOrder;
        }

        public string Slot { get; }
        public string Trigger { get; }
        public string Anchor { get; }
        public string VfxId { get; }
        public string AttachMode { get; }
        public string VfxRole { get; }
        public string Lifecycle { get; }
        public int? ProjectileSequenceIndex { get; }
        public int DurationMs { get; }
        public int SortOrder { get; }
    }

    /// <summary>
    /// Materializes declared GENERATED cues in progression_catalog.shared.json. The file-writing
    /// entry point validates ownership and fresh global generation before calling the surgical
    /// formatter. Manual/legacy rows and bytes outside the affected owner remain unchanged.
    /// <para>
    /// SpliceOwnerCues is an in-memory formatter, including insertion support for explicit staging;
    /// it is not permission to write new or profile-dependent runtime content. WriteOwnerCues
    /// accepts only existing generated-owned rows that match every current animation context.
    /// </para>
    /// <para>
    /// Runtime catalog changes require the canonical data-preserving local setup to rebuild the
    /// embedded catalog. Resyncing an older module does not publish edited source JSON.
    /// </para>
    /// </summary>
    public static class SpellCueCatalogWriter
    {
        public const string Generated = "GENERATED";
        public const string Manual = "MANUAL";
        public const string Legacy = "LEGACY";
        private const string CombatVfxCuesKey = "\"combat_vfx_cues\"";

        public static List<string> CompareGeneratedRows(SpellCueRow expected, SpellCueRow actual)
        {
            var differences = new List<string>();
            void Compare(string field, object? left, object? right)
            {
                if (!Equals(left, right)) differences.Add($"{field}: {left ?? "<none>"} -> {right ?? "<none>"}");
            }
            Compare("slot", expected.Slot, actual.Slot);
            Compare("trigger", expected.Trigger, actual.Trigger);
            Compare("anchor", expected.Anchor, actual.Anchor);
            Compare("vfx_id", expected.VfxId, actual.VfxId);
            Compare("attach_mode", expected.AttachMode, actual.AttachMode);
            Compare("vfx_role", expected.VfxRole, actual.VfxRole);
            Compare("lifecycle", expected.Lifecycle, actual.Lifecycle);
            Compare("projectile_sequence_index", expected.ProjectileSequenceIndex, actual.ProjectileSequenceIndex);
            Compare("duration_ms", expected.DurationMs, actual.DurationMs);
            Compare("sort_order", expected.SortOrder, actual.SortOrder);
            return differences;
        }

        /// <summary>Checks authoring metadata without changing or projecting runtime cue fields.</summary>
        public static List<string> ValidateOwnership(string catalogJson)
        {
            var errors = new List<string>();
            if (!TryFindCombatVfxCuesArray(catalogJson, out int open, out int close))
                throw new InvalidOperationException("combat_vfx_cues array not found in catalog JSON.");
            string body = catalogJson.Substring(open + 1, close - open - 1);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var span in TokenizeObjectSpans(body))
            {
                var fields = ParseFlatObject(body.Substring(span.Start, span.End - span.Start), out _, out _);
                TryGetString(fields, "owner_kind", out string kind);
                TryGetString(fields, "owner_id", out string owner);
                TryGetString(fields, "authoring_mode", out string mode);
                TryGetString(fields, "authoring_reason", out string reason);
                TryGetString(fields, "slot", out string slot);
                string identity = NormalizeOwnerId(kind) + "/" + NormalizeOwnerId(owner);
                if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(owner))
                    errors.Add(identity + ": cue owner_kind and owner_id are required.");
                if (!TryGetInt(fields, "sort_order", out int order))
                    errors.Add(identity + ": missing integer sort_order.");
                identity += "/" + order;
                if (!identities.Add(identity)) errors.Add(identity + ": duplicate cue identity.");
                if (mode != Generated && mode != Manual && mode != Legacy)
                    errors.Add(identity + ": authoring_mode must be GENERATED, MANUAL, or LEGACY.");
                else if (mode == Generated)
                {
                    if (NormalizeOwnerId(kind) != "ABILITY" || string.IsNullOrWhiteSpace(slot))
                        errors.Add(identity + ": generated cues require an ABILITY owner and an explicit slot.");
                    if (!string.IsNullOrWhiteSpace(reason))
                        errors.Add(identity + ": generated cues must not retain a manual exception reason.");
                    foreach (var field in fields)
                        if ((!CanonicalKeyOrder.ContainsKey(field.Key)
                            && field.Key != "authoring_mode" && field.Key != "authoring_reason")
                            || field.Key == "hit_index")
                            errors.Add(identity + ": generated cues cannot own unsupported field '" + field.Key + "'.");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(reason))
                        errors.Add(identity + ": manual and legacy cues require an authoring_reason.");
                    if (mode == Legacy && NormalizeOwnerId(kind) == "ABILITY")
                        errors.Add(identity + ": legacy compatibility cues require a non-ABILITY owner.");
                }
            }
            return errors;
        }

        // Canonical key order for a re-serialised row. `slot` (the new author-time key) sits right
        // after `owner_id`, which is the placement that keeps FIREBALL's first write a pure insertion
        // (owner_id already carries a trailing comma, so no other line changes). Existing FIREBALL
        // rows are already authored in exactly this order, so re-serialising them is byte-identical
        // apart from the inserted `slot` line. Any key not listed here (an unmodelled legacy field)
        // is preserved and appended after the known keys in its original relative order.
        private static readonly IReadOnlyDictionary<string, int> CanonicalKeyOrder =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["owner_kind"] = 0,
                ["owner_id"] = 1,
                ["slot"] = 2,
                ["trigger"] = 3,
                ["hit_index"] = 4,
                ["anchor"] = 5,
                ["vfx_id"] = 6,
                ["attach_mode"] = 7,
                ["vfx_role"] = 8,
                ["lifecycle"] = 9,
                ["projectile_sequence_index"] = 10,
                ["duration_ms"] = 11,
                ["sort_order"] = 12,
            };

        private const int UnknownKeyOrderBase = 1000;

        /// <summary>
        /// Reads <paramref name="catalogPath"/>, splices <paramref name="rows"/> into the ABILITY
        /// owner's rows, and writes the result back. Other owner kinds remain untouched even when
        /// they share the same owner ID. Only declared GENERATED rows reproduced from fresh inputs
        /// in every animation context can be written. Unowned additions must be declared first.
        /// Returns <c>true</c> if the file content changed.
        /// </summary>
        public static bool WriteOwnerCues(
            string catalogPath, string ownerId, IReadOnlyList<SpellCueRow> rows, string expectedCatalogJson)
        {
            string original = File.ReadAllText(catalogPath);
            if (!string.Equals(original, expectedCatalogJson, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The catalog changed since this preview was loaded. Reload the spell authoring window before writing generated cues.");
            var errors = ValidateOwnership(original);
            var window = UnityEngine.ScriptableObject.CreateInstance<SpellAuthoringWindow>();
            try { errors.AddRange(window.ValidateCueWriteRequest(original, ownerId, rows)); }
            finally { UnityEngine.Object.DestroyImmediate(window); }
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join("\n", errors));
            if (File.ReadAllText(catalogPath) != original)
                throw new InvalidOperationException("The catalog changed during generation validation. Reload before writing.");
            string updated = SpliceOwnerCues(original, ownerId, rows);
            if (string.Equals(original, updated, StringComparison.Ordinal))
                return false;

            File.WriteAllText(catalogPath, updated);
            return true;
        }

        /// <summary>
        /// Returns <paramref name="catalogJson"/> with <paramref name="ownerId"/>'s
        /// <c>combat_vfx_cues</c> materialised from <paramref name="rows"/>. Pure and deterministic —
        /// every byte outside the owner's rows is preserved exactly. Correspondence is by
        /// <c>sort_order</c> within the selected ABILITY owner:
        /// <list type="bullet">
        /// <item>a provided row whose <c>sort_order</c> matches an existing owner row <b>updates</b> it
        /// in place (generator-owned fields overwritten, <c>slot</c> inserted, everything else — incl.
        /// legacy override keys — preserved), so an unchanged spell (e.g. FIREBALL) diffs to only the
        /// inserted <c>slot</c> lines;</item>
        /// <item>a provided row with no matching existing row is <b>inserted</b> — grouped after the
        /// owner's last existing row, or (for a brand-new owner) after the last row in the array;</item>
        /// <item>an existing owner row with no matching provided row is <b>kept</b> as-is — the writer
        /// never silently deletes authored data (the window surfaces it as a catalog-only slot to remove
        /// by hand).</item>
        /// </list>
        /// </summary>
        public static string SpliceOwnerCues(string catalogJson, string ownerId, IReadOnlyList<SpellCueRow> rows)
        {
            if (catalogJson == null) throw new ArgumentNullException(nameof(catalogJson));
            if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("ownerId is required", nameof(ownerId));
            if (rows == null) throw new ArgumentNullException(nameof(rows));

            string normalizedOwner = NormalizeOwnerId(ownerId);
            if (!TryFindCombatVfxCuesArray(catalogJson, out int openBracket, out int closeBracket))
                throw new InvalidOperationException("combat_vfx_cues array not found in catalog JSON.");

            string body = catalogJson.Substring(openBracket + 1, closeBracket - openBracket - 1);
            List<(int Start, int End)> objectSpans = TokenizeObjectSpans(body);

            // The generated rows, indexed by the sort_order they target.
            var rowsBySortOrder = new Dictionary<int, SpellCueRow>();
            foreach (SpellCueRow row in rows)
            {
                if (rowsBySortOrder.ContainsKey(row.SortOrder))
                    throw new InvalidOperationException(
                        $"Two generated rows target sort_order {row.SortOrder} for owner '{ownerId}'.");
                rowsBySortOrder.Add(row.SortOrder, row);
            }

            // Which spans belong to this owner, in file order, with the sort_order each carries.
            var targets = new List<(int Start, int End, int SortOrder)>();
            var targetSortOrders = new HashSet<int>();
            const string ownerKind = "ABILITY";
            foreach ((int start, int end) in objectSpans)
            {
                string objText = body.Substring(start, end - start);
                List<KeyValuePair<string, object>> fields = ParseFlatObject(objText, out _, out _);
                if (!TryGetString(fields, "owner_kind", out string kind)
                    || !string.Equals(NormalizeOwnerId(kind), ownerKind, StringComparison.Ordinal)
                    || !TryGetString(fields, "owner_id", out string owner)
                    || !string.Equals(NormalizeOwnerId(owner), normalizedOwner, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryGetInt(fields, "sort_order", out int sortOrder))
                    throw new InvalidOperationException(
                        $"Owner '{ownerId}' has a combat_vfx_cues row with no integer sort_order.");
                if (!targetSortOrders.Add(sortOrder))
                    throw new InvalidOperationException(
                        $"Owner '{ownerId}' has duplicate sort_order {sortOrder}; cannot match generated rows unambiguously.");
                if (rowsBySortOrder.TryGetValue(sortOrder, out var requested))
                {
                    if (!TryGetString(fields, "authoring_mode", out string mode) || mode != Generated)
                        throw new InvalidOperationException($"Owner '{ownerId}' row {sortOrder} is not GENERATED; manual and legacy cues cannot be overwritten.");
                    if (TryGetString(fields, "slot", out string slot) && !string.IsNullOrWhiteSpace(slot)
                        && (!SpellAuthoringWindow.TryNormalizeExplicitSlotKey(slot, out string normalizedSlot)
                            || !SpellAuthoringWindow.TryNormalizeExplicitSlotKey(requested.Slot, out string requestedSlot)
                            || normalizedSlot != requestedSlot))
                        throw new InvalidOperationException($"Owner '{ownerId}' row {sortOrder} targets a different slot.");
                }
                targets.Add((start, end, sortOrder));
            }

            // Partition provided rows: those matching an existing owner row (update) vs new (insert).
            var newRows = new List<SpellCueRow>();
            foreach (SpellCueRow row in rows)
            {
                if (!targetSortOrders.Contains(row.SortOrder))
                    newRows.Add(row);
            }
            newRows.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));

            if (targets.Count == 0 && newRows.Count == 0)
                throw new InvalidOperationException(
                    $"No combat_vfx_cues rows found for owner '{ownerId}' and no rows to insert.");

            string rowIndent = DetectRowIndent(body, objectSpans);
            string newFieldIndent = rowIndent + "  ";

            var rebuilt = new StringBuilder(body.Length + 128 + 96 * (targets.Count + newRows.Count));
            if (targets.Count > 0)
            {
                // Owner's rows are (virtually always) a contiguous run in the file. Rebuild that run from
                // the merged existing rows + any inserted rows, laid out in sort_order — so an update-only
                // owner (FIREBALL) is byte-identical apart from the slot lines, and inserted rows land in
                // their correct sort position rather than tacked on the end. A non-contiguous owner (a
                // foreign row interleaved) falls back to keeping existing rows in place and appending the
                // new ones — still correct at runtime (sort_order governs), just not re-sorted in the file.
                int runStart = targets[0].Start;
                int runEnd = targets[targets.Count - 1].End;
                bool contiguous = true;
                for (int i = 1; i < targets.Count && contiguous; i++)
                    contiguous = !body.Substring(targets[i - 1].End, targets[i].Start - targets[i - 1].End).Contains('{');

                if (contiguous)
                {
                    // Inter-row separator, taken from the file so the rebuilt run matches byte-for-byte.
                    string sep = targets.Count >= 2
                        ? body.Substring(targets[0].End, targets[1].Start - targets[0].End)
                        : ",\n" + rowIndent;

                    var block = new List<(int SortOrder, string Text)>(targets.Count + newRows.Count);
                    foreach ((int start, int end, int sortOrder) in targets)
                        block.Add((sortOrder, rowsBySortOrder.TryGetValue(sortOrder, out SpellCueRow m)
                            ? MergeRow(body.Substring(start, end - start), m)
                            : body.Substring(start, end - start)));
                    foreach (SpellCueRow nr in newRows)
                        block.Add((nr.SortOrder, SerializeNewRow(nr, ownerKind, ownerId, newFieldIndent, rowIndent)));
                    block.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));

                    rebuilt.Append(body, 0, runStart);
                    for (int i = 0; i < block.Count; i++)
                    {
                        if (i > 0) rebuilt.Append(sep);
                        rebuilt.Append(block[i].Text);
                    }
                    rebuilt.Append(body, runEnd, body.Length - runEnd);
                }
                else
                {
                    int cursor = 0;
                    for (int t = 0; t < targets.Count; t++)
                    {
                        (int start, int end, int sortOrder) = targets[t];
                        rebuilt.Append(body, cursor, start - cursor);
                        rebuilt.Append(rowsBySortOrder.TryGetValue(sortOrder, out SpellCueRow m)
                            ? MergeRow(body.Substring(start, end - start), m)
                            : body.Substring(start, end - start));
                        cursor = end; // ends at runEnd after the last target
                    }
                    foreach (SpellCueRow nr in newRows)
                        rebuilt.Append(",\n").Append(rowIndent)
                            .Append(SerializeNewRow(nr, ownerKind, ownerId, newFieldIndent, rowIndent));
                    rebuilt.Append(body, runEnd, body.Length - runEnd);
                }
            }
            else
            {
                // Brand-new owner: append its rows after the last row already in the array.
                if (objectSpans.Count == 0)
                    throw new InvalidOperationException(
                        "combat_vfx_cues array has no rows to anchor a new owner's insertion.");
                int lastEnd = objectSpans[objectSpans.Count - 1].End;
                rebuilt.Append(body, 0, lastEnd);
                foreach (SpellCueRow nr in newRows)
                    rebuilt.Append(",\n").Append(rowIndent)
                        .Append(SerializeNewRow(nr, "ABILITY", ownerId, newFieldIndent, rowIndent));
                rebuilt.Append(body, lastEnd, body.Length - lastEnd);
            }

            return catalogJson.Substring(0, openBracket + 1)
                + rebuilt
                + catalogJson.Substring(closeBracket);
        }

        // ---- row merge + serialization ---------------------------------------------------------

        // A row's fields as an insertion-ordered key set with an index for stable tiebreaking of
        // unknown (legacy) keys under the canonical order.
        private sealed class OrderedFields
        {
            public readonly List<string> Keys = new();
            public readonly Dictionary<string, object> Values = new(StringComparer.Ordinal);
            public readonly Dictionary<string, int> OriginalIndex = new(StringComparer.Ordinal);

            public void Set(string key, object value)
            {
                if (!Values.ContainsKey(key))
                {
                    OriginalIndex[key] = Keys.Count;
                    Keys.Add(key);
                }
                Values[key] = value;
            }

            public void Remove(string key)
            {
                if (Values.Remove(key))
                    Keys.Remove(key);
            }
        }

        // Overwrites the generator-owned fields of an existing row with the generated values, inserts
        // the author-time `slot` key, and preserves everything else (owner_kind/owner_id, sort_order,
        // hit_index, and any unmodelled legacy override keys). Re-serialises in canonical key order,
        // reproducing the source row's exact indentation.
        private static string MergeRow(string objText, SpellCueRow row)
        {
            List<KeyValuePair<string, object>> fields = ParseFlatObject(objText, out string fieldIndent, out string braceIndent);
            var f = new OrderedFields();
            foreach (KeyValuePair<string, object> field in fields)
                f.Set(field.Key, field.Value);
            ApplyGeneratorFields(f, row);
            // owner_kind, owner_id, sort_order and any legacy override keys are left as parsed.
            return SerializeCanonical(f, fieldIndent, braceIndent);
        }

        // Serialises a brand-new row (no existing object to merge) — used for inserted cues. Supplies
        // owner_kind/owner_id/sort_order the merge path would otherwise preserve from the source row.
        private static string SerializeNewRow(
            SpellCueRow row, string ownerKind, string ownerId, string fieldIndent, string braceIndent)
        {
            var f = new OrderedFields();
            f.Set("owner_kind", ownerKind);
            f.Set("owner_id", ownerId);
            ApplyGeneratorFields(f, row);
            f.Set("sort_order", row.SortOrder);
            f.Set("authoring_mode", Generated);
            return SerializeCanonical(f, fieldIndent, braceIndent);
        }

        // The author-time `slot` key + generator-owned fields (design doc §3.2 / Appendix B). Shared by
        // the update (merge) and insert (new) paths so both emit identical field shapes.
        private static void ApplyGeneratorFields(OrderedFields f, SpellCueRow row)
        {
            f.Set("slot", row.Slot);
            f.Set("trigger", row.Trigger);
            f.Set("anchor", row.Anchor);
            f.Set("vfx_id", row.VfxId);
            f.Set("attach_mode", row.AttachMode);
            f.Set("vfx_role", row.VfxRole);
            f.Set("lifecycle", row.Lifecycle);
            if (row.ProjectileSequenceIndex.HasValue)
                f.Set("projectile_sequence_index", row.ProjectileSequenceIndex.Value);
            else
                f.Remove("projectile_sequence_index");
            f.Set("duration_ms", row.DurationMs);
        }

        private static string SerializeCanonical(OrderedFields f, string fieldIndent, string braceIndent)
        {
            var keys = new List<string>(f.Keys);
            keys.Sort((a, b) => OrderKey(a, f.OriginalIndex[a]).CompareTo(OrderKey(b, f.OriginalIndex[b])));

            var sb = new StringBuilder();
            sb.Append('{');
            for (int i = 0; i < keys.Count; i++)
            {
                sb.Append('\n').Append(fieldIndent).Append('"').Append(keys[i]).Append("\": ");
                AppendValue(sb, f.Values[keys[i]]);
                if (i < keys.Count - 1)
                    sb.Append(',');
            }
            sb.Append('\n').Append(braceIndent).Append('}');
            return sb.ToString();
        }

        // The indentation before a row's opening brace (e.g. 4 spaces), detected from the first row so
        // inserted rows match the file's layout.
        private static string DetectRowIndent(string body, List<(int Start, int End)> spans)
        {
            if (spans.Count == 0)
                return "    ";
            int start = spans[0].Start;
            int j = start;
            while (j > 0 && (body[j - 1] == ' ' || body[j - 1] == '\t'))
                j--;
            return body.Substring(j, start - j);
        }

        private static int OrderKey(string key, int originalIndex)
            => CanonicalKeyOrder.TryGetValue(key, out int priority)
                ? priority
                : UnknownKeyOrderBase + originalIndex;

        private static void AppendValue(StringBuilder sb, object value)
        {
            switch (value)
            {
                case string s:
                    sb.Append('"').Append(EscapeJsonString(s)).Append('"');
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported cue value type: {value?.GetType().Name ?? "null"}.");
            }
        }

        private static string EscapeJsonString(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ' || c > '~')
                            sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        // ---- catalog scanning (string-aware) ---------------------------------------------------

        private static bool TryFindCombatVfxCuesArray(string json, out int openBracket, out int closeBracket)
        {
            openBracket = -1;
            closeBracket = -1;

            int keyIndex = json.IndexOf(CombatVfxCuesKey, StringComparison.Ordinal);
            if (keyIndex < 0)
                return false;

            int i = keyIndex + CombatVfxCuesKey.Length;
            while (i < json.Length && json[i] != '[')
            {
                // Only whitespace and the ':' separator are expected between the key and its array.
                if (json[i] != ':' && !char.IsWhiteSpace(json[i]))
                    return false;
                i++;
            }
            if (i >= json.Length)
                return false;

            openBracket = i;
            int depth = 0;
            bool inString = false;
            for (int j = openBracket; j < json.Length; j++)
            {
                char c = json[j];
                if (inString)
                {
                    if (c == '\\') { j++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; break;
                    case '[':
                    case '{': depth++; break;
                    case ']':
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            closeBracket = j;
                            return true;
                        }
                        break;
                }
            }

            return false;
        }

        // Returns the (start, end) spans of each top-level `{...}` object inside the array body,
        // in ascending order. `end` is exclusive (one past the closing brace). String-aware.
        private static List<(int Start, int End)> TokenizeObjectSpans(string body)
        {
            var spans = new List<(int, int)>();
            int depth = 0;
            int start = -1;
            bool inString = false;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; break;
                    case '{':
                        if (depth == 0) start = i;
                        depth++;
                        break;
                    case '[':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0 && start >= 0)
                        {
                            spans.Add((start, i + 1));
                            start = -1;
                        }
                        break;
                    case ']':
                        depth--;
                        break;
                }
            }

            return spans;
        }

        // ---- flat-object parser ----------------------------------------------------------------

        // Parses a flat cue-row object (`{ "key": "str"|int, ... }`) into its ordered key/value pairs
        // and detects the field + closing-brace indentation from the source text. Cue rows carry only
        // string and integer scalars — anything else (nested object/array, float, bool, null) is
        // surfaced as an error rather than silently mangled.
        private static List<KeyValuePair<string, object>> ParseFlatObject(
            string objText, out string fieldIndent, out string braceIndent)
        {
            fieldIndent = DetectFieldIndent(objText);
            braceIndent = DetectBraceIndent(objText);

            var fields = new List<KeyValuePair<string, object>>();
            int i = objText.IndexOf('{');
            if (i < 0)
                throw new InvalidOperationException("Cue row is not a JSON object.");
            i++; // past '{'

            while (i < objText.Length)
            {
                SkipInsignificant(objText, ref i);
                if (i >= objText.Length || objText[i] == '}')
                    break;

                if (objText[i] != '"')
                    throw new InvalidOperationException($"Expected a quoted key in cue row at index {i}.");
                string key = ReadJsonString(objText, ref i);

                SkipInsignificant(objText, ref i);
                if (i >= objText.Length || objText[i] != ':')
                    throw new InvalidOperationException($"Expected ':' after key '{key}' in cue row.");
                i++; // past ':'
                SkipInsignificant(objText, ref i);

                object value;
                if (i < objText.Length && objText[i] == '"')
                {
                    value = ReadJsonString(objText, ref i);
                }
                else
                {
                    value = ReadJsonInteger(objText, ref i, key);
                }

                fields.Add(new KeyValuePair<string, object>(key, value));
            }

            return fields;
        }

        private static void SkipInsignificant(string s, ref int i)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ','))
                i++;
        }

        private static string ReadJsonString(string s, ref int i)
        {
            // s[i] == '"'
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '\\')
                {
                    if (i >= s.Length)
                        break;
                    char esc = s[i++];
                    sb.Append(esc switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        'b' => '\b',
                        'f' => '\f',
                        'u' => ReadJsonUnicodeEscape(s, ref i),
                        _ => throw new InvalidOperationException($"Invalid escape sequence '\\{esc}' in cue row string."),
                    });
                    continue;
                }
                if (c == '"')
                    return sb.ToString();
                sb.Append(c);
            }

            throw new InvalidOperationException("Unterminated string in cue row.");
        }

        private static char ReadJsonUnicodeEscape(string s, ref int i)
        {
            if (i + 4 > s.Length)
                throw new InvalidOperationException("Incomplete unicode escape in cue row string.");

            string hex = s.Substring(i, 4);
            if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value))
                throw new InvalidOperationException($"Invalid unicode escape '\\u{hex}' in cue row string.");

            i += 4;
            return (char)value;
        }

        private static object ReadJsonInteger(string s, ref int i, string key)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
                i++;
            while (i < s.Length && char.IsDigit(s[i]))
                i++;

            string token = s.Substring(start, i - start);
            if (token.Length == 0)
                throw new InvalidOperationException($"Expected an integer value for key '{key}' in cue row.");
            if (i < s.Length && (s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
                throw new InvalidOperationException(
                    $"Non-integer numeric value for key '{key}' in cue row; the writer models only string/int fields.");
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                throw new InvalidOperationException($"Could not parse integer '{token}' for key '{key}' in cue row.");

            return parsed;
        }

        private static string DetectFieldIndent(string objText)
        {
            int brace = objText.IndexOf('{');
            int newline = objText.IndexOf('\n', brace < 0 ? 0 : brace);
            if (newline < 0)
                return "      ";
            int i = newline + 1;
            int start = i;
            while (i < objText.Length && (objText[i] == ' ' || objText[i] == '\t'))
                i++;
            return objText.Substring(start, i - start);
        }

        private static string DetectBraceIndent(string objText)
        {
            int lastNewline = objText.LastIndexOf('\n');
            if (lastNewline < 0)
                return "    ";
            int i = lastNewline + 1;
            int start = i;
            while (i < objText.Length && (objText[i] == ' ' || objText[i] == '\t'))
                i++;
            return objText.Substring(start, i - start);
        }

        // ---- small helpers ---------------------------------------------------------------------

        private static bool TryGetString(IReadOnlyList<KeyValuePair<string, object>> fields, string key, out string value)
        {
            foreach (KeyValuePair<string, object> field in fields)
            {
                if (string.Equals(field.Key, key, StringComparison.Ordinal) && field.Value is string s)
                {
                    value = s;
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }

        private static bool TryGetInt(IReadOnlyList<KeyValuePair<string, object>> fields, string key, out int value)
        {
            foreach (KeyValuePair<string, object> field in fields)
            {
                if (string.Equals(field.Key, key, StringComparison.Ordinal) && field.Value is int i)
                {
                    value = i;
                    return true;
                }
            }

            value = 0;
            return false;
        }

        private static string NormalizeOwnerId(string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }
}
