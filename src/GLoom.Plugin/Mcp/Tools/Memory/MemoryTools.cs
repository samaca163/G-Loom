using System;
using System.Collections.Generic;
using System.Linq;
using GLoom.Mcp.Protocol;
using GLoom.Vcs;

namespace GLoom.Mcp.Tools.Memory;

/// <summary>
/// The project-memory tools: git facts about a definition, read through GLoomRepository
/// exactly as the panel reads them. Host-free; the only live fact is the snapshot the host
/// supplies. Every call spawns git, so these run on the request thread, never on the UI.
/// </summary>
public static class MemoryTools
{
    public static void Register(McpDispatcher d, Func<LiveSnapshot?> live)
    {
        d.Register(new McpTool(
            "gloom_status",
            "Where a Grasshopper definition stands in its project's version history: project root, " +
            "branch (system option), last commit, whether the working file matches a committed version, " +
            "and whether the active canvas has unsaved edits. Call this first.",
            Schema.Object().String("file", ProjectLocator.FileArgDescription).Build(),
            ToolAccess.Read,
            (args, _) => Status(Args.String(args, "file"), live())));

        d.Register(new McpTool(
            "gloom_history",
            "The version history of one Grasshopper definition, newest first: each commit's version label, " +
            "subject, description, author, date and G-Loom trailers (Gloom-Version, agent provenance). " +
            "This is the project's record of design decisions for that definition.",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription)
                .Integer("limit", "Maximum number of commits to return (default 20, max 200).", min: 1, max: 200)
                .Build(),
            ToolAccess.Read,
            (args, _) => History(Args.String(args, "file"), Args.Int(args, "limit", 20), live())));
    }

    public sealed record CommitSummary(
        string Sha, string ShortSha, string? Version, string Subject, string Description,
        IReadOnlyDictionary<string, string> Trailers, string Author, DateTimeOffset When, bool IsCurrent);

    public static ToolResult Status(string? file, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        var files = f.Files;
        var status = GLoomRepository.GetStatus(f.RepoRoot, files);
        var count = GLoomRepository.CountCommitsTouching(f.RepoRoot, files);

        string? currentSha = f.IsActiveDocument && live is not null
            ? live.CurrentSha
            : f.Exists ? GLoomRepository.FindCommitMatchingWorkingTree(f.RepoRoot, f.GhRel, f.JsonRel) : null;

        var current = currentSha is null ? null
            : GLoomRepository.Log(f.RepoRoot, 200, files).FirstOrDefault(c => c.Sha == currentSha)
              ?? new GLoomRepository.CommitInfo(currentSha, "", default, "", "");

        return ToolResult.Json(new
        {
            file = f.GhFullPath,
            exists = f.Exists,
            isActiveDocument = f.IsActiveDocument,
            projectRoot = f.RepoRoot,
            definitionPath = f.GhRel,
            recipePath = f.JsonRel,
            branch = status.Branch,
            commitCount = count,
            nextVersion = CommitVersioning.FormatMessage(f.BaseName, count + 1),
            lastCommit = status.LastCommit is { } lc ? Summarize(lc, currentSha) : null,
            currentVersion = current is null ? null : Summarize(current, currentSha),
            unsavedEdits = f.IsActiveDocument ? live!.IsDirty : (bool?)null,
            note = currentSha is null
                ? "The working file does not match any of the last 200 committed versions of this definition " +
                  "(uncommitted changes, never committed, or older than the search window)."
                : null,
        });
    }

    public static ToolResult History(string? file, int limit, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        limit = Math.Clamp(limit, 1, 200);
        var files = f.Files;
        var fetched = GLoomRepository.Log(f.RepoRoot, limit + 1, files);
        var commits = fetched.Take(limit).ToList();
        var currentSha = f.IsActiveDocument && live is not null ? live.CurrentSha : null;
        var branch = GLoomRepository.GetStatus(f.RepoRoot, files).Branch;

        return ToolResult.Json(new
        {
            file = f.GhFullPath,
            exists = f.Exists,
            definitionPath = f.GhRel,
            branch,
            returned = commits.Count,
            truncated = fetched.Count > limit,
            commits = commits.Select(c => Summarize(c, currentSha)).ToList(),
            note = commits.Count == 0 && !f.Exists
                ? "No file on disk and no commits touch this path; check the path (relative paths resolve against the project root)."
                : null,
        });
    }

    private static CommitSummary Summarize(GLoomRepository.CommitInfo c, string? currentSha)
    {
        var split = CommitTrailers.Parse(c.Body);
        return new CommitSummary(
            c.Sha, VersionRef.Short(c.Sha), CommitVersioning.ExtractVersionLabel(c), c.Message, split.Text,
            split.Trailers, c.Author, c.When, c.Sha == currentSha);
    }
}
