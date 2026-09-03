using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GLoom.Mcp.Protocol;
using GLoom.Serialization;
using GLoom.Vcs;

namespace GLoom.Mcp.Tools.Memory;

/// <summary>
/// The write tools: commit, revert, switch system option, and tag a milestone. They reuse
/// the same git primitives the panel drives (GLoomRepository + CommitVersioning + TagMetadata)
/// so an agent's edits land as ordinary, diffable, attributed history. The canvas-bound half
/// (save/serialize the live document, reload after a swap, capture the running toolchain) is
/// injected as delegates so this file stays host-free; the gate is the dispatcher's
/// call-time AccessDenied, which admits these only when the panel's access is read-write.
/// </summary>
public static class WriteTools
{
    public static void Register(
        McpDispatcher d,
        Func<LiveSnapshot?> live,
        Func<string> serializeActiveDocument,
        Action<string> reloadFromDisk,
        Action<string> reloadAllInRepo,
        Func<Toolchain> captureToolchain,
        Action refreshTracker)
    {
        d.Register(new McpTool(
            "gloom_commit",
            "Commit the active Grasshopper definition as a new version. Saves unsaved canvas edits first, writes the " +
            "canonical recipe, stages the .gh + .gloom.json pair and commits it with the next auto version " +
            "(tower_V013) plus your message. The author is the repo's configured git identity; the agent is named in " +
            "Gloom-Agent/Gloom-Intent trailers so history shows who made the move. \"subject\" is required (one line, " +
            "the design decision); \"description\" is the longer why. Commit only the active document.",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription + " Must be the active document (it is committed live).")
                .String("subject", "Required. One line naming the design decision, e.g. \"Reroute the tower geometry to the new base\".")
                .String("description", "Optional. The longer explanation of why the change was made (the commit body).")
                .String("agent", "Optional. Who is making the change, for the Gloom-Agent trailer (e.g. \"opencode\").")
                .String("intent", "Optional. Short intent, for the Gloom-Intent trailer (e.g. \"adjust massing\").")
                .Build(),
            ToolAccess.Write,
            (args, _) => Commit(
                Args.String(args, "file"), Args.String(args, "subject"), Args.String(args, "description"),
                Args.String(args, "agent"), Args.String(args, "intent"),
                live(), serializeActiveDocument, refreshTracker)));

        d.Register(new McpTool(
            "gloom_revert",
            "Revert the definition to a previous version: restores the .gh + .gloom.json pair at that commit and reloads " +
            "the canvas from disk. DESTRUCTIVE - any uncommitted edits made after that version are discarded. " +
            "\"version\" is the target (a label like V012, a SHA, a tag or a branch); omit it to revert to the last " +
            "committed version (i.e. discard uncommitted changes).",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription)
                .String("version", VersionRef.ArgDescription + " Omit to revert to the last committed version.")
                .Build(),
            ToolAccess.Destructive,
            (args, _) => Revert(
                Args.String(args, "file"), Args.String(args, "version"), live(), reloadFromDisk)));

        d.Register(new McpTool(
            "gloom_switch_branch",
            "Switch the project's current system option (git branch). Swaps the working tree to that option and reloads " +
            "every open definition in the repo. Use it to compare or adopt a design strategy listed by gloom_branches; " +
            "it never deletes a branch or pushes. \"branch\" is required.",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription + " Any definition in the repo anchors it (the switch is project-wide).")
                .String("branch", "Required. The branch (system option) to switch to, e.g. \"envelope_b\". gloom_branches lists them.")
                .Build(),
            ToolAccess.Write,
            (args, _) => SwitchBranch(
                Args.String(args, "file"), Args.String(args, "branch"), live(), reloadAllInRepo)));

        d.Register(new McpTool(
            "gloom_tag",
            "Tag a milestone: creates an annotated git tag on a committed version carrying the toolchain pin " +
            "(Rhino / Grasshopper / Rhino.Inside.Revit / G-Loom versions) and your notes, so the deliverable is " +
            "reproducible and auditable later. \"name\" is required (e.g. \"release_03\"); \"version\" is the commit to " +
            "pin (default: the last committed version); \"notes\" is optional.",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription)
                .String("name", "Required. The tag name, e.g. \"release_03\" or \"sprint_12_signoff\" (spaces become hyphens).")
                .String("version", VersionRef.ArgDescription + " Omit to tag the last committed version.")
                .String("notes", "Optional. Free-text notes recorded on the tag.")
                .Build(),
            ToolAccess.Write,
            (args, _) => Tag(
                Args.String(args, "file"), Args.String(args, "name"), Args.String(args, "version"),
                Args.String(args, "notes"), live(), captureToolchain, refreshTracker)));
    }

    public static ToolResult Commit(
        string? file, string? subject, string? description, string? agent, string? intent,
        LiveSnapshot? live, Func<string> serializeActiveDocument, Action refreshTracker)
    {
        var f = ProjectLocator.Locate(file, live);
        if (!f.IsActiveDocument)
            throw new ToolArgumentException(
                "gloom_commit can only commit the active Grasshopper document (the canvas is the source of truth); " +
                $"\"{f.GhRel}\" is not the active one. Open it and retry.");

        if (string.IsNullOrWhiteSpace(subject))
            throw new ToolArgumentException(
                "\"subject\" is required: one line naming the design decision (it becomes the commit headline).");

        var json = serializeActiveDocument();

        // The panel stages its pair and holds it across a modal dialog; committing into
        // that window would carry the human's files away under this message.
        if (!IndexGate.TryEnter("an agent"))
            return ToolResult.Error(
                $"{IndexGate.Holder ?? "Someone else"} is committing right now, so the staging area is busy. Retry in a moment.");

        var jsonFull = Path.Combine(f.RepoRoot, f.JsonRel);
        IReadOnlyList<string> staged;
        try
        {
            staged = GLoomRepository.StageForCommit(f.RepoRoot, json, jsonFull, alsoStageFullPath: f.GhFullPath);
        }
        catch
        {
            IndexGate.Exit();
            throw;
        }

        if (staged.Count == 0)
        {
            IndexGate.Exit();
            return ToolResult.Text(
                $"Nothing to commit: {f.BaseName} has no changes against its last committed version. Make an edit on the " +
                "canvas first, then commit.");
        }

        try
        {
            var nextV = CommitVersioning.NextVersion(f.RepoRoot, f.GhRel, f.JsonRel);
            var versionLabel = CommitVersioning.FormatMessage(f.BaseName, nextV);
            var trailers = new List<KeyValuePair<string, string>> { new("Gloom-Version", versionLabel) };
            if (!string.IsNullOrWhiteSpace(agent)) trailers.Add(new("Gloom-Agent", agent.Trim()));
            if (!string.IsNullOrWhiteSpace(intent)) trailers.Add(new("Gloom-Intent", intent.Trim()));
            var body = CommitTrailers.Append(description, trailers);

            var sha = GLoomRepository.CommitStaged(f.RepoRoot, subject.Trim(), body)
                ?? throw new InvalidOperationException("git reported nothing staged at commit time.");

            // Recompute the tracker's "current commit" and tell the panel to re-read, so the
            // new version shows without a manual Refresh.
            refreshTracker();

            return ToolResult.Json(new
            {
                committed = true,
                sha,
                shortSha = VersionRef.Short(sha),
                version = versionLabel,
                subject = subject.Trim(),
                file = f.GhFullPath,
                definitionPath = f.GhRel,
                staged,
                note = "Committed; the canvas now matches this version and G-Loom's history has a new entry.",
            });
        }
        catch (Exception ex)
        {
            GLoomRepository.UnstagePaths(f.RepoRoot, staged);
            return ToolResult.Error($"Commit failed (staged changes were rolled back): {ex.Message}");
        }
        finally
        {
            IndexGate.Exit();
        }
    }

    public static ToolResult Revert(string? file, string? version, LiveSnapshot? live, Action<string> reloadFromDisk)
    {
        var f = ProjectLocator.Locate(file, live);
        var v = string.IsNullOrWhiteSpace(version)
            ? VersionRef.LastCommitted(f)
            : VersionRef.Resolve(f, version, VersionRef.Working);

        if (v.IsWorkingTree || v.Sha is null)
            throw new ToolArgumentException(
                "gloom_revert needs a committed version to restore to (a label, SHA, tag or branch); \"working\" is the " +
                "current state on disk, so there is nothing to revert to. gloom_history lists the versions that exist.");

        // A checkout of these paths rewrites the index entries for them, so it must not
        // land while the panel is holding a staged pair.
        if (!IndexGate.TryEnter("an agent"))
            return ToolResult.Error(
                $"{IndexGate.Holder ?? "Someone else"} is committing right now, so the staging area is busy. Retry in a moment.");
        try
        {
            GLoomRepository.Restore(f.RepoRoot, v.Sha, new[] { f.GhRel, f.JsonRel });
        }
        finally
        {
            IndexGate.Exit();
        }
        reloadFromDisk(f.GhFullPath);

        return ToolResult.Json(new
        {
            reverted = true,
            file = f.GhFullPath,
            definitionPath = f.GhRel,
            restoredTo = new { version = v.Label, sha = v.Sha, shortSha = v.ShortSha },
            note = "Restored the .gh + .gloom.json pair to that commit and reloaded the canvas. Uncommitted edits made " +
                   "after that version were discarded.",
        });
    }

    public static ToolResult SwitchBranch(string? file, string? branch, LiveSnapshot? live, Action<string> reloadAllInRepo)
    {
        var f = ProjectLocator.Locate(file, live);
        if (string.IsNullOrWhiteSpace(branch))
            throw new ToolArgumentException(
                "\"branch\" is required: the system option to switch to. gloom_branches lists the available ones.");

        var name = branch.Trim();
        var branches = GLoomRepository.GetBranches(f.RepoRoot);
        var target = branches.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.Ordinal))
            ?? throw new ToolArgumentException(
                $"\"{name}\" is not a branch on this project. Available: {string.Join(", ", branches.Select(b => b.Name))}.");

        var affected = GLoomRepository.ListAffectedGhFiles(f.RepoRoot, target.Name);

        // A branch switch rewrites the whole index; git would either refuse or carry the
        // panel's staged pair across to the other system option.
        if (!IndexGate.TryEnter("an agent"))
            return ToolResult.Error(
                $"{IndexGate.Holder ?? "Someone else"} is committing right now, so the staging area is busy. Retry in a moment.");
        try
        {
            GLoomRepository.SwitchBranch(f.RepoRoot, target.Name);
        }
        finally
        {
            IndexGate.Exit();
        }
        reloadAllInRepo(f.RepoRoot);

        return ToolResult.Json(new
        {
            switched = true,
            branch = target.Name,
            wasAlreadyCurrent = target.IsCurrent,
            affectedDefinitions = affected,
            note = target.IsCurrent
                ? "Already on that branch; nothing changed."
                : "HEAD moved to that system option, the working tree was swapped, and open definitions were reloaded.",
        });
    }

    public static ToolResult Tag(
        string? file, string? name, string? version, string? notes,
        LiveSnapshot? live, Func<Toolchain> captureToolchain, Action refreshTracker)
    {
        var f = ProjectLocator.Locate(file, live);
        if (string.IsNullOrWhiteSpace(name))
            throw new ToolArgumentException(
                "\"name\" is required: the milestone tag name, e.g. \"release_03\" or \"sprint_12_signoff\".");

        var tag = name.Trim().Replace(' ', '-');
        if (tag.Length == 0)
            throw new ToolArgumentException("\"name\" must not be empty after normalizing spaces to hyphens.");

        var v = string.IsNullOrWhiteSpace(version)
            ? VersionRef.LastCommitted(f)
            : VersionRef.Resolve(f, version, VersionRef.Working);
        if (v.IsWorkingTree || v.Sha is null)
            throw new ToolArgumentException(
                "gloom_tag pins a committed version; name a label, SHA, tag or branch (or omit \"version\" for the last " +
                "committed one).");

        var author = Identity.Resolve(f.RepoRoot)
            ?? throw new ToolArgumentException(Identity.NotSetMessage);

        var metadata = new TagMetadata(
            SchemaVersion: 2,
            TagName: tag,
            CommitSha: v.Sha,
            CreatedAt: DateTimeOffset.UtcNow,
            CreatedBy: author.Name,
            Toolchain: captureToolchain(),
            Notes: string.IsNullOrWhiteSpace(notes) ? null : notes.Trim());

        GLoomRepository.CreateAnnotatedTag(f.RepoRoot, tag, v.Sha, TagMetadataJson.Write(metadata), author.Name, author.Email);

        // A tag leaves TrackedState unchanged, so the panel would not re-read; force it.
        refreshTracker();

        return ToolResult.Json(new
        {
            tagged = true,
            tag = tag,
            commit = new { version = v.Label, sha = v.Sha, shortSha = v.ShortSha },
            createdBy = author.Name,
            toolchain = metadata.Toolchain,
            note = "Annotated tag created with the toolchain pinned for reproducibility; it travels with `git push --tags`.",
        });
    }
}
