using System;
using System.Collections.Generic;

namespace GLoom.Survey;

/// <summary>
/// The user-text key vocabulary. Exactly three dot-separated segments throughout:
/// `SVY.&lt;group&gt;.&lt;field&gt;`. One `SVY.` prefix means a single sort click in Rhino's
/// Attribute User Text page gathers the whole schema and pushes the user's own keys
/// away, and the group segment splits it into legible blocks without nesting the panel
/// cannot show.
/// </summary>
public static class SurveyKeys
{
    public const string Prefix = "SVY.";

    public const string Category = "SVY.identity.category";
    public const string Role = "SVY.identity.role";
    public const string Type = "SVY.identity.type";
    public const string Phase = "SVY.phase.status";

    /// <summary>
    /// All machine-owned bookkeeping in one key holding compact JSON, rather than a
    /// spray of separate keys. Rhino documents no way to hide a key from the Properties
    /// page, so anything written is something the architect sees - one row of plumbing
    /// is honest, six rows interleaved with the survey form is noise.
    /// </summary>
    public const string State = "SVY.gloom.state";

    public const string Unknown = "unknown";
    public const string NotApplicable = "n/a";

    public static string For(string group, string field) => $"{Prefix}{group}.{field}";

    public static bool IsSurveyKey(string? key) =>
        key is not null && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Machine-owned provenance, serialized into <see cref="SurveyKeys.State"/>. Carrying
/// the schema hash and the matched rule is what makes a reclassification detectable
/// later: if the rule that produced this object is no longer the rule that matches its
/// layer, something was decided in between and the diff can say so.
/// </summary>
public sealed record SurveyState(
    string Schema,
    string Rule,
    string Layer,
    string Unit);

public sealed record SurveyRecord(
    SurveyCategory Category,
    LayerMatch Match,
    IReadOnlyList<KeyValuePair<string, string>> Pairs);

public static class SurveyRecordBuilder
{
    /// <summary>
    /// Materializes the metadata container for one classified object: every key the
    /// schema declares for its category, resolved as existing ?? default ?? placeholder.
    ///
    /// Machine-owned fields are always taken from the match. Human-owned fields keep
    /// whatever is already on the object - G-Loom reads the architect's values and never
    /// overwrites them, which is the invariant that makes re-running safe.
    /// </summary>
    public static SurveyRecord Build(
        SurveySchema schema,
        LayerMatch match,
        IReadOnlyDictionary<string, string>? existing,
        string schemaHash,
        string lengthUnit)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var full = schema.Materialise == MaterialisePolicy.Full;

        foreach (var group in schema.Core)
            foreach (var field in group.Fields)
                Add(pairs, SurveyKeys.For(group.Id, field.Id), field, match, existing, full);

        foreach (var field in match.Category.Fields)
            Add(pairs, SurveyKeys.For(match.Category.Id, field.Id), field, match, existing, full);

        var state = new SurveyState($"{schema.Id}@{schemaHash}", match.Rule.Id, match.Layer, lengthUnit);
        pairs.Add(new KeyValuePair<string, string>(SurveyKeys.State, SurveySchemaJson.WriteState(state)));

        return new SurveyRecord(match.Category, match, pairs);
    }

    /// <summary>
    /// The complete key set an object should end up carrying: everything it already has,
    /// with the survey keys replaced by the freshly built record. Foreign keys - another
    /// plugin's, a colleague's own - are carried through untouched, in their original
    /// order, so running the tagger over someone else's file never costs them data.
    ///
    /// Computed here rather than through ModelUserText's MergeRange/EnsureRange/
    /// UpdateRange because McNeel documents the semantics of none of the three, and a
    /// wrong guess would either leave machine keys stale or overwrite hand-entered
    /// survey values. A complete set assigned wholesale has no such ambiguity.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Merge(
        IReadOnlyDictionary<string, string>? existing,
        IReadOnlyList<KeyValuePair<string, string>> desired)
    {
        var final = new List<KeyValuePair<string, string>>();
        if (existing is not null)
            foreach (var pair in existing)
                if (!SurveyKeys.IsSurveyKey(pair.Key))
                    final.Add(pair);

        final.AddRange(desired);
        return final;
    }

    private static void Add(
        ICollection<KeyValuePair<string, string>> pairs,
        string key,
        SurveyField field,
        LayerMatch match,
        IReadOnlyDictionary<string, string>? existing,
        bool full)
    {
        var value = Resolve(key, field, match, existing, full);

        // Rhino removes a key when it is written with a null value, and an empty string
        // is at best undefined. A field that would be blank is simply not written, so an
        // object never ends up looking un-surveyed because of a formatting slip.
        if (string.IsNullOrWhiteSpace(value)) return;

        pairs.Add(new KeyValuePair<string, string>(key, value!.Trim()));
    }

    private static string? Resolve(
        string key,
        SurveyField field,
        LayerMatch match,
        IReadOnlyDictionary<string, string>? existing,
        bool full)
    {
        if (field.Source == FieldSource.Rule)
        {
            var fromRule = key switch
            {
                SurveyKeys.Category => match.Category.Id.ToUpperInvariant(),
                SurveyKeys.Role => match.Role,
                SurveyKeys.Phase => match.Phase,
                SurveyKeys.Type => match.Type,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(fromRule)) return fromRule;
        }

        if (existing is not null && existing.TryGetValue(key, out var current) && !string.IsNullOrWhiteSpace(current))
            return current;

        if (!string.IsNullOrWhiteSpace(field.Default)) return field.Default;

        return full ? SurveyKeys.Unknown : null;
    }
}
