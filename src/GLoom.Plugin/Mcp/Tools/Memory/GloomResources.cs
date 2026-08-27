using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GLoom.Mcp.Protocol;
using GLoom.Vcs;

namespace GLoom.Mcp.Tools.Memory;

/// <summary>
/// The project's memory as @-mentionable resources: the status, system options and
/// milestones of the active project, and per definition its record of decisions, the
/// recipe on disk and the uncommitted changes. Every read goes through the same tool
/// entry points, so a resource and the tool it mirrors never disagree. Host-free.
/// </summary>
public sealed class GloomResources : IMcpResourceProvider
{
    private const string Scheme = "gloom://";
    private const string DefinitionPrefix = Scheme + "definition/";
    private const string JsonMime = "application/json";
    private const string MarkdownMime = "text/markdown";
    private const int RecordVersions = 50, DiffItems = 200, TagCount = 50;

    private static readonly string[] Shapes =
    {
        Scheme + "status", Scheme + "branches", Scheme + "tags",
        DefinitionPrefix + "<path>/record",
        DefinitionPrefix + "<path>/recipe", DefinitionPrefix + "<path>/recipe@<version>",
        DefinitionPrefix + "<path>/changes", DefinitionPrefix + "<path>/changes@<from>..<to>",
    };

    private readonly Func<LiveSnapshot?> _live;

    public GloomResources(Func<LiveSnapshot?> live) => _live = live;

    public IReadOnlyList<McpResource> List()
    {
        var live = _live();
        var list = new List<McpResource>();
        if (live?.ActiveFilePath is null || live.RepoRoot is null) return list;

        list.Add(new McpResource(Scheme + "status", "Project status", "Project status",
            "Where the active definition stands in its project's version history (what gloom_status returns).", JsonMime));
        list.Add(new McpResource(Scheme + "branches", "System options", "System options",
            "The project's branches as system options, with fork points and remote tracking (what gloom_branches returns).", JsonMime));
        list.Add(new McpResource(Scheme + "tags", "Milestones", "Milestones",
            "The project's tags, newest first, with the toolchain each was pinned on (what gloom_tags returns).", JsonMime));

        foreach (var rel in GLoomRepository.ListGhFilesInWorkingTree(live.RepoRoot))
        {
            var name = Path.GetFileName(rel);
            var uri = DefinitionPrefix + Uri.EscapeDataString(rel).Replace("%2F", "/");
            list.Add(new McpResource(uri + "/record", rel + "/record", $"{name} — decision record",
                $"Every committed version of {rel} in order, with subjects, descriptions, agents, tags and toolchain pins.", MarkdownMime));
            list.Add(new McpResource(uri + "/recipe", rel + "/recipe", $"{name} — recipe (working tree)",
                $"The canonical recipe of {rel} as it sits on disk: objects, wiring, groups and persistent values.", JsonMime));
            list.Add(new McpResource(uri + "/changes", rel + "/changes", $"{name} — uncommitted changes",
                $"What changed in {rel} between its last committed version and the file on disk.", JsonMime));
        }
        return list;
    }

    public IReadOnlyList<McpResourceTemplate> Templates() => new[]
    {
        new McpResourceTemplate(DefinitionPrefix + "{+path}/recipe@{+version}", "Recipe at a version", null,
            "The recipe of a definition (path relative to the project root) at a version: a version label like V012, " +
            "a sha, a tag, a branch or \"working\".", JsonMime),
        new McpResourceTemplate(DefinitionPrefix + "{+path}/changes@{+from}..{+to}", "Changes between two versions", null,
            "What changed in a definition between two versions (labels, shas, tags, branches or \"working\"), " +
            "exactly as the panel's history drawer lists it.", JsonMime),
        new McpResourceTemplate(DefinitionPrefix + "{+path}/record", "Decision record", null,
            "The record of decisions for a definition: every version in order with its reasons, agents, tags and toolchain pins.",
            MarkdownMime),
    };

    public ResourceContents? Read(string uri, CancellationToken cancellation)
    {
        if (!uri.StartsWith(Scheme, StringComparison.Ordinal)) return null;
        var live = _live()
            ?? throw new ToolArgumentException("No active Grasshopper document, so there is no project to read resources from.");

        switch (uri)
        {
            case Scheme + "status": return Json(uri, MemoryTools.Status(null, live));
            case Scheme + "branches": return Json(uri, RecordTools.Branches(null, live));
            case Scheme + "tags": return Json(uri, RecordTools.Tags(null, TagCount, live));
        }

        if (!uri.StartsWith(DefinitionPrefix, StringComparison.Ordinal)) throw UnknownShape(uri);
        var rest = uri[DefinitionPrefix.Length..];
        // A version can hold slashes of its own (experiment/mcp, origin/main), so the path ends
        // at the last slash before the first '@'; List() escapes an '@' in a file name as %40.
        var at = rest.IndexOf('@');
        var head = at < 0 ? rest : rest[..at];
        var slash = head.LastIndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) throw UnknownShape(uri);
        var path = Uri.UnescapeDataString(rest[..slash]);
        var kind = Uri.UnescapeDataString(head[(slash + 1)..]);
        var spec = at < 0 ? null : Uri.UnescapeDataString(rest[(at + 1)..]);
        if (spec is not null && spec.Length == 0) throw UnknownShape(uri);

        switch (kind)
        {
            case "record" when spec is null:
                return new ResourceContents(uri, MarkdownMime,
                    RecordTools.DecisionRecordMarkdown(ProjectLocator.Locate(path, live), live, RecordVersions, includeChanges: false, newestFirst: false));
            case "recipe":
            {
                var f = ProjectLocator.Locate(path, live);
                var version = VersionRef.Resolve(f, spec, VersionRef.Working);
                return new ResourceContents(uri, JsonMime, VersionRef.LoadRecipe(f, version).Json);
            }
            case "changes":
            {
                string? from = spec, to = null;
                var range = spec?.IndexOf("..", StringComparison.Ordinal) ?? -1;
                if (range >= 0)
                {
                    from = spec![..range];
                    to = spec[(range + 2)..];
                    if (from.Length == 0 || to.Length == 0) throw UnknownShape(uri);
                }
                return Json(uri, VersionTools.Diff(path, from, to, DiffItems, live));
            }
            default:
                throw UnknownShape(uri);
        }
    }

    private static ResourceContents Json(string uri, ToolResult result) =>
        new(uri, JsonMime, result.Content[0].Text!);

    private static ToolArgumentException UnknownShape(string uri) =>
        new($"\"{uri}\" is not a G-Loom resource. The shapes are: {string.Join(", ", Shapes)} " +
            "(<path> is a .gh path relative to the project root; a version is a label like V012, a sha, a tag, a branch or \"working\").");
}
