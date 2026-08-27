using System;
using System.Collections.Generic;
using System.Linq;
using GLoom.Mcp.Protocol;
using GLoom.Serialization;

namespace GLoom.Mcp.Tools.Memory;

/// <summary>
/// The version-reading tools: a recipe at one version and the diff between two, shaped as
/// the panel's history drawer shows them (the narrative over a run of versions lives in
/// ChangeNarrative). Both read the canonical JSON the panel diffs, through VersionRef, so
/// an agent sees exactly what the drawer shows. Host-free; git and JSON work runs on the
/// request thread.
/// </summary>
public static class VersionTools
{
    private const int DefaultPage = 50, MaxPage = 200;
    private const int DefaultItems = 200, MaxItems = 1000;

    private static readonly string[] ObjectKinds = { "component", "param" };

    internal const string WorkingTreeNote =
        "The recipe on disk (.gloom.json) is written at commit time, so edits made on the canvas since " +
        "the last commit are not in it; gloom_status reports unsavedEdits.";

    public sealed record VersionInfo(
        string Reference, string? Sha, string? ShortSha, string Label, string? VersionLabel,
        string? Subject, DateTimeOffset? When, bool IsWorkingTree);

    public static void Register(McpDispatcher d, Func<LiveSnapshot?> live)
    {
        d.Register(new McpTool(
            "gloom_read_version",
            "The recipe of a Grasshopper definition at one version: its objects (components and parameters " +
            "with names, pivots, wiring and persistent values such as slider settings) and groups, paged. " +
            "Call it to inspect a previous version, or the file on disk, without opening it. " +
            "\"file\" is optional (the active document); \"version\" defaults to \"working\".",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription)
                .String("version", VersionRef.ArgDescription + " Default: \"working\".")
                .Integer("offset", "Index of the first object to return (default 0).", min: 0)
                .Integer("limit", "Objects per page (default 50, max 200).", min: 1, max: MaxPage)
                .String("query", "Case-insensitive substring matched against each object's name, nickname and instanceGuid.")
                .Enum("kind", "Only objects of this kind: \"component\" (has inputs and outputs) or \"param\" (a free-floating " +
                              "slider, panel, toggle, value list, swatch or other parameter). Omit for both.", ObjectKinds)
                .Build(),
            ToolAccess.Read,
            (args, _) => ReadVersion(
                Args.String(args, "file"), Args.String(args, "version"),
                Args.Int(args, "offset", 0), Args.Int(args, "limit", DefaultPage),
                Args.String(args, "query"), Args.String(args, "kind"), live())));

        d.Register(new McpTool(
            "gloom_diff",
            "What changed in a Grasshopper definition between two versions, exactly as the panel's history " +
            "drawer lists it: objects added, removed and modified (renamed, moved, rewired, value changed) " +
            "with per-change details, plus group changes. Defaults compare the last committed version with " +
            "the file on disk. Call it to review an edit before committing, or to see what one version " +
            "changed against another. \"file\" is optional (the active document).",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription)
                .String("from", VersionRef.ArgDescription + " Default: the last committed version of this definition.")
                .String("to", VersionRef.ArgDescription + " Default: \"working\".")
                .Integer("maxItems", "Maximum entries per category (default 200, max 1000).", min: 1, max: MaxItems)
                .Build(),
            ToolAccess.Read,
            (args, _) => Diff(
                Args.String(args, "file"), Args.String(args, "from"), Args.String(args, "to"),
                Args.Int(args, "maxItems", DefaultItems), live())));

        d.Register(new McpTool(
            "gloom_explain_changes",
            "A plain-language markdown narrative of what versions of a Grasshopper definition changed, " +
            "computed from the recipes (no model involved): one section per version with date, author, " +
            "agent provenance, the commit description and the list of changes. Pass \"version\" for one " +
            "version against its predecessor (default: the last committed version), or \"from\"/\"to\" for " +
            "every version in between, oldest first; \"from\" defaults to the last committed version and \"to\" " +
            "to the file on disk, which adds a final section for uncommitted changes. \"file\" is optional " +
            "(the active document).",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription)
                .String("version", VersionRef.ArgDescription + " Explained against the previous version of this definition.")
                .String("from", VersionRef.ArgDescription + " Start of a range (exclusive). Default: the last committed " +
                                "version of this definition, so passing only \"to\" walks from there.")
                .String("to", VersionRef.ArgDescription + " End of a range (inclusive). Default: \"working\".")
                .Build(),
            ToolAccess.Read,
            (args, _) => ChangeNarrative.ExplainChanges(
                Args.String(args, "file"), Args.String(args, "version"),
                Args.String(args, "from"), Args.String(args, "to"), live())));
    }

    public static ToolResult ReadVersion(
        string? file, string? version, int offset, int limit, string? query, string? kind, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        var v = VersionRef.Resolve(f, version, VersionRef.Working);
        var doc = VersionRef.LoadRecipe(f, v).Document;
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxPage);

        var q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var k = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
        if (k is not null && !ObjectKinds.Contains(k, StringComparer.OrdinalIgnoreCase))
            throw new ToolArgumentException(
                $"\"kind\" must be \"component\" or \"param\" (got \"{k}\"); sliders, panels, toggles and value lists are " +
                "params, so use \"query\" to narrow by name.");
        var matched = doc.Objects
            .Where(o => q is null || Matches(o, q))
            .Where(o => k is null || string.Equals(o.Kind, k, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var page = matched.Skip(offset).Take(limit).ToList();
        var hasMore = offset + page.Count < matched.Count;

        return ToolResult.Json(new
        {
            file = f.GhFullPath,
            definitionPath = f.GhRel,
            recipePath = f.JsonRel,
            version = Describe(v),
            schemaVersion = doc.SchemaVersion,
            document = new { name = doc.Document.Name, description = doc.Document.Description },
            totals = new { objects = doc.Objects.Count, groups = doc.Groups.Count, byKind = CountByKind(doc) },
            filter = q is null && k is null ? null : new { query = q, kind = k, matched = matched.Count },
            page = new { offset, limit, returned = page.Count, hasMore, nextOffset = hasMore ? offset + page.Count : (int?)null },
            objects = page,
            groups = doc.Groups,
            note = v.IsWorkingTree ? WorkingTreeNote : null,
        });
    }

    public static ToolResult Diff(string? file, string? from, string? to, int maxItems, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        maxItems = Math.Clamp(maxItems, 1, MaxItems);
        var fromV = string.IsNullOrWhiteSpace(from) ? VersionRef.LastCommitted(f) : VersionRef.Resolve(f, from, VersionRef.Working);
        var toV = VersionRef.Resolve(f, to, VersionRef.Working);
        var fromDoc = VersionRef.LoadRecipe(f, fromV).Document;
        var toDoc = VersionRef.LoadRecipe(f, toV).Document;

        var diff = DocumentDiff.Compute(fromDoc, toDoc);
        var owners = SourceOwners(fromDoc, toDoc);
        var counts = new[]
        {
            diff.ObjectsAdded.Count, diff.ObjectsRemoved.Count, diff.ObjectsModified.Count,
            diff.GroupsAdded.Count, diff.GroupsRemoved.Count, diff.GroupsModified.Count,
        };
        var truncated = counts.Any(n => n > maxItems);
        var notes = new List<string>();
        if (toV.IsWorkingTree) notes.Add(WorkingTreeNote);
        if (truncated) notes.Add("Some categories were cut at maxItems; counts hold the full numbers.");

        return ToolResult.Json(new
        {
            file = f.GhFullPath,
            definitionPath = f.GhRel,
            from = Describe(fromV),
            to = Describe(toV),
            isEmpty = diff.IsEmpty,
            totalChanges = diff.TotalChanges,
            metaChanged = diff.MetaChanged,
            headline = DiffSummaryText.Headline(diff, f.BaseName),
            counts = new
            {
                added = counts[0], removed = counts[1], modified = counts[2],
                groupsAdded = counts[3], groupsRemoved = counts[4], groupsModified = counts[5],
            },
            added = diff.ObjectsAdded.Take(maxItems).Select(Brief).ToList(),
            removed = diff.ObjectsRemoved.Take(maxItems).Select(Brief).ToList(),
            modified = diff.ObjectsModified.Take(maxItems).Select(c => Modified(c, owners)).ToList(),
            groupsAdded = diff.GroupsAdded.Take(maxItems).Select(BriefGroup).ToList(),
            groupsRemoved = diff.GroupsRemoved.Take(maxItems).Select(BriefGroup).ToList(),
            groupsModified = diff.GroupsModified.Take(maxItems)
                .Select(c => new { name = c.To.Name, instanceGuid = c.To.InstanceGuid, summary = c.Summary }).ToList(),
            truncated,
            note = notes.Count == 0 ? null : string.Join(" ", notes),
        });
    }

    public static ToolResult ExplainChanges(string? file, string? version, string? from, string? to, LiveSnapshot? live) =>
        ChangeNarrative.ExplainChanges(file, version, from, to, live);

    private static VersionInfo Describe(ResolvedVersion v) =>
        new(v.Reference, v.Sha, v.ShortSha, v.Label, v.VersionLabel, v.Commit?.Message, v.Commit?.When, v.IsWorkingTree);

    private static object Brief(CanonicalObject o) =>
        new { name = DocumentDiff.DisplayName(o), instanceGuid = o.InstanceGuid, kind = o.Kind, componentName = o.Name, pivot = o.Pivot };

    private static object BriefGroup(CanonicalGroup g) =>
        new { name = g.Name, instanceGuid = g.InstanceGuid, members = g.Members };

    private static object Modified(ObjectChange c, IReadOnlyDictionary<string, CanonicalObject> owners)
    {
        var k = c.Kinds;
        return new
        {
            name = DocumentDiff.DisplayName(c.To),
            instanceGuid = c.To.InstanceGuid,
            kind = c.To.Kind,
            kinds = KindNames(k),
            summary = c.Summary,
            details = new
            {
                renamed = k.HasFlag(ObjectChangeKind.Renamed)
                    ? new { from = DocumentDiff.DisplayName(c.From), to = DocumentDiff.DisplayName(c.To) } : null,
                moved = k.HasFlag(ObjectChangeKind.Moved) ? new { from = c.From.Pivot, to = c.To.Pivot } : null,
                wires = k.HasFlag(ObjectChangeKind.WiresChanged) ? WireChanges(c.From, c.To, owners) : null,
                persistent = k.HasFlag(ObjectChangeKind.PersistentChanged)
                    ? new { before = c.From.Persistent, after = c.To.Persistent } : null,
            },
        };
    }

    private static List<string> KindNames(ObjectChangeKind k)
    {
        var names = new List<string>();
        if (k.HasFlag(ObjectChangeKind.Renamed)) names.Add("renamed");
        if (k.HasFlag(ObjectChangeKind.Moved)) names.Add("moved");
        if (k.HasFlag(ObjectChangeKind.WiresChanged)) names.Add("wiresChanged");
        if (k.HasFlag(ObjectChangeKind.PersistentChanged)) names.Add("persistentChanged");
        return names;
    }

    private static List<object> WireChanges(CanonicalObject from, CanonicalObject to, IReadOnlyDictionary<string, CanonicalObject> owners)
    {
        var before = from.Inputs.ToDictionary(p => p.InstanceGuid, StringComparer.Ordinal);
        var after = to.Inputs.ToDictionary(p => p.InstanceGuid, StringComparer.Ordinal);
        var changes = new List<object>();
        foreach (var id in before.Keys.Union(after.Keys, StringComparer.Ordinal))
        {
            var b = before.GetValueOrDefault(id)?.Sources ?? Array.Empty<string>();
            var a = after.GetValueOrDefault(id)?.Sources ?? Array.Empty<string>();
            if (b.SequenceEqual(a, StringComparer.Ordinal)) continue;
            var input = after.GetValueOrDefault(id) ?? before[id];
            changes.Add(new
            {
                input = new { name = string.IsNullOrEmpty(input.Nickname) ? input.Name : input.Nickname, instanceGuid = id },
                before = b,
                after = a,
                connected = a.Except(b, StringComparer.Ordinal).Select(s => Source(s, owners)).ToList(),
                disconnected = b.Except(a, StringComparer.Ordinal).Select(s => Source(s, owners)).ToList(),
            });
        }
        return changes;
    }

    private static object Source(string guid, IReadOnlyDictionary<string, CanonicalObject> owners) =>
        new { sourceGuid = guid, sourceObject = owners.TryGetValue(guid, out var o) ? DocumentDiff.DisplayName(o) : null };

    /// <summary>A wire's source is an output parameter's guid, or the object's own guid when a
    /// free-floating parameter (slider, panel) feeds the wire directly. Later documents win.</summary>
    private static Dictionary<string, CanonicalObject> SourceOwners(params CanonicalDocument[] docs)
    {
        var map = new Dictionary<string, CanonicalObject>(StringComparer.Ordinal);
        foreach (var doc in docs)
            foreach (var o in doc.Objects)
            {
                map[o.InstanceGuid] = o;
                foreach (var p in o.Outputs) map[p.InstanceGuid] = o;
            }
        return map;
    }

    private static bool Matches(CanonicalObject o, string q) =>
        o.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || o.Nickname.Contains(q, StringComparison.OrdinalIgnoreCase)
        || o.InstanceGuid.Contains(q, StringComparison.OrdinalIgnoreCase);

    internal static Dictionary<string, int> CountByKind(CanonicalDocument doc) =>
        doc.Objects.GroupBy(o => o.Kind, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    internal static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
