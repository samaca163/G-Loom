using System;
using System.Collections.Generic;
using System.Text;

namespace GLoom.Survey;

/// <summary>
/// Normalizes a layer full path before it is matched against the rule list.
///
/// A survey rarely stays inside Rhino: it arrives from an iPad app as DXF and may go
/// back out as DWG, and different CAD tools join nested layers with different
/// separators - and some flatten the tree entirely. Normalizing here means the map file
/// absorbs that mangling instead of every rule having to spell out three spellings of
/// the same path.
/// </summary>
public static class LayerPath
{
    public const string Separator = "::";

    private static readonly char[] SeparatorChars = { ':', '$', '/', '\\', '|' };

    /// <summary>
    /// Upper-invariant, single-separator, bracket-free. Brackets are stripped because
    /// they carry meaning in the pattern matchers and a layer is allowed to contain them.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var segments = Split(path);
        if (segments.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0) sb.Append(Separator);
            sb.Append(segments[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The normalized segments of a path, outermost first. Empty segments are dropped so
    /// a doubled separator does not produce a phantom level.
    /// </summary>
    public static IReadOnlyList<string> Split(string? path)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(path)) return result;

        var current = new StringBuilder();
        foreach (var ch in path)
        {
            if (Array.IndexOf(SeparatorChars, ch) >= 0)
            {
                Flush(current, result);
                continue;
            }
            if (ch == '[' || ch == ']') continue;
            current.Append(char.ToUpperInvariant(ch));
        }
        Flush(current, result);
        return result;
    }

    /// <summary>
    /// The innermost segment - the layer's own name. NCS parsing applies to this, not to
    /// the whole path, because the parent layers carry grouping rather than identity.
    /// </summary>
    public static string Leaf(string? path)
    {
        var segments = Split(path);
        return segments.Count == 0 ? string.Empty : segments[segments.Count - 1];
    }

    private static void Flush(StringBuilder current, ICollection<string> into)
    {
        var segment = current.ToString().Trim();
        current.Clear();
        if (segment.Length > 0) into.Add(segment);
    }
}
