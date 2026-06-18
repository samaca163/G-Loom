using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GLoom.Serialization;

/// <summary>
/// Turns a <see cref="DocumentDiff"/> into plain text for pre-filling the
/// commit dialog: a one-line <see cref="Headline"/> (the subject draft) and a
/// multi-line bulleted <see cref="Body"/> (the description draft). Reuses the
/// diff engine's existing per-change <c>Summary</c> strings and
/// <see cref="DocumentDiff.DisplayName"/>; no new diff logic lives here. The
/// user edits whatever this produces, so it favours being readable over being
/// exhaustive.
/// </summary>
public static class DiffSummaryText
{
    private const int MaxNamedInHeadline = 2;

    public static string Headline(DocumentDiff? diff, string ghBase)
    {
        if (diff is null || diff.IsEmpty)
            return $"Initial commit of {ghBase}";

        var phrases = Phrases(diff).ToList();
        if (phrases.Count == 0)
            return "Updated document settings";

        if (phrases.Count <= MaxNamedInHeadline)
            return Capitalize(string.Join("; ", phrases));

        var head = string.Join("; ", phrases.Take(MaxNamedInHeadline));
        return Capitalize($"{head}; +{phrases.Count - MaxNamedInHeadline} more");
    }

    public static string Body(DocumentDiff? diff)
    {
        if (diff is null || diff.IsEmpty) return "";

        var sb = new StringBuilder();
        AppendList(sb, "Added", diff.ObjectsAdded.Select(DocumentDiff.DisplayName));
        AppendList(sb, "Removed", diff.ObjectsRemoved.Select(DocumentDiff.DisplayName));
        AppendList(sb, "Modified",
            diff.ObjectsModified.Select(c => $"{DocumentDiff.DisplayName(c.To)} — {c.Summary}"));
        AppendList(sb, "Groups added", diff.GroupsAdded.Select(g => g.Name));
        AppendList(sb, "Groups removed", diff.GroupsRemoved.Select(g => g.Name));
        AppendList(sb, "Groups modified",
            diff.GroupsModified.Select(c => $"{c.To.Name} — {c.Summary}"));
        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<string> Phrases(DocumentDiff diff)
    {
        foreach (var o in diff.ObjectsAdded)
            yield return $"added {DocumentDiff.DisplayName(o)}";
        foreach (var c in diff.ObjectsModified)
            yield return $"{DocumentDiff.DisplayName(c.To)} {c.Summary}";
        foreach (var o in diff.ObjectsRemoved)
            yield return $"removed {DocumentDiff.DisplayName(o)}";
        foreach (var g in diff.GroupsAdded)
            yield return $"added group {g.Name}";
        foreach (var c in diff.GroupsModified)
            yield return $"group {c.To.Name} {c.Summary}";
        foreach (var g in diff.GroupsRemoved)
            yield return $"removed group {g.Name}";
    }

    private static void AppendList(StringBuilder sb, string header, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(header).Append(":\n");
        foreach (var item in list)
            sb.Append("  - ").Append(item).Append('\n');
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
