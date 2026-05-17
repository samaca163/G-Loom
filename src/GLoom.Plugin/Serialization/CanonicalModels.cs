using System.Collections.Generic;

namespace GLoom.Serialization;

/// <summary>
/// Schema v6 of the canonical, diff-friendly representation of a Grasshopper
/// document. Designed for stability across saves: deterministic field order,
/// objects sorted by InstanceGuid, sources sorted lexicographically.
///
/// v6 adds optional structured representations for two long-tail
/// persistent kinds: <see cref="PersistentData.GradientStops"/> (sorted
/// position+colour list for GH_GradientControl) and
/// <see cref="PersistentData.MdSlider"/> (X/Y position for MD slider).
/// Capture is reflection-based at the serializer layer so the schema
/// doesn't have to reference uncertain SDK type names; failures fall
/// through to the existing opaque-digest path.
/// v5 added ValueListMode; v4 added ValueListItems; v3 added Bounds;
/// v2 added Persistent. Older documents parse cleanly because all new
/// fields have `= null` defaults.
/// </summary>
public sealed record CanonicalDocument(
    int SchemaVersion,
    DocumentMeta Document,
    IReadOnlyList<CanonicalObject> Objects,
    IReadOnlyList<CanonicalGroup> Groups);

public sealed record DocumentMeta(
    string Name,
    string Description);

public sealed record CanonicalObject(
    string InstanceGuid,
    string ComponentGuid,
    string Kind,
    string Name,
    string Nickname,
    Pivot Pivot,
    IReadOnlyList<CanonicalParameter> Inputs,
    IReadOnlyList<CanonicalParameter> Outputs,
    PersistentData? Persistent = null,
    Bounds? Bounds = null);

public sealed record Pivot(float X, float Y);

public sealed record Bounds(float X, float Y, float Width, float Height);

public sealed record CanonicalParameter(
    string InstanceGuid,
    string Name,
    string Nickname,
    string Access,
    IReadOnlyList<string> Sources);

public sealed record CanonicalGroup(
    string InstanceGuid,
    string Name,
    IReadOnlyList<string> Members);

/// <summary>
/// Captured user-tweakable state on a free-floating param. <see cref="Kind"/>
/// is a discriminator; for each kind, only the relevant sub-field is
/// populated (the rest serialize as null and JsonIgnore drops them). The
/// fat-union shape reads simpler in raw JSON than polymorphic discriminator
/// attributes, and diff code can switch on Kind without reflection.
///
/// Kinds emitted today:
///   "slider"    → <see cref="Slider"/>
///   "panel"     → <see cref="PanelText"/>
///   "boolean"   → <see cref="BooleanState"/>
///   "valuelist" → <see cref="ValueListSelected"/>
///   "color"     → <see cref="ColorArgb"/>
///   "data"      → <see cref="Digest"/> (SHA-256 opaque fallback for any
///                 persistent-typed param we don't yet model structurally
///                 — MD sliders, gradients, internalized geometry, etc.)
/// </summary>
public sealed record PersistentData(
    string Kind,
    SliderValue? Slider = null,
    string? PanelText = null,
    bool? BooleanState = null,
    IReadOnlyList<string>? ValueListSelected = null,
    IReadOnlyList<ValueListItem>? ValueListItems = null,
    string? ValueListMode = null,
    string? ColorArgb = null,
    IReadOnlyList<GradientStop>? GradientStops = null,
    MdSliderValue? MdSlider = null,
    string? Digest = null)
{
    // Records use reference equality for collection fields, which would
    // make ValueListSelected and ValueListItems always read as "changed"
    // between two snapshots (each serialization creates a fresh list
    // instance even if the contents are identical). Override Equals +
    // GetHashCode to compare by content. Other fields are primitives,
    // strings, or value-equal records, so the rest is fine.
    public bool Equals(PersistentData? other)
    {
        if (other is null) return false;
        return Kind == other.Kind
            && Slider == other.Slider
            && PanelText == other.PanelText
            && BooleanState == other.BooleanState
            && SequenceEqualOrdinal(ValueListSelected, other.ValueListSelected)
            && SequenceEqual(ValueListItems, other.ValueListItems)
            && LenientStringEquals(ValueListMode, other.ValueListMode)
            && ColorArgb == other.ColorArgb
            && SequenceEqualStops(GradientStops, other.GradientStops)
            && MdSlider == other.MdSlider
            && Digest == other.Digest;
    }

    /// <summary>
    /// Treat null on either side as "no information to compare with"
    /// rather than "definitely different". Used for fields added in
    /// later schema bumps so a v5-vs-pre-v5 diff doesn't fire just
    /// because the older commit didn't capture the field at all.
    /// </summary>
    private static bool LenientStringEquals(string? a, string? b) =>
        a is null || b is null || string.Equals(a, b, System.StringComparison.Ordinal);

    public override int GetHashCode()
    {
        var hc = new System.HashCode();
        hc.Add(Kind);
        hc.Add(Slider);
        hc.Add(PanelText);
        hc.Add(BooleanState);
        if (ValueListSelected is not null)
            foreach (var s in ValueListSelected) hc.Add(s);
        if (ValueListItems is not null)
            foreach (var i in ValueListItems) hc.Add(i);
        hc.Add(ValueListMode);
        hc.Add(ColorArgb);
        if (GradientStops is not null)
            foreach (var s in GradientStops) hc.Add(s);
        hc.Add(MdSlider);
        hc.Add(Digest);
        return hc.ToHashCode();
    }

    private static bool SequenceEqualStops(IReadOnlyList<GradientStop>? a, IReadOnlyList<GradientStop>? b)
    {
        // Lenient on null - same pattern as other later-schema fields.
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static bool SequenceEqualOrdinal(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], System.StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool SequenceEqual(IReadOnlyList<ValueListItem>? a, IReadOnlyList<ValueListItem>? b)
    {
        // Lenient on missing data: pre-v4 commits don't capture
        // ValueListItems, so strict comparison would fire on every
        // v4-vs-pre-v4 diff. Treat null on either side as "no
        // information to compare with".
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return true;
        if (a.Count != b.Count) return false;

        // Compare by Name AND canonicalized Expression. GH rewrites
        // Expression strings between sessions ("4" -> "4L" after a
        // solve), but ValueListItem.Canonicalize strips numeric type
        // suffixes and re-parses, so equivalent expressions agree.
        // Real same-name expression edits (e.g. "4" -> "5") still
        // surface correctly.
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Name, b[i].Name, System.StringComparison.Ordinal)) return false;
            if (!ValueListItem.ExpressionsEquivalent(a[i].Expression, b[i].Expression)) return false;
        }
        return true;
    }
}

public sealed record ValueListItem(string Name, string Expression)
{
    /// <summary>
    /// Normalized form of an Expression for comparison purposes. GH
    /// rewrites Expression strings between sessions ("4" can become
    /// "4L" after the first solve, "5.0" into "5d", etc. - .NET numeric
    /// literal suffixes leak in). Strip a single trailing L/D/F/M
    /// suffix and re-parse as decimal so equivalent numeric expressions
    /// canonicalize to the same string. Non-numeric expressions
    /// (string literals, GUIDs, etc.) round-trip unchanged after a
    /// trim. Used by PersistentData equality and the value-list diff
    /// summary so a same-name expression edit is detected when it's a
    /// REAL change, not GH's normalization noise.
    /// </summary>
    public static string Canonicalize(string expression)
    {
        if (string.IsNullOrEmpty(expression)) return string.Empty;
        var trimmed = expression.Trim();

        // Try parsing the raw string first so scientific notation
        // ("1e10") survives — the exponent's 'e' would otherwise be
        // stripped as a trailing-letter suffix below. If that fails,
        // strip any trailing run of letters (covers GH's numeric
        // literal suffixes — L, d, F, M, combos thereof) and retry.
        if (!TryParseDecimal(trimmed, out var d))
        {
            int end = trimmed.Length;
            while (end > 0 && char.IsLetter(trimmed[end - 1])) end--;
            if (end == trimmed.Length || !TryParseDecimal(trimmed.AsSpan(0, end).ToString(), out d))
                return trimmed;
        }

        // Normalize trailing zeros so "5", "5.0", and "5.00" all
        // canonicalize to "5" — GH sometimes rewrites scale between
        // sessions too, not just suffix.
        var s = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s.Contains('.'))
        {
            s = s.TrimEnd('0');
            if (s.EndsWith(".")) s = s.Substring(0, s.Length - 1);
        }
        return s;
    }

    private static bool TryParseDecimal(string s, out decimal d) =>
        decimal.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out d);

    /// <summary>
    /// True when two value-list expressions should be treated as the
    /// same for diff purposes. Numeric forms are compared via
    /// canonicalization (handles GH's suffix + scale rewrites). When
    /// NEITHER side parses as a number, the expression is opaque text
    /// that GH may normalize unpredictably (e.g. stripping unbound
    /// variable references), so we suppress the diff to avoid false
    /// positives. Numeric edits AND edits that cross the numeric
    /// boundary still surface — only pure text-vs-text drift is
    /// silenced.
    /// </summary>
    public static bool ExpressionsEquivalent(string fromExpr, string toExpr)
    {
        var fromC = Canonicalize(fromExpr);
        var toC = Canonicalize(toExpr);
        if (string.Equals(fromC, toC, System.StringComparison.Ordinal)) return true;
        return !IsCleanNumeric(fromC) && !IsCleanNumeric(toC);
    }

    private static bool IsCleanNumeric(string s) => TryParseDecimal(s, out _);
}

public sealed record SliderValue(
    decimal Value,
    decimal Min,
    decimal Max,
    int Decimals,
    string Type);

public sealed record GradientStop(double Position, string ColorArgb);

public sealed record MdSliderValue(double X, double Y);
