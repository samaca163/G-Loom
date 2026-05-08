using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;

namespace GBim.Vcs;

/// <summary>
/// Thin wrapper over LibGit2Sharp scoped to G-BIM's needs: open or init a repo,
/// commit a canonical JSON file (and optionally an accompanying .gh) at any
/// path inside the repo, and read back recent commits and HEAD status.
/// Stateless - each call opens and disposes its own <see cref="Repository"/>.
/// </summary>
public static class GBimRepository
{
    public sealed record CommitInfo(string Sha, string Author, DateTimeOffset When, string Message);
    public sealed record RepoStatus(string Branch, CommitInfo? LastCommit);

    /// <summary>
    /// Writes the canonical JSON to <paramref name="canonicalJsonFullPath"/> and
    /// creates a commit. Optionally also stages an accompanying file (typically
    /// the .gh) at <paramref name="alsoStageFullPath"/> if it exists on disk.
    /// </summary>
    /// <returns>The new commit's SHA, or null if there was nothing to commit.</returns>
    public static string? Commit(
        string repoRoot,
        string canonicalJson,
        string canonicalJsonFullPath,
        string message,
        Signature author,
        string? alsoStageFullPath = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("Repo root is required.", nameof(repoRoot));

        EnsureDirectoryAndRepo(repoRoot);

        var jsonDir = Path.GetDirectoryName(canonicalJsonFullPath);
        if (!string.IsNullOrEmpty(jsonDir)) Directory.CreateDirectory(jsonDir);
        File.WriteAllText(canonicalJsonFullPath, canonicalJson);

        using var repo = new Repository(repoRoot);

        var jsonRel = ToRepoRelative(repoRoot, canonicalJsonFullPath);
        LibGit2Sharp.Commands.Stage(repo, jsonRel);

        if (!string.IsNullOrEmpty(alsoStageFullPath) && File.Exists(alsoStageFullPath))
        {
            var alsoRel = ToRepoRelative(repoRoot, alsoStageFullPath);
            LibGit2Sharp.Commands.Stage(repo, alsoRel);
        }

        try
        {
            var commit = repo.Commit(message, author, author);
            return commit.Sha;
        }
        catch (EmptyCommitException)
        {
            return null;
        }
    }

    public static IReadOnlyList<CommitInfo> Log(string repoRoot, int limit)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Repository.IsValid(repoRoot))
            return Array.Empty<CommitInfo>();

        if (limit <= 0) limit = 10;

        using var repo = new Repository(repoRoot);
        return repo.Commits
            .QueryBy(new CommitFilter { SortBy = CommitSortStrategies.Time })
            .Take(limit)
            .Select(c => new CommitInfo(
                Sha: c.Sha,
                Author: c.Author?.Name ?? string.Empty,
                When: c.Author?.When ?? DateTimeOffset.MinValue,
                Message: c.MessageShort ?? string.Empty))
            .ToList();
    }

    public static RepoStatus GetStatus(string repoRoot)
    {
        if (!Repository.IsValid(repoRoot))
            return new RepoStatus("(no repo)", null);

        using var repo = new Repository(repoRoot);
        var head = repo.Head;
        var branch = head?.FriendlyName ?? "(detached)";
        var tip = head?.Tip;
        var info = tip == null ? null : new CommitInfo(
            Sha: tip.Sha,
            Author: tip.Author?.Name ?? string.Empty,
            When: tip.Author?.When ?? DateTimeOffset.MinValue,
            Message: tip.MessageShort ?? string.Empty);
        return new RepoStatus(branch, info);
    }

    private static string ToRepoRelative(string repoRoot, string fullPath) =>
        Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');

    private static void EnsureDirectoryAndRepo(string workingDir)
    {
        if (!Directory.Exists(workingDir))
            Directory.CreateDirectory(workingDir);
        if (!Repository.IsValid(workingDir))
            Repository.Init(workingDir);
    }
}
