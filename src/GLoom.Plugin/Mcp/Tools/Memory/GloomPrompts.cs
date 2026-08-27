using System;
using System.Collections.Generic;
using System.Text;
using GLoom.Mcp.Protocol;

namespace GLoom.Mcp.Tools.Memory;

/// <summary>
/// Conversation openers the client's model reasons over. The server only gathers facts
/// through the memory tools and hands them over in fenced blocks under the instructions;
/// nothing here changes a definition or the project. Host-free.
/// </summary>
public static class GloomPrompts
{
    private const int DefaultHistory = 30, MaxHistory = 50, DiffItems = 200;

    public static void Register(McpDispatcher d, Func<LiveSnapshot?> live)
    {
        d.Register(new McpPrompt(
            "review-changes",
            "Review what changed in a Grasshopper definition between two versions as a design reviewer and draft the commit message.",
            new[]
            {
                new PromptArgument("file", ProjectLocator.FileArgDescription),
                new PromptArgument("from", VersionRef.ArgDescription + " Default: the last committed version of this definition."),
                new PromptArgument("to", VersionRef.ArgDescription + " Default: \"working\", the file on disk."),
            },
            (args, _) => ReviewChanges(Arg(args, "file"), Arg(args, "from"), Arg(args, "to"), live()),
            Title: "Review what changed in a definition"));

        d.Register(new McpPrompt(
            "design-history",
            "Tell the story of a Grasshopper definition from its record of decisions: the arc, the turning points, the milestones and who made them.",
            new[]
            {
                new PromptArgument("file", ProjectLocator.FileArgDescription),
                new PromptArgument("limit", $"Maximum number of versions to include, oldest to newest (default {DefaultHistory}, max {MaxHistory})."),
            },
            (args, _) => DesignHistory(Arg(args, "file"), Limit(args), live()),
            Title: "Tell the story of a definition"));
    }

    public static PromptResult ReviewChanges(string? file, string? from, string? to, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        var status = MemoryTools.Status(file, live).Content[0].Text!;
        var diff = VersionTools.Diff(file, from, to, DiffItems, live).Content[0].Text!;
        string? narrative = null, narrativeNote = null;
        // Always a range: without "to", ExplainChanges would explain the last commit against
        // its predecessor rather than the same span the diff above covers.
        try { narrative = VersionTools.ExplainChanges(file, null, from, to ?? VersionRef.Working, live).Content[0].Text; }
        catch (ToolArgumentException e) { narrativeNote = e.Message; }

        var sb = new StringBuilder();
        sb.Append("You are reviewing changes to the Grasshopper definition `").Append(f.GhRel).Append("` in a G-Loom project. ")
          .Append("Act as a design reviewer, not a version-control tool: the data below describes the parametric recipe ")
          .Append("(components, parameters, wires and persistent values such as slider settings), and the changes are design moves.\n\n")
          .Append("Do this, in order:\n")
          .Append("1. Summarise what changed in design terms: what the definition now does differently, not which objects changed.\n")
          .Append("2. Call out everything the designer should confirm before committing: deleted components, inputs that lost a source ")
          .Append("(disconnected wires), and persistent values that changed (sliders, panels, toggles, value lists, colours, gradients). ")
          .Append("Quote the before and after values.\n")
          .Append("3. Say whether the changes look intentional and coherent as one design move, or whether they mix unrelated edits ")
          .Append("or leave something half done (a component added but unwired, a value changed with no downstream effect).\n")
          .Append("4. Draft a commit message in G-Loom's voice: the subject is the design move in a few words (what changed in the design), ")
          .Append("the description says why, in one or two sentences. Present it as `Subject:` and `Description:` lines.\n\n")
          .Append("Refer to versions by their labels (for example tower_V012) as the data does. ")
          .Append("You must NOT modify anything: do not call tools that change files, the canvas or the project. You only review.\n\n");

        sb.Append("Status of the definition (from gloom_status):\n\n```json\n").Append(status).Append("\n```\n\n");
        sb.Append("The changes (from gloom_diff):\n\n```json\n").Append(diff).Append("\n```\n");
        if (narrative is not null)
            sb.Append("\nThe same changes as a narrative (from gloom_explain_changes):\n\n```markdown\n").Append(narrative).Append("```\n");
        else if (narrativeNote is not null)
            sb.Append("\n(No narrative for this range: ").Append(narrativeNote).Append(")\n");

        return new PromptResult(
            $"Review of {f.GhRel}: {DescribeRange(from, to)}.",
            new[] { PromptMessage.User(sb.ToString()) });
    }

    public static PromptResult DesignHistory(string? file, int limit, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        limit = Math.Clamp(limit, 1, MaxHistory);
        var record = RecordTools.DecisionRecordMarkdown(f, live, limit, includeChanges: true, newestFirst: false);

        var sb = new StringBuilder();
        sb.Append("Below is the record of decisions for the Grasshopper definition `").Append(f.GhRel).Append("` in a G-Loom project: ")
          .Append("every committed version in order, oldest first, with its subject, description, author, date, agent provenance ")
          .Append("(from the Gloom-* trailers), milestone tags with the toolchain they were pinned on, where the current system option ")
          .Append("branched off, and the recipe changes each version made against the previous one.\n\n")
          .Append("Tell the story of this definition:\n")
          .Append("1. The arc of the design: what changed and why, in order, as a narrative rather than a list.\n")
          .Append("2. The key turning points: the versions where the design direction changed, and what prompted them.\n")
          .Append("3. Which versions are tagged as milestones or pinned, and on which toolchain (Rhino, Grasshopper, Rhino.Inside.Revit, ")
          .Append("G-Loom versions), since a recipe is only reproducible on the toolchain it was made with.\n")
          .Append("4. Who made the changes, people and agents alike, using the authors and the Gloom-Agent / Gloom-Agent-Session / ")
          .Append("Gloom-Intent trailers.\n")
          .Append("5. Open threads: a decision reversed later, a system option started and abandoned, a value that keeps oscillating, ")
          .Append("or anything the record leaves unexplained.\n\n")
          .Append("Version labels such as tower_V012 (or V012 for short) are the vocabulary to use when referring to versions; ")
          .Append("they are what the designer sees in the G-Loom panel. Branches are system options, substitutable design strategies, ")
          .Append("not detours.\n\n");
        sb.Append("```markdown\n").Append(record).Append("```\n");

        return new PromptResult($"The design history of {f.GhRel}.", new[] { PromptMessage.User(sb.ToString()) });
    }

    private static string DescribeRange(string? from, string? to) =>
        $"from {(string.IsNullOrWhiteSpace(from) ? "the last committed version" : from.Trim())} " +
        $"to {(string.IsNullOrWhiteSpace(to) ? "the file on disk" : to.Trim())}";

    private static string? Arg(IReadOnlyDictionary<string, string> args, string name) =>
        args.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static int Limit(IReadOnlyDictionary<string, string> args)
    {
        var raw = Arg(args, "limit");
        if (raw is null) return DefaultHistory;
        return int.TryParse(raw, out var n) && n > 0
            ? n
            : throw new ToolArgumentException($"\"limit\" must be a positive integer (default {DefaultHistory}, max {MaxHistory}); got \"{raw}\".");
    }
}
