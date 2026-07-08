#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Arena.Editor
{
    /// <summary>
    /// One materialised <c>combat_vfx_cues</c> row the writer emits — the generator-owned wiring +
    /// look for a slot, plus the author-time <c>slot</c> key (design doc §3.4) and the
    /// <see cref="SortOrder"/> it targets. <see cref="SortOrder"/> is the *join key* against the
    /// existing catalog row: the writer preserves that row's own <c>sort_order</c> (and
    /// <c>owner_kind</c>/<c>owner_id</c> and any per-slot legacy override keys) verbatim and only
    /// overwrites the generator-owned fields + inserts <c>slot</c>. That is what makes FIREBALL's
    /// first write a zero-diff-except-slot change (the generator reproduces every wiring field, so
    /// only the new <c>slot</c> lines appear in the diff).
    /// <para>
    /// <see cref="ProjectileSequenceIndex"/> is <c>null</c> for non-projectile slots (the key is
    /// omitted / removed) and <c>0</c> for a <c>PROJECTILE_BODY</c> row.
    /// </para>
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

        /// <summary>Author-time slot key (snake_case: <c>cast_glow</c>, <c>projectile_body</c>, ...).</summary>
        public string Slot { get; }
        public string Trigger { get; }
        public string Anchor { get; }
        public string VfxId { get; }
        public string AttachMode { get; }
        public string VfxRole { get; }
        public string Lifecycle { get; }
        public int? ProjectileSequenceIndex { get; }
        public int DurationMs { get; }
        /// <summary>The existing row's <c>sort_order</c> this generated row updates (the join key).</summary>
        public int SortOrder { get; }
    }

    /// <summary>
    /// Surgical writer for the <c>combat_vfx_cues[]</c> region of
    /// <c>server/src/progression_catalog.shared.json</c> (design doc decision 4 / decision 10, the
    /// "tested JSON writer" slice). Materialises generated cues for a single owner back into the
    /// catalog while leaving every other byte identical — melee rows, other spells, ability data,
    /// key order, indentation, and line endings are untouched.
    /// <para>
    /// It deliberately does <b>not</b> round-trip the file through a JSON de/serialiser: the
    /// authoring window's models declare only a handful of fields, so reserialising would drop
    /// <c>damage</c>/<c>impact_effects</c>/all the rich gameplay and reorder keys, destroying the
    /// 5300-line file. Instead it locates the owner's rows textually and rewrites only those spans,
    /// parsing each existing row to preserve its <c>sort_order</c> and any legacy override keys and
    /// merging the generator-owned fields over the top (design doc §3.3/§3.4).
    /// </para>
    /// <para>
    /// The new author-time <c>slot</c> key is written to the JSON but is <b>not</b> synced to the
    /// runtime <c>CombatVfxCueCatalog</c> table (<c>sync_combat_vfx_cue_catalog</c> ignores it) — no
    /// runtime schema or wire change (design doc §3.4a). JSON is <c>include_str!</c>'d, so a write is
    /// not live until <c>spacetime publish -p server</c> (or a data-preserving
    /// <c>publish_progression_catalogs</c> resync).
    /// </para>
    /// </summary>
    public static class SpellCueCatalogWriter
    {
        private const string CombatVfxCuesKey = "\"combat_vfx_cues\"";

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
        /// Reads <paramref name="catalogPath"/>, splices <paramref name="rows"/> into the owner's
        /// rows, and writes the result back. Returns <c>true</c> if the file content changed.
        /// </summary>
        public static bool WriteOwnerCues(string catalogPath, string ownerId, IReadOnlyList<SpellCueRow> rows)
        {
            string original = File.ReadAllText(catalogPath);
            string updated = SpliceOwnerCues(original, ownerId, rows);
            if (string.Equals(original, updated, StringComparison.Ordinal))
                return false;

            File.WriteAllText(catalogPath, updated);
            return true;
        }

        /// <summary>
        /// Returns <paramref name="catalogJson"/> with <paramref name="ownerId"/>'s
        /// <c>combat_vfx_cues</c> rows replaced by the materialised <paramref name="rows"/>. Pure and
        /// deterministic — every byte outside the owner's row spans is preserved exactly. Throws when
        /// the owner's rows and <paramref name="rows"/> do not correspond 1:1 by <c>sort_order</c>
        /// (this slice only updates rows that already exist; adding a generator-only slot or removing
        /// an authored row is surfaced, never guessed).
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
            foreach ((int start, int end) in objectSpans)
            {
                string objText = body.Substring(start, end - start);
                List<KeyValuePair<string, object>> fields = ParseFlatObject(objText, out _, out _);
                if (!TryGetString(fields, "owner_id", out string owner)
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
                targets.Add((start, end, sortOrder));
            }

            if (targets.Count == 0)
                throw new InvalidOperationException($"No combat_vfx_cues rows found for owner '{ownerId}'.");

            // Require an exact 1:1 correspondence by sort_order. A generated row with no existing row,
            // or an existing row with no generated row, would mean inserting/orphaning — out of scope
            // for this zero-diff-first slice, so surface it rather than write a surprising diff.
            foreach ((_, _, int sortOrder) in targets)
            {
                if (!rowsBySortOrder.ContainsKey(sortOrder))
                    throw new InvalidOperationException(
                        $"Authored row sort_order {sortOrder} for owner '{ownerId}' has no matching generated row "
                        + "(the writer only updates existing rows this slice).");
            }
            foreach (SpellCueRow row in rows)
            {
                if (!targetSortOrders.Contains(row.SortOrder))
                    throw new InvalidOperationException(
                        $"Generated slot '{row.Slot}' targets sort_order {row.SortOrder} for owner '{ownerId}', "
                        + "but no authored row has that sort_order (the writer does not insert new rows this slice).");
            }

            // Rebuild the array body, replacing only the target spans and keeping every other byte.
            var rebuilt = new StringBuilder(body.Length + 64 * targets.Count);
            int cursor = 0;
            // targets came from objectSpans, which are already in ascending file order.
            foreach ((int start, int end, int sortOrder) in targets)
            {
                rebuilt.Append(body, cursor, start - cursor);
                string objText = body.Substring(start, end - start);
                rebuilt.Append(MergeRow(objText, rowsBySortOrder[sortOrder]));
                cursor = end;
            }
            rebuilt.Append(body, cursor, body.Length - cursor);

            return catalogJson.Substring(0, openBracket + 1)
                + rebuilt
                + catalogJson.Substring(closeBracket);
        }

        // ---- row merge + serialization ---------------------------------------------------------

        // Overwrites the generator-owned fields of an existing row with the generated values, inserts
        // the author-time `slot` key, and preserves everything else (owner_kind/owner_id, sort_order,
        // hit_index, and any unmodelled legacy override keys). Re-serialises in canonical key order,
        // reproducing the source row's exact indentation.
        private static string MergeRow(string objText, SpellCueRow row)
        {
            List<KeyValuePair<string, object>> fields = ParseFlatObject(objText, out string fieldIndent, out string braceIndent);

            var keys = new List<string>();
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            var originalIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> field in fields)
            {
                if (!values.ContainsKey(field.Key))
                {
                    originalIndex[field.Key] = keys.Count;
                    keys.Add(field.Key);
                }
                values[field.Key] = field.Value;
            }

            void Set(string key, object value)
            {
                if (!values.ContainsKey(key))
                {
                    originalIndex[key] = keys.Count;
                    keys.Add(key);
                }
                values[key] = value;
            }

            void Remove(string key)
            {
                if (values.Remove(key))
                    keys.Remove(key);
            }

            // Author-time key + generator-owned fields (design doc §3.2 / Appendix B).
            Set("slot", row.Slot);
            Set("trigger", row.Trigger);
            Set("anchor", row.Anchor);
            Set("vfx_id", row.VfxId);
            Set("attach_mode", row.AttachMode);
            Set("vfx_role", row.VfxRole);
            Set("lifecycle", row.Lifecycle);
            if (row.ProjectileSequenceIndex.HasValue)
                Set("projectile_sequence_index", row.ProjectileSequenceIndex.Value);
            else
                Remove("projectile_sequence_index");
            Set("duration_ms", row.DurationMs);
            // owner_kind, owner_id, sort_order and any legacy override keys are left as parsed.

            keys.Sort((a, b) =>
            {
                int oa = OrderKey(a, originalIndex[a]);
                int ob = OrderKey(b, originalIndex[b]);
                return oa.CompareTo(ob);
            });

            var sb = new StringBuilder();
            sb.Append('{');
            for (int i = 0; i < keys.Count; i++)
            {
                sb.Append('\n').Append(fieldIndent).Append('"').Append(keys[i]).Append("\": ");
                AppendValue(sb, values[keys[i]]);
                if (i < keys.Count - 1)
                    sb.Append(',');
            }
            sb.Append('\n').Append(braceIndent).Append('}');
            return sb.ToString();
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
            => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

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
                        _ => esc,
                    });
                    continue;
                }
                if (c == '"')
                    return sb.ToString();
                sb.Append(c);
            }

            throw new InvalidOperationException("Unterminated string in cue row.");
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
