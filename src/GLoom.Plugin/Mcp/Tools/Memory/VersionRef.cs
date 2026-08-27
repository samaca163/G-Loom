using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GLoom.Mcp.Protocol;
using GLoom.Serialization;
using GLoom.Vcs;

namespace GLoom.Mcp.Tools.Memory;

/// <summary>
/// One version of a definition, as an agent named it. Either a commit (<see cref="Sha"/>
/// and, when it could be read, <see cref="Commit"/>) or the file as it sits on disk.
/// </summary>
public sealed record ResolvedVersion(
    string Reference,
    string? Sha,
    GLoomRepository.CommitInfo? Commit,
    bool IsWorkingTree)
{
    public string? ShortSha => Sha is null ? null : VersionRef.Short(Sha);

    /// <summary>What the panel would call it: "V012", else the short SHA, else "working tree".</summary>
    public string Label => IsWorkingTree
        ? "working tree"
        : (Commit is null ? null : CommitVersioning.ExtractVersionLabel(Commit)) ?? ShortSha ?? Reference;

    public string? VersionLabel => Commit is null ? null : CommitVersioning.ExtractVersionLabel(Commit);
}

public sealed record LoadedRecipe(ResolvedVersion Version, CanonicalDocument Document, string Json);

/// <summary>
/// How every memory tool turns a "version" argument into a commit. Version labels are the
/// vocabulary the panel teaches (tower_V012), so they resolve against this definition's own
/// history; anything else is handed to git as a commit-ish. Host-free.
/// </summary>
public static class VersionRef
{
    public const string Working = "working";

    public const string ArgDescription =
        "A version of the definition: a version label (V012 or tower_V012), a commit SHA (full or " +
        "abbreviated), a tag, a branch, HEAD or HEAD~2, or \"working\" for the file as it is on disk.";

    private const int LabelSearchWindow = 500;
    private static readonly Regex LabelShape = new(@"^(?:(?<base>.*)_)?V(?<number>\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Resolves <paramref name="reference"/>; null or blank means <paramref name="fallback"/>
    /// ("HEAD" or <see cref="Working"/>, whichever the tool documents as its default).</summary>
    public static ResolvedVersion Resolve(LocatedFile f, string? reference, string fallback)
    {
        var r = string.IsNullOrWhiteSpace(reference) ? fallback : reference.Trim();

        if (string.Equals(r, Working, StringComparison.OrdinalIgnoreCase) || string.Equals(r, "disk", StringComparison.OrdinalIgnoreCase))
            return new ResolvedVersion(Working, null, null, true);

        var label = LabelShape.Match(r);
        if (label.Success && int.TryParse(label.Groups["number"].Value, out var number))
        {
            var prefix = label.Groups["base"].Value;
            var ownLabel = prefix.Length == 0 || string.Equals(prefix, f.BaseName, StringComparison.OrdinalIgnoreCase);
            // "site_V2" asked of tower.gh is a ref that happens to end in _V<n>, not tower's second
            // version; the label search only stands in when no such ref exists (a renamed definition
            // keeps its old labels in history).
            if (!ownLabel && GLoomRepository.ResolveCommit(f.RepoRoot, r) is { } named)
                return new ResolvedVersion(r, named, CommitAt(f.RepoRoot, named), false);

            var wanted = "V" + label.Groups["number"].Value.TrimStart('0').PadLeft(3, '0');
            var commit = GLoomRepository.Log(f.RepoRoot, LabelSearchWindow, f.Files)
                .FirstOrDefault(c => SameVersion(CommitVersioning.ExtractVersionLabel(c), number));
            if (commit is not null) return new ResolvedVersion(r, commit.Sha, commit, false);
            // A label can still be a real ref (a tag named V012) - fall through before refusing.
            var asRef = ownLabel ? GLoomRepository.ResolveCommit(f.RepoRoot, r) : null;
            if (asRef is null)
                throw new ToolArgumentException(
                    $"No version {wanted} in the last {LabelSearchWindow} commits of {f.GhRel} on this branch" +
                    (ownLabel ? "" : $", and \"{r}\" is not a tag or branch either") +
                    "; gloom_history lists the labels that exist.");
            return new ResolvedVersion(r, asRef, CommitAt(f.RepoRoot, asRef), false);
        }

        var sha = GLoomRepository.ResolveCommit(f.RepoRoot, r)
            ?? throw new ToolArgumentException(
                $"\"{r}\" is not a version of {f.GhRel}: not a version label, commit SHA, tag or branch. " +
                "gloom_history lists the versions that exist; \"working\" is the file on disk.");
        return new ResolvedVersion(r, sha, CommitAt(f.RepoRoot, sha), false);
    }

    /// <summary>The recipe (canonical .gloom.json) of the definition at a version. Throws a
    /// readable reason when there is none: the file predates G-Loom, only the .gh was committed,
    /// or nothing has been committed from the panel yet.</summary>
    public static LoadedRecipe LoadRecipe(LocatedFile f, ResolvedVersion v)
    {
        string? json;
        if (v.IsWorkingTree)
        {
            var path = RepoDiscovery.CanonicalJsonFullPathFor(f.GhFullPath);
            json = File.Exists(path) ? File.ReadAllText(path) : null;
            if (json is null)
                throw new ToolArgumentException(
                    $"No recipe on disk for {f.GhRel} ({f.JsonRel} is missing). G-Loom writes it on every commit " +
                    "from the panel; commit once, or read a committed version instead.");
        }
        else
        {
            json = GLoomRepository.ReadFileAtCommit(f.RepoRoot, v.Sha!, f.JsonRel);
            if (json is null)
                throw new ToolArgumentException(
                    $"No recipe at {v.Label} for {f.GhRel}: {f.JsonRel} does not exist in that commit " +
                    "(the version predates G-Loom, or only the .gh was committed).");
        }

        var doc = CanonicalJson.TryParse(json)
            ?? throw new ToolArgumentException($"The recipe at {v.Label} ({f.JsonRel}) is not valid canonical JSON.");
        return new LoadedRecipe(v, doc, json);
    }

    public static GLoomRepository.CommitInfo? CommitAt(string repoRoot, string sha) =>
        GLoomRepository.Log(repoRoot, 1, null, startingAt: sha).FirstOrDefault();

    /// <summary>The commit touching this definition just before <paramref name="sha"/> - what
    /// "the previous version" means for one file in a multi-definition project.</summary>
    public static GLoomRepository.CommitInfo? PreviousTouching(LocatedFile f, string sha)
    {
        // The log opens on the last touching commit at or before sha, so skipping by position
        // would drop a real predecessor whenever sha itself changed other files only.
        var touching = GLoomRepository.Log(f.RepoRoot, 2, f.Files, startingAt: sha);
        return touching.Count > 0 && !string.Equals(touching[0].Sha, sha, StringComparison.Ordinal)
            ? touching[0]
            : touching.Skip(1).FirstOrDefault();
    }

    /// <summary>The last version of this definition on the current system option: what a diff
    /// or narrative compares against when the caller names no "from".</summary>
    internal static ResolvedVersion LastCommitted(LocatedFile f)
    {
        var c = GLoomRepository.Log(f.RepoRoot, 1, f.Files).FirstOrDefault()
            ?? throw new ToolArgumentException(
                $"{f.GhRel} has no committed versions yet, so there is no previous version to compare against; " +
                "commit it once from the panel, or name \"from\" explicitly.");
        return AsVersion(c);
    }

    internal static ResolvedVersion AsVersion(GLoomRepository.CommitInfo c) => new(Label(c), c.Sha, c, false);

    internal static string Label(GLoomRepository.CommitInfo c) => CommitVersioning.ExtractVersionLabel(c) ?? Short(c.Sha);

    public static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static bool SameVersion(string? label, int number) =>
        label is not null && label.Length > 1 && int.TryParse(label[1..], out var n) && n == number;
}
