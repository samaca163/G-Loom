using System;
using System.Collections.Generic;
using System.IO;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.State;
using GLoom.Vcs;

namespace GLoom.Mcp.Tools.Memory;

/// <summary>
/// The edit envelope: a checkpoint before an agent starts changing a definition and an
/// attributed commit after. It exists because the interesting edits need not come through
/// G-Loom at all - another server can drive the same canvas - so the guarantee cannot be
/// "every mutation is recorded" but "there is always a version to go back to", which a
/// checkpoint gives regardless of who did the editing.
///
/// Opening one also aims the canvas overlay at the checkpoint, so the human watches the
/// agent's changes accumulate as highlights instead of reading about them afterwards.
/// </summary>
public static class EnvelopeTools
{
    public static void Register(
        McpDispatcher d,
        Func<LiveSnapshot?> live,
        Func<string> serializeActiveDocument,
        Action<string> setOverlayReference,
        Action<string> reloadFromDisk,
        Action refreshTracker)
    {
        d.Register(new McpTool(
            "gloom_begin_edit",
            "Open an edit envelope on the active definition before changing anything on the canvas - with these " +
            "tools or another server's. Records the version the definition stands at as a checkpoint, committing " +
            "the human's unsaved work first so the checkpoint is a real version to return to, and points G-Loom's " +
            "canvas overlay at it so the person watching Rhino sees your changes highlighted as you make them. " +
            "gloom_set_value refuses while no envelope is open. Close it with gloom_end_edit.",
            Schema.Object()
                .String("agent", "Who is editing, e.g. \"claude-code\". Recorded in the Gloom-Agent trailer.")
                .String("session", "Optional. An id for this conversation, for the Gloom-Agent-Session trailer.")
                .String("intent", "Required. One line on what you are about to change and why, e.g. \"raise the podium to four storeys\".")
                .Build(),
            ToolAccess.Write,
            (args, _) => Begin(
                Args.String(args, "agent"), Args.String(args, "session"), Args.String(args, "intent"),
                live(), serializeActiveDocument, setOverlayReference, refreshTracker)));

        d.Register(new McpTool(
            "gloom_end_edit",
            "Close the open edit envelope. By default commits everything changed since the checkpoint as a new " +
            "version, attributed to the human and carrying your Gloom-Agent / Gloom-Intent / Gloom-Checkpoint-Base " +
            "trailers so the history says who made the move and what it was measured against. With \"discard\" " +
            "true it restores the definition to the checkpoint and reloads the canvas instead, throwing your edits " +
            "away. Either way the overlay goes back to comparing against the latest version.",
            Schema.Object()
                .String("subject", "One line naming the design decision, for the commit headline. Required unless discarding.")
                .String("description", "Optional. The longer explanation of why (the commit body).")
                .Boolean("discard", "Throw the changes away and restore the checkpoint instead of committing (default false).")
                .Build(),
            ToolAccess.Write,
            (args, _) => End(
                Args.String(args, "subject"), Args.String(args, "description"), Args.Bool(args, "discard", false),
                live(), serializeActiveDocument, setOverlayReference, reloadFromDisk, refreshTracker)));
    }

    /// <summary>The guard every canvas-mutating tool calls first. Null when the caller may proceed.</summary>
    public static string? RequireOpen(LocatedFile f)
    {
        var open = EnvelopeStore.Current;
        if (open is null)
            return "No edit envelope is open, so there would be no checkpoint to undo this from. Call " +
                   "gloom_begin_edit with your intent first, then retry.";

        if (!string.Equals(open.DefinitionPath, f.GhRel, StringComparison.OrdinalIgnoreCase))
            return $"The open envelope is on \"{open.DefinitionPath}\", not \"{f.GhRel}\". Close it with " +
                   "gloom_end_edit before editing another definition.";

        return null;
    }

    public static ToolResult Begin(
        string? agent, string? session, string? intent, LiveSnapshot? live,
        Func<string> serializeActiveDocument, Action<string> setOverlayReference, Action refreshTracker)
    {
        if (string.IsNullOrWhiteSpace(intent))
            throw new ToolArgumentException(
                "\"intent\" is required: one line on what you are about to change, so whoever reviews it later " +
                "knows what you were trying to do.");

        var f = ProjectLocator.Locate(null, live);
        if (!f.IsActiveDocument)
            throw new ToolArgumentException(
                "gloom_begin_edit checkpoints the active Grasshopper document, and there is no active one.");

        if (EnvelopeStore.Current is { } already)
            return ToolResult.Error(
                $"An edit envelope is already open on \"{already.DefinitionPath}\", opened by {already.Describe()}. " +
                "Close it with gloom_end_edit before opening another.");

        // Commit the human's pending work first, so the checkpoint is a version that can be
        // returned to rather than a sha with uncommitted changes floating above it.
        var committedNow = false;
        var json = serializeActiveDocument();

        if (!IndexGate.TryEnter("an agent"))
            return ToolResult.Error(
                $"{IndexGate.Holder ?? "Someone else"} is committing right now. Retry in a moment.");

        string? sha;
        try
        {
            var jsonFull = Path.Combine(f.RepoRoot, f.JsonRel);
            var staged = GLoomRepository.StageForCommit(f.RepoRoot, json, jsonFull, alsoStageFullPath: f.GhFullPath);
            if (staged.Count > 0)
            {
                var checkpointAuthor = Identity.Resolve(f.RepoRoot);
                if (checkpointAuthor is null)
                {
                    GLoomRepository.UnstagePaths(f.RepoRoot, staged);
                    throw new ToolArgumentException(Identity.NotSetMessage);
                }

                var pendingVersion = CommitVersioning.NextVersion(f.RepoRoot, f.GhRel, f.JsonRel);
                var body = CommitTrailers.Append(
                    "Committed automatically so the agent's work has a version to return to.",
                    new[]
                    {
                        new KeyValuePair<string, string>(
                            "Gloom-Version", CommitVersioning.FormatMessage(f.BaseName, pendingVersion)),
                    });

                sha = GLoomRepository.CommitStaged(
                    f.RepoRoot, $"Checkpoint before {agent ?? "an agent"} edits {f.BaseName}", body,
                    checkpointAuthor.Name, checkpointAuthor.Email);
                committedNow = sha is not null;
            }
            else
            {
                sha = VersionRef.LastCommitted(f).Sha;
            }
        }
        finally
        {
            IndexGate.Exit();
        }

        if (sha is null)
            return ToolResult.Error(
                $"{f.BaseName} has no committed version yet, so there is nothing to checkpoint against. Commit it " +
                "once with gloom_commit, then open an envelope.");

        EnvelopeStore.Open(new EditEnvelope(
            f.RepoRoot, f.GhRel, sha, agent?.Trim(), session?.Trim(), intent.Trim(), DateTimeOffset.Now));

        // Aim the overlay at the checkpoint: from here the human sees every change drawn on
        // the canvas as it lands, rather than only described afterwards.
        setOverlayReference(sha);
        refreshTracker();

        return ToolResult.Json(new
        {
            opened = true,
            file = f.GhFullPath,
            definitionPath = f.GhRel,
            checkpoint = new { sha, shortSha = VersionRef.Short(sha), committedNow },
            agent = agent?.Trim(),
            intent = intent.Trim(),
            note = (committedNow
                       ? "Unsaved work was committed as the checkpoint, so none of it is lost. "
                       : "The last committed version is the checkpoint. ")
                   + "The canvas overlay now compares against it, so the person watching Rhino sees your changes " +
                     "highlighted as you make them. Call gloom_end_edit when you are done.",
        });
    }

    public static ToolResult End(
        string? subject, string? description, bool discard, LiveSnapshot? live,
        Func<string> serializeActiveDocument, Action<string> setOverlayReference,
        Action<string> reloadFromDisk, Action refreshTracker)
    {
        var envelope = EnvelopeStore.Current
            ?? throw new ToolArgumentException("No edit envelope is open; gloom_begin_edit opens one.");

        var f = ProjectLocator.Locate(Path.Combine(envelope.RepoRoot, envelope.DefinitionPath), live);

        if (discard) return Discard(f, envelope, setOverlayReference, reloadFromDisk, refreshTracker);

        if (string.IsNullOrWhiteSpace(subject))
            throw new ToolArgumentException(
                "\"subject\" is required to commit: one line naming the design decision. Pass \"discard\": true to " +
                "throw the changes away instead.");

        var author = Identity.Resolve(f.RepoRoot) ?? throw new ToolArgumentException(Identity.NotSetMessage);
        var json = serializeActiveDocument();

        if (!IndexGate.TryEnter("an agent"))
            return ToolResult.Error($"{IndexGate.Holder ?? "Someone else"} is committing right now. Retry in a moment.");

        try
        {
            var jsonFull = Path.Combine(f.RepoRoot, f.JsonRel);
            var staged = GLoomRepository.StageForCommit(f.RepoRoot, json, jsonFull, alsoStageFullPath: f.GhFullPath);
            if (staged.Count == 0)
            {
                Reset(setOverlayReference, refreshTracker);
                return ToolResult.Text(
                    "Nothing changed since the checkpoint, so there was no version to make. The envelope is closed.");
            }

            try
            {
                var version = CommitVersioning.FormatMessage(
                    f.BaseName, CommitVersioning.NextVersion(f.RepoRoot, f.GhRel, f.JsonRel));

                var trailers = new List<KeyValuePair<string, string>> { new("Gloom-Version", version) };
                if (envelope.Agent is { Length: > 0 } a) trailers.Add(new("Gloom-Agent", a));
                if (envelope.Session is { Length: > 0 } s) trailers.Add(new("Gloom-Agent-Session", s));
                if (envelope.Intent is { Length: > 0 } i) trailers.Add(new("Gloom-Intent", i));
                trailers.Add(new("Gloom-Checkpoint-Base", envelope.CheckpointSha));

                var sha = GLoomRepository.CommitStaged(
                    f.RepoRoot, subject.Trim(), CommitTrailers.Append(description, trailers),
                    author.Name, author.Email)
                    ?? throw new InvalidOperationException("git reported nothing staged at commit time.");

                Reset(setOverlayReference, refreshTracker);

                return ToolResult.Json(new
                {
                    closed = true,
                    committed = true,
                    sha,
                    shortSha = VersionRef.Short(sha),
                    version,
                    subject = subject.Trim(),
                    definitionPath = f.GhRel,
                    checkpointBase = VersionRef.Short(envelope.CheckpointSha),
                    note = "Committed and the envelope closed. The version names you in its trailers, so the history " +
                           "drawer shows who made it; gloom_diff against the checkpoint shows exactly what changed, " +
                           "and the person reviewing can reject any single change on the canvas.",
                });
            }
            catch (Exception ex)
            {
                GLoomRepository.UnstagePaths(f.RepoRoot, staged);
                return ToolResult.Error($"Could not close the envelope (staged changes were rolled back): {ex.Message}");
            }
        }
        finally
        {
            IndexGate.Exit();
        }
    }

    private static ToolResult Discard(
        LocatedFile f, EditEnvelope envelope, Action<string> setOverlayReference,
        Action<string> reloadFromDisk, Action refreshTracker)
    {
        if (!IndexGate.TryEnter("an agent"))
            return ToolResult.Error($"{IndexGate.Holder ?? "Someone else"} is committing right now. Retry in a moment.");
        try
        {
            GLoomRepository.Restore(f.RepoRoot, envelope.CheckpointSha, new[] { f.GhRel, f.JsonRel });
        }
        finally
        {
            IndexGate.Exit();
        }

        // Without the reload the canvas keeps the discarded edits while git no longer has
        // them, and the panel reports a clean tree over a dirty canvas.
        reloadFromDisk(f.GhFullPath);
        Reset(setOverlayReference, refreshTracker);

        return ToolResult.Json(new
        {
            closed = true,
            discarded = true,
            definitionPath = f.GhRel,
            restoredTo = new { sha = envelope.CheckpointSha, shortSha = VersionRef.Short(envelope.CheckpointSha) },
            note = "Everything since the checkpoint was thrown away and the canvas reloaded from it.",
        });
    }

    private static void Reset(Action<string> setOverlayReference, Action refreshTracker)
    {
        EnvelopeStore.Close();
        setOverlayReference("HEAD");
        refreshTracker();
    }
}
