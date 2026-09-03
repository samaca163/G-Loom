using System;
using System.Collections.Generic;
using System.Linq;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Memory;
using GLoom.Vcs;

namespace GLoom.Mcp.Tools.Live;

/// <summary>
/// The live-read tools: what the open canvases know that the recipe on disk does not -
/// runtime messages, solve state, the data on outputs, the host itself. Every call reaches
/// Grasshopper through an <see cref="ILiveHost"/>, which is the only place Grasshopper types
/// appear; the shaping here is pure so the tests can drive it with a fake. The catalogue and
/// screenshot tools live in <see cref="CatalogueTools"/>.
/// </summary>
public static class LiveTools
{
    private const int DefaultPage = 50, MaxPage = 200;
    private const int DefaultPreview = 3, MaxPreview = 20;
    private const int ProblemsCap = 100;
    private const int DefaultItems = 100, MaxItems = 2000;
    private const int DefaultTextLength = 200, MaxTextLength = 4000;
    private const int DefaultSolveSeconds = 120, MinSolveSeconds = 5, MaxSolveSeconds = 600;

    internal const string OpenFileArgDescription =
        "Path to an OPEN .gh definition: absolute, or relative to the active document's project root. " +
        "Omit to use the active Grasshopper document.";

    internal const string UiThreadBusy =
        "Rhino's UI thread did not respond within 30 s: a modal dialog or a long solve is holding it. " +
        "Dismiss the dialog or wait for the solve, then retry.";

    internal const string SolveTimedOut =
        "Rhino's UI thread did not respond within the timeout: a modal dialog or a long solve is holding it. " +
        "The solve may still be running, and every live tool waits for it; after a pause, gloom_documents " +
        "shows the document's solutionState and error count, or call gloom_solve again with a larger timeoutSeconds.";

    public static void Register(McpDispatcher d, ILiveHost host)
    {
        d.Register(new McpTool(
            "gloom_documents",
            "Which Grasshopper definitions are open right now, which one is active, and for each whether it " +
            "is saved, enabled, solved (solutionState, solutionMs) and how many objects carry errors or " +
            "warnings. Call it before any other live tool when unsure which document is active. Unsaved " +
            "documents have a null filePath and no project; saved ones report their project root and " +
            "repo-relative definitionPath.",
            Schema.Object().Build(),
            ToolAccess.Read,
            (_, _) => Documents(host)));

        d.Register(new McpTool(
            "gloom_read_document",
            "The LIVE canvas of an open Grasshopper definition, unsaved edits included (gloom_read_version " +
            "reads the recipe on disk instead): every object with its recipe fields (name, nickname, pivot, " +
            "wiring, persistent values) joined with its runtime state (message level and messages, locked, " +
            "hidden, solve phase, processor time, input and output data counts with a preview of each " +
            "output). \"problems\" lists every object at error or warning level up front regardless of paging, " +
            "so check it first. Objects are paged (default 50, max 200) and can be narrowed by query, kind " +
            "and onlyProblems. \"file\" is optional (the active document).",
            Schema.Object()
                .String("file", OpenFileArgDescription)
                .Integer("offset", "Index of the first object to return (default 0).", min: 0)
                .Integer("limit", "Objects per page (default 50, max 200).", min: 1, max: MaxPage)
                .String("query", "Case-insensitive substring matched against each object's name, nickname and instanceGuid.")
                .Enum("kind", ObjectFilter.KindDescription, ObjectFilter.Kinds)
                .Boolean("onlyProblems", "Only objects whose runtime level is warning or error (default false).")
                .Integer("previewItems", "How many data items each output previews (default 3, max 20).", min: 0, max: MaxPreview)
                .Build(),
            ToolAccess.Read,
            (args, _) => ReadDocument(host,
                Args.String(args, "file"), Args.Int(args, "offset", 0), Args.Int(args, "limit", DefaultPage),
                Args.String(args, "query"), Args.String(args, "kind"), Args.Bool(args, "onlyProblems", false),
                Args.Int(args, "previewItems", DefaultPreview))));

        d.Register(new McpTool(
            "gloom_read_outputs",
            "The data flowing out of one object on the live canvas: each output (or a free-floating " +
            "parameter's own data) as a data tree, branch by branch, with every item's type and text. " +
            "Geometry shows its type and bounding box, not coordinates. \"object\" is an instanceGuid or the " +
            "exact name or nickname of an object (ambiguous names are refused with the candidates); \"param\" " +
            "narrows to one output. Items are capped per output (default 100, max 2000) and text per item " +
            "(default 200 characters, max 4000). \"file\" is optional (the active document).",
            Schema.Object()
                .String("file", OpenFileArgDescription)
                .String("object", "An instanceGuid, or the exact name or nickname of an object on the canvas.", required: true)
                .String("param", "One output: its name, nickname or 0-based index. Omit for every output.")
                .Integer("maxItems", "Maximum items per output (default 100, max 2000).", min: 1, max: MaxItems)
                .Integer("maxTextLength", "Maximum characters of each item's text (default 200, max 4000).", min: 1, max: MaxTextLength)
                .Build(),
            ToolAccess.Read,
            (args, _) => ReadOutputs(host,
                Args.String(args, "file"), Args.String(args, "object"), Args.String(args, "param"),
                Args.Int(args, "maxItems", DefaultItems), Args.Int(args, "maxTextLength", DefaultTextLength))));

        d.Register(new McpTool(
            "gloom_solve",
            "Recompute an open Grasshopper definition and report what failed: every object at error or " +
            "warning level with its messages, the solve duration and the resulting solution state. Needs " +
            "read-write agent access. \"expireAll\" false (default) recomputes only objects that are expired, " +
            "which is what Grasshopper does after an edit; true expires and recomputes everything, which is " +
            "slower but starts clean. Only the active definition can be solved: Grasshopper disables the solver " +
            "of background tabs. A long solve can exceed timeoutSeconds (default 120, min 5, max 600); the solve " +
            "then keeps running in Grasshopper, every live tool waits for it, and gloom_documents shows its " +
            "state once it finishes.",
            Schema.Object()
                .String("file", OpenFileArgDescription)
                .Boolean("expireAll", "Recompute everything (true) or only expired objects (false, default).")
                .Integer("timeoutSeconds", "How long to wait for the solve (default 120, min 5, max 600).", min: MinSolveSeconds, max: MaxSolveSeconds)
                .Build(),
            ToolAccess.Write,
            (args, _) => Solve(host,
                Args.String(args, "file"), Args.Bool(args, "expireAll", false),
                Args.Int(args, "timeoutSeconds", DefaultSolveSeconds))));

        d.Register(new McpTool(
            "gloom_rhino_context",
            "The host this endpoint runs in: the Rhino version, whether Rhino runs inside Revit " +
            "(Rhino.Inside.Revit, with its version), the Grasshopper and G-Loom versions, the active Rhino " +
            "model (path, units, absolute tolerance, object and layer counts, unsaved changes) and how many " +
            "Grasshopper definitions are open.",
            Schema.Object().Build(),
            ToolAccess.Read,
            (_, _) => Context(host)));

        CatalogueTools.Register(d, host);
    }

    public static ToolResult Documents(ILiveHost host) => Guard(() =>
    {
        var docs = host.Documents();
        var shaped = docs.Select(d =>
        {
            var root = d.FilePath is null ? null : RepoDiscovery.FindRepoRoot(d.FilePath);
            return new
            {
                filePath = d.FilePath,
                d.DisplayName, d.IsActive, d.IsModified, d.Enabled, d.ObjectCount,
                d.SolutionState, d.SolutionMs, d.ErrorCount, d.WarningCount,
                projectRoot = root,
                definitionPath = root is null || d.FilePath is null ? null : ProjectLocator.Rel(root, d.FilePath),
            };
        }).ToList();

        return ToolResult.Json(new
        {
            documents = shaped,
            active = docs.FirstOrDefault(d => d.IsActive)?.FilePath,
            note = docs.Count == 0
                ? "No Grasshopper document is open; the live tools need one."
                : "Pass a document's filePath as \"file\" to the other live tools; unsaved documents (null filePath) " +
                  "are only reachable while active. A null projectRoot means the file is outside any G-Loom project.",
        });
    });

    public static ToolResult ReadDocument(
        ILiveHost host, string? file, int offset, int limit, string? query, string? kind, bool onlyProblems, int previewItems)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxPage);
        previewItems = Math.Clamp(previewItems, 0, MaxPreview);
        var q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var k = ObjectFilter.ValidateKind(kind);

        return Guard(() =>
        {
            var live = host.ReadDocument(file, previewItems);
            var runtime = live.Runtime.ToDictionary(r => r.InstanceGuid, StringComparer.OrdinalIgnoreCase);
            var joined = live.Structure.Objects
                .Select(o => (Object: o, Runtime: runtime.GetValueOrDefault(o.InstanceGuid)))
                .ToList();

            var problems = joined.Where(j => j.Runtime is not null && IsError(j.Runtime))
                .Concat(joined.Where(j => j.Runtime is not null && IsWarning(j.Runtime)))
                .Select(j => new
                {
                    j.Object.InstanceGuid, j.Object.Name, j.Object.Nickname,
                    level = j.Runtime!.Level, messages = j.Runtime.Messages.Select(m => m.Text).ToList(),
                })
                .ToList();

            var matched = joined
                .Where(j => q is null || ObjectFilter.Matches(j.Object, q))
                .Where(j => k is null || string.Equals(j.Object.Kind, k, StringComparison.OrdinalIgnoreCase))
                .Where(j => !onlyProblems || (j.Runtime is not null && (IsError(j.Runtime) || IsWarning(j.Runtime))))
                .ToList();
            var page = matched.Skip(offset).Take(limit).ToList();
            var hasMore = offset + page.Count < matched.Count;
            var info = live.Info;

            return ToolResult.Json(new
            {
                file = info.FilePath,
                info.DisplayName, info.IsActive, info.IsModified, info.Enabled, info.SolutionState, info.SolutionMs,
                totals = new
                {
                    objects = live.Structure.Objects.Count,
                    groups = live.Structure.Groups.Count,
                    byKind = VersionTools.CountByKind(live.Structure),
                    errors = live.Runtime.Count(IsError),
                    warnings = live.Runtime.Count(IsWarning),
                    remarks = live.Runtime.Count(r => Is(r.Level, "remark")),
                },
                problems = problems.Take(ProblemsCap).ToList(),
                problemsTruncated = problems.Count > ProblemsCap,
                filter = q is null && k is null && !onlyProblems ? null
                    : new { query = q, kind = k, onlyProblems, matched = matched.Count },
                page = new { offset, limit, returned = page.Count, hasMore, nextOffset = hasMore ? offset + page.Count : (int?)null },
                objects = page.Select(j => new
                {
                    j.Object.InstanceGuid, j.Object.ComponentGuid, j.Object.Kind, j.Object.Name, j.Object.Nickname,
                    j.Object.Pivot, j.Object.Inputs, j.Object.Outputs, j.Object.Persistent, j.Object.Bounds,
                    runtime = j.Runtime is null ? null : new
                    {
                        j.Runtime.Level, j.Runtime.Messages, j.Runtime.Locked, j.Runtime.Hidden, j.Runtime.Phase,
                        j.Runtime.ProcessorMs, j.Runtime.Inputs, j.Runtime.Outputs, j.Runtime.IsCluster, j.Runtime.Library,
                    },
                }).ToList(),
                groups = live.Structure.Groups,
                note = "This is the live canvas, unsaved edits included; gloom_read_version reads the recipe on disk. " +
                       "\"problems\" holds every object at error or warning level whatever the page; " +
                       "gloom_read_outputs reads the full data behind an output preview." +
                       (problems.Count > ProblemsCap ? $" Only the first {ProblemsCap} problems are listed; totals hold the full counts." : ""),
            });
        });
    }

    public static ToolResult ReadOutputs(ILiveHost host, string? file, string? objectRef, string? param, int maxItems, int maxTextLength)
    {
        if (string.IsNullOrWhiteSpace(objectRef))
            throw new ToolArgumentException("\"object\" is required: an instanceGuid, or the exact name or nickname of an object on the canvas.");
        maxItems = Math.Clamp(maxItems, 1, MaxItems);
        maxTextLength = Math.Clamp(maxTextLength, 1, MaxTextLength);
        var p = string.IsNullOrWhiteSpace(param) ? null : param.Trim();

        return Guard(() =>
        {
            var r = host.ReadOutputs(file, objectRef.Trim(), p, maxItems, maxTextLength);
            return ToolResult.Json(new
            {
                file,
                @object = r.Object,
                @params = r.Params.Select(o => new
                {
                    o.InstanceGuid, o.Name, o.Nickname, o.TypeName, o.DataCount, o.PathCount, o.Truncated,
                    branches = o.Branches.Select(b => new
                    {
                        b.Path, b.Count,
                        items = b.Items.Select(i => new { type = i.TypeName, text = i.Text, boundsMin = i.BoundsMin, boundsMax = i.BoundsMax }).ToList(),
                    }).ToList(),
                }).ToList(),
                note = "Each output is a data tree: a path such as {0;1} names one branch and its items are listed in " +
                       "order. Geometry items show a type and a bounding box (boundsMin/boundsMax), not coordinates; " +
                       "text is cut at maxTextLength. truncated=true on an output means it holds more than maxItems " +
                       "items: raise maxItems or pass \"param\" to read one output at a time." +
                       (r.Params.Any(o => o.Truncated) ? " Some outputs were truncated." : ""),
            });
        });
    }

    public static ToolResult Solve(ILiveHost host, string? file, bool expireAll, int timeoutSeconds)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, MinSolveSeconds, MaxSolveSeconds);
        return Guard(() =>
        {
            var r = host.Solve(file, expireAll, TimeSpan.FromSeconds(timeoutSeconds));
            return ToolResult.Json(new
            {
                file,
                r.Ran, r.SolverLocked, r.ExpiredAll, r.DurationMs, r.SolutionState, r.ObjectCount,
                errors = r.Errors.Select(Problem).ToList(),
                warnings = r.Warnings.Select(Problem).ToList(),
                counts = new { errors = r.Errors.Count, warnings = r.Warnings.Count },
                note = r.SolverLocked
                    ? "Nothing ran: the solver is locked in Grasshopper (Solution > Lock Solver); unlock it there and call gloom_solve again."
                    : r.Errors.Count > 0
                        ? "Some objects failed; gloom_read_document with onlyProblems=true shows them with their wiring, " +
                          "gloom_read_outputs shows the data reaching them."
                        : "The definition solved without errors.",
            });
        }, SolveTimedOut);
    }

    public static ToolResult Context(ILiveHost host) => Guard(() =>
    {
        var c = host.Context();
        return ToolResult.Json(new
        {
            c.RhinoVersion, c.HostProcess, c.InsideRevit, c.RhinoInsideRevitVersion, c.GrasshopperVersion, c.GloomVersion,
            c.ActiveModel, c.OpenDefinitions, c.ActiveDefinition,
            note = c.InsideRevit
                ? "Rhino runs inside Revit through Rhino.Inside.Revit: the Revit model is the host document and Grasshopper solves under Revit's UI thread."
                : "gloom_documents lists the open definitions; gloom_status places the active one in its project's history.",
        });
    });

    /// <summary>A UI-thread timeout is a condition the agent can act on (dismiss the dialog, wait),
    /// so it becomes a tool error with instructions instead of the dispatcher's generic failure.</summary>
    internal static ToolResult Guard(Func<ToolResult> run, string timeoutMessage = UiThreadBusy)
    {
        try { return run(); }
        catch (TimeoutException) { return ToolResult.Error(timeoutMessage); }
    }

    private static object Problem(ObjectProblem p) =>
        new { p.InstanceGuid, p.Name, p.Nickname, messages = p.Messages.Select(m => m.Text).ToList() };

    private static bool IsError(RuntimeObject r) => Is(r.Level, "error");
    private static bool IsWarning(RuntimeObject r) => Is(r.Level, "warning");
    private static bool Is(string level, string name) => string.Equals(level, name, StringComparison.OrdinalIgnoreCase);
}
