using System.Collections.Generic;

namespace GLoom.Serialization;

/// <summary>
/// Schema v5 of the canonical, diff-friendly representation of a Grasshopper
/// document. Designed for stability across saves: deterministic field order,
/// objects sorted by InstanceGuid, sources sorted lexicographically.
///
/// v5 adds the optional <see cref="PersistentData.ValueListMode"/> field
/// so a value-list display-mode change (DropDown / CheckList / Sequence
/// / Cycle) shows up in the diff. v4 added ValueListItems; v3 added
/// Bounds; v2 added Persistent. Older documents parse cleanly because
/// all new fields have `= null` defaults.
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
        hc.Add(Digest);
        return hc.ToHashCode();
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
            if (!string.Equals(
                ValueListItem.Canonicalize(a[i].Expression),
                ValueListItem.Canonicalize(b[i].Expression),
                System.StringComparison.Ordinal)) return false;
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

        var withoutSuffix = trimmed.Length > 1 && IsNumericSuffix(trimmed[trimmed.Length - 1])
            ? trimmed.Substring(0, trimmed.Length - 1)
            : trimmed;

        if (decimal.TryParse(
            withoutSuffix,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var d))
        {
            return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return trimmed;
    }

    private static bool IsNumericSuffix(char c) =>
        c is 'L' or 'l' or 'D' or 'd' or 'F' or 'f' or 'M' or 'm';
}

public sealed record SliderValue(
    decimal Value,
    decimal Min,
    decimal Max,
    int Decimals,
    string Type);
