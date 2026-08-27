using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GLoom.Mcp.Protocol;
using GLoom.Serialization;
using GLoom.Vcs;

namespace GLoom.Mcp.Tools.Memory;

/// <summary>
/// The gloom_explain_changes narrative: one markdown section per version of a definition,
/// computed from the recipes alone so an agent can read what a run of versions changed
/// without a model open. Also owns the commit meta line the decision record shares, so a
/// version reads the same in both. Host-free.
/// </summary>
public static class ChangeNarrative
{
    private const int MaxSections = 20, RangeWindow = 500;

    public static ToolResult ExplainChanges(string? file, string? version, string? from, string? to, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        var ranged = !string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to);
        if (ranged && !string.IsNullOrWhiteSpace(version))
            throw new ToolArgumentException(
                "Pass either \"version\" (one version against its predecessor) or \"from\"/\"to\" (a range), not both.");

        var recipes = new Recipes(f);
        var baseName = f.BaseName;
        var sb = new StringBuilder();
        if (ranged) ExplainRange(sb, f, recipes, baseName, from, to);
        else ExplainOne(sb, f, recipes, baseName, version);
        return ToolResult.Text(sb.ToString().TrimEnd() + "\n");
    }

    internal static string MetaLine(GLoomRepository.CommitInfo c)
    {
        var trailers = CommitTrailers.Parse(c.Body).Trailers;
        var parts = new List<string> { c.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), $"by {c.Author}" };
        if (trailers.TryGetValue("Gloom-Agent", out var agent) && agent.Length > 0) parts.Add($"agent {agent}");
        if (trailers.TryGetValue("Gloom-Agent-Session", out var session) && session.Length > 0) parts.Add($"session {session}");
        if (trailers.TryGetValue("Gloom-Checkpoint-Base", out var checkpoint) && checkpoint.Length > 0) parts.Add($"checkpoint base {VersionRef.Short(checkpoint)}");
        if (trailers.TryGetValue("Gloom-Intent", out var intent) && intent.Length > 0) parts.Add($"intent: {intent}");
        return string.Join(" · ", parts);
    }

    private static void ExplainOne(StringBuilder sb, LocatedFile f, Recipes recipes, string baseName, string? version)
    {
        var v = string.IsNullOrWhiteSpace(version) ? VersionRef.LastCommitted(f) : VersionRef.Resolve(f, version, VersionRef.Working);
        if (v.IsWorkingTree)
        {
            WorkingSection(sb, recipes, baseName, GLoomRepository.Log(f.RepoRoot, 1, f.Files).FirstOrDefault());
            return;
        }
        var commit = v.Commit ?? new GLoomRepository.CommitInfo(v.Sha!, "", default, v.Reference);
        // HEAD or a branch tip may have changed other definitions only; what it holds of this
        // one is the last version it can see, and that is the version to narrate.
        var touching = GLoomRepository.Log(f.RepoRoot, 1, f.Files, startingAt: v.Sha!).FirstOrDefault();
        if (touching is not null && !string.Equals(touching.Sha, commit.Sha, StringComparison.Ordinal))
        {
            sb.Append($"{v.Reference} ({VersionRef.Short(commit.Sha)}) did not change {baseName}; the version of it there is {VersionRef.Label(touching)}.\n\n");
            commit = touching;
        }
        var previous = VersionRef.PreviousTouching(f, commit.Sha);
        CommitSection(sb, recipes, baseName, commit, previous is null ? null : VersionRef.AsVersion(previous));
    }

    private static void ExplainRange(StringBuilder sb, LocatedFile f, Recipes recipes, string baseName, string? from, string? to)
    {
        var fromV = string.IsNullOrWhiteSpace(from) ? VersionRef.LastCommitted(f) : VersionRef.Resolve(f, from, VersionRef.Working);
        if (fromV.IsWorkingTree)
            throw new ToolArgumentException("\"from\" must be a committed version; the working tree can only end a range (\"to\").");
        var toV = VersionRef.Resolve(f, to, VersionRef.Working);
        var toRev = toV.IsWorkingTree ? "HEAD" : toV.Sha!;
        sb.Append($"# {baseName} — changes from {fromV.Label} to {toV.Label}\n\n");

        // Commits reachable from `from` but not from `to` exist only when `from` is not an ancestor.
        if (GLoomRepository.Log(f.RepoRoot, 1, null, startingAt: $"{toRev}..{fromV.Sha}").Count > 0)
        {
            sb.Append($"{fromV.Label} is not an earlier version on the path to {toV.Label} (it is the newer one, or the two " +
                      "sit on different system options), so there are no versions to walk between them.\n");
            return;
        }

        var range = GLoomRepository.Log(f.RepoRoot, RangeWindow, f.Files, startingAt: $"{fromV.Sha}..{toRev}");
        // `from` may predate the definition (a README root, another definition's first commit):
        // the oldest version in the range is then its first one, not a change from nothing.
        var existedAtFrom = range.Count == 0 || GLoomRepository.Log(f.RepoRoot, 1, f.Files, startingAt: fromV.Sha!).Count > 0;
        var shown = range.Take(MaxSections).ToList();
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            ResolvedVersion? predecessor = i + 1 < range.Count ? VersionRef.AsVersion(range[i + 1]) : existedAtFrom ? fromV : null;
            CommitSection(sb, recipes, baseName, shown[i], predecessor);
        }
        if (range.Count > shown.Count)
            sb.Append($"… and {range.Count - shown.Count} earlier versions not shown.\n\n");

        if (toV.IsWorkingTree) WorkingSection(sb, recipes, baseName, range.Count > 0 ? range[0] : fromV.Commit);
        else if (range.Count == 0) sb.Append($"No versions of {f.GhRel} between {fromV.Label} and {toV.Label}.\n");
    }

    private static void CommitSection(
        StringBuilder sb, Recipes recipes, string baseName, GLoomRepository.CommitInfo c, ResolvedVersion? predecessor)
    {
        var text = CommitTrailers.Parse(c.Body).Text;
        sb.Append($"## {baseName} {VersionRef.Label(c)} — {c.Message}\n");
        sb.Append(MetaLine(c)).Append('\n');
        if (text.Length > 0) sb.Append('\n').Append(text).Append('\n');

        var (doc, error) = recipes.At(VersionRef.AsVersion(c));
        if (doc is null)
        {
            sb.Append($"\nRecipe unavailable at this version: {error}\n\n");
            return;
        }
        if (predecessor is null)
        {
            sb.Append("\nFirst version of this definition.\n");
            DescribeWhole(sb, doc, baseName);
            return;
        }
        AppendDiff(sb, recipes, baseName, predecessor, doc);
    }

    private static void WorkingSection(StringBuilder sb, Recipes recipes, string baseName, GLoomRepository.CommitInfo? last)
    {
        sb.Append($"## {baseName} — Uncommitted changes on disk\n");
        var (doc, error) = recipes.At(new ResolvedVersion(VersionRef.Working, null, null, true));
        if (doc is null)
        {
            sb.Append($"Recipe unavailable on disk: {error}\n\n");
            return;
        }
        if (last is null)
        {
            sb.Append("Not committed yet.\n");
            DescribeWhole(sb, doc, baseName);
        }
        else
        {
            sb.Append($"Compared with {VersionRef.Label(last)} — {last.Message}\n");
            AppendDiff(sb, recipes, baseName, VersionRef.AsVersion(last), doc);
        }
        sb.Append('(').Append(VersionTools.WorkingTreeNote).Append(")\n\n");
    }

    private static void AppendDiff(StringBuilder sb, Recipes recipes, string baseName, ResolvedVersion predecessor, CanonicalDocument doc)
    {
        var (previous, _) = recipes.At(predecessor);
        sb.Append('\n');
        if (previous is null)
        {
            sb.Append($"Recipe unavailable at the previous version ({predecessor.Label}); describing this version on its own.\n");
            DescribeWhole(sb, doc, baseName);
            return;
        }

        var diff = DocumentDiff.Compute(previous, doc);
        if (diff.IsEmpty)
        {
            sb.Append("No structural changes to the recipe (the .gh changed on its own).\n\n");
            return;
        }
        sb.Append($"Changes ({diff.TotalChanges}):\n");
        if (diff.MetaChanged) sb.Append("  - document name or description changed\n");
        var body = DiffSummaryText.Body(diff);
        if (body.Length > 0) sb.Append(body).Append('\n');
        sb.Append('\n');
    }

    private static void DescribeWhole(StringBuilder sb, CanonicalDocument doc, string baseName)
    {
        var kinds = string.Join(", ", VersionTools.CountByKind(doc).Select(kv => $"{kv.Value} {kv.Key}"));
        sb.Append($"{VersionTools.Count(doc.Objects.Count, "object")}{(kinds.Length > 0 ? $" ({kinds})" : "")}, {VersionTools.Count(doc.Groups.Count, "group")}.\n");
        var empty = new CanonicalDocument(doc.SchemaVersion, doc.Document, Array.Empty<CanonicalObject>(), Array.Empty<CanonicalGroup>());
        sb.Append(DiffSummaryText.Headline(DocumentDiff.Compute(empty, doc), baseName)).Append("\n\n");
    }

    /// <summary>Recipes read once per version within one call; a version whose recipe cannot be
    /// read keeps its reason so the narrative can say why instead of failing whole.</summary>
    private sealed class Recipes
    {
        private readonly LocatedFile _f;
        private readonly Dictionary<string, (CanonicalDocument? Doc, string? Error)> _cache = new(StringComparer.Ordinal);

        public Recipes(LocatedFile f) => _f = f;

        public (CanonicalDocument? Doc, string? Error) At(ResolvedVersion v)
        {
            var key = v.IsWorkingTree ? VersionRef.Working : v.Sha!;
            if (_cache.TryGetValue(key, out var cached)) return cached;
            (CanonicalDocument?, string?) result;
            try { result = (VersionRef.LoadRecipe(_f, v).Document, null); }
            catch (ToolArgumentException ex) { result = (null, ex.Message); }
            _cache[key] = result;
            return result;
        }
    }
}
