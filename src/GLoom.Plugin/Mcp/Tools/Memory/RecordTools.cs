using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GLoom.Mcp.Protocol;
using GLoom.Serialization;
using GLoom.Vcs;

namespace GLoom.Mcp.Tools.Memory;

/// <summary>
/// The record-of-decisions tools: the project's system options, its milestones with their
/// toolchain pins, and the whole record for one definition as a document an agent reads top
/// to bottom. Host-free; the running toolchain arrives as a delegate because capturing it
/// needs Rhino.
/// </summary>
public static class RecordTools
{
    private const int DecisionRecordChangesCap = 50, MaxPins = 50;

    public static void Register(McpDispatcher d, Func<LiveSnapshot?> live, Func<Toolchain?> runningToolchain)
    {
        d.Register(new McpTool(
            "gloom_branches",
            "The project's system options. Every branch is a substitutable design strategy (an envelope option, " +
            "a structural scheme, a product variant, a tool feature), not a detour: which option is current, " +
            "what it branched from, the remote it tracks (ahead/behind, read locally without network), and the " +
            "last version of this definition on each option. Switching options happens in the G-Loom panel.",
            Schema.Object().String("file", ProjectLocator.FileArgDescription).Build(),
            ToolAccess.Read,
            (args, _) => Branches(Args.String(args, "file"), live())));

        d.Register(new McpTool(
            "gloom_tags",
            "The project's milestones (tags), newest first, with the toolchain each was made on: Rhino, " +
            "Grasshopper, Rhino.Inside.Revit and G-Loom versions pinned at tag time, the tagger's notes, and the " +
            "AEC / product / release metadata the panel recorded. Each tag also says which version of this " +
            "definition it captures and whether it sits on a commit that changed this definition.",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription)
                .Integer("limit", "Maximum number of tags to return (default 50, max 200).", min: 1, max: 200)
                .Build(),
            ToolAccess.Read,
            (args, _) => Tags(Args.String(args, "file"), Args.Int(args, "limit", 50), live())));

        d.Register(new McpTool(
            "gloom_toolchain",
            "The versions of Rhino, Grasshopper, Rhino.Inside.Revit and G-Loom a milestone was made with (the pin " +
            "on its tag) and the versions running now. A recipe is only reproducible on the toolchain it was made " +
            "with: another Rhino or plug-in version can solve it differently or not at all, and decade-scale " +
            "audits depend on the pin to rebuild a deliverable exactly. Pass \"tag\" to compare one milestone's pin " +
            "against the running toolchain; omit it to list the pins on this definition's history.",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription)
                .String("tag", "A tag name. Omit to list every pinned tag on this definition's recent history.")
                .Build(),
            ToolAccess.Read,
            (args, _) => ToolchainInfo(Args.String(args, "file"), Args.String(args, "tag"), live(), runningToolchain())));

        d.Register(new McpTool(
            "gloom_decision_record",
            "The record of decisions for one definition as a single markdown document: every version in order " +
            "with its label, subject, author, date, description, agent provenance, milestone tags with their " +
            "toolchain pins, and where the current system option branched off. Read it to understand why a " +
            "definition is the way it is before changing it. includeChanges adds each version's recipe changes " +
            "against the previous version (capped at 50 versions, because each one costs a read of the recipe).",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription)
                .Integer("limit", "Maximum number of versions (default 50, max 200; 50 when includeChanges is true).", min: 1, max: 200)
                .Boolean("includeChanges", "Add each version's recipe changes against the previous version (default false).")
                .Boolean("newestFirst", "Newest version first instead of the oldest-to-newest narrative (default false).")
                .Build(),
            ToolAccess.Read,
            (args, _) => DecisionRecord(
                Args.String(args, "file"), Args.Int(args, "limit", 50),
                Args.Bool(args, "includeChanges", false), Args.Bool(args, "newestFirst", false), live())));
    }

    public sealed record VersionStamp(string Sha, string ShortSha, string? VersionLabel, string Subject, DateTimeOffset When);
    public sealed record ToolchainDifference(string Component, string? Pinned, string? Running);

    public static ToolResult Branches(string? file, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        var files = f.Files;
        var current = GLoomRepository.GetStatus(f.RepoRoot, files).Branch;
        var branches = GLoomRepository.GetBranches(f.RepoRoot);
        var defaultBranch = GLoomRepository.GetDefaultBranch(f.RepoRoot);
        var upstream = GLoomRepository.GetUpstream(f.RepoRoot, current);
        var aheadBehind = GLoomRepository.GetAheadBehind(f.RepoRoot, current, upstream);
        var forks = GLoomRepository.GetForkPoints(f.RepoRoot, current, branches);

        return ToolResult.Json(new
        {
            projectRoot = f.RepoRoot,
            definitionPath = f.GhRel,
            current,
            defaultBranch,
            upstream = upstream is null ? null : new
            {
                remote = upstream.Remote,
                branch = upstream.RemoteBranch,
                ahead = aheadBehind.Ahead,
                behind = aheadBehind.Behind,
            },
            remotes = GLoomRepository.GetRemotes(f.RepoRoot).Select(r => new { name = r.Name, url = r.FetchUrl }).ToList(),
            branchedFrom = forks.Select(fp =>
            {
                var c = VersionRef.CommitAt(f.RepoRoot, fp.ForkSha);
                return new
                {
                    branch = fp.ParentBranch,
                    sha = fp.ForkSha,
                    shortSha = VersionRef.Short(fp.ForkSha),
                    subject = c?.Message,
                    versionLabel = c is null ? null : CommitVersioning.ExtractVersionLabel(c),
                };
            }).ToList(),
            branches = branches.Select(b =>
            {
                var last = GLoomRepository.Log(f.RepoRoot, 1, files, startingAt: b.Name).FirstOrDefault();
                return new
                {
                    name = b.Name,
                    isCurrent = b.IsCurrent,
                    isDefault = b.Name == defaultBranch,
                    lastVersion = last is null ? null : Stamp(last),
                };
            }).ToList(),
            note = "Branches are system options: substitutable design strategies, each with its own version history " +
                   "of this definition. A branch without a lastVersion never touched this definition. Switching " +
                   "between options happens in the G-Loom panel.",
        });
    }

    public static ToolResult Tags(string? file, int limit, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        limit = Math.Clamp(limit, 1, 200);
        var files = f.Files;
        var all = GLoomRepository.GetTags(f.RepoRoot)
            .Select(t =>
            {
                var meta = TagMetadataJson.TryRead(t.Message);
                return (Tag: t, Meta: meta, When: meta?.CreatedAt ?? t.When ?? DateTimeOffset.MinValue);
            })
            .OrderByDescending(x => x.When)
            .ThenBy(x => x.Tag.Name, StringComparer.Ordinal)
            .ToList();
        var page = all.Take(limit).ToList();

        return ToolResult.Json(new
        {
            projectRoot = f.RepoRoot,
            definitionPath = f.GhRel,
            returned = page.Count,
            truncated = all.Count > limit,
            tags = page.Select(x =>
            {
                var definitionVersion = GLoomRepository.Log(f.RepoRoot, 1, files, startingAt: x.Tag.Sha).FirstOrDefault();
                var isOnThisDefinition = definitionVersion is not null && definitionVersion.Sha == x.Tag.Sha;
                var commit = isOnThisDefinition ? definitionVersion : VersionRef.CommitAt(f.RepoRoot, x.Tag.Sha);
                return new
                {
                    name = x.Tag.Name,
                    sha = x.Tag.Sha,
                    shortSha = VersionRef.Short(x.Tag.Sha),
                    isAnnotated = x.Tag.IsAnnotated,
                    createdAt = x.Meta?.CreatedAt,
                    createdBy = x.Meta?.CreatedBy,
                    notes = x.Meta?.Notes,
                    toolchain = x.Meta?.Toolchain,
                    aec = x.Meta?.Aec,
                    product = x.Meta?.Product,
                    release = x.Meta?.Release,
                    commit = commit is null ? null : new
                    {
                        subject = commit.Message,
                        versionLabel = CommitVersioning.ExtractVersionLabel(commit),
                        when = commit.When,
                    },
                    definitionVersion = definitionVersion is null ? null : Stamp(definitionVersion),
                    isOnThisDefinition,
                };
            }).ToList(),
            note = page.Count == 0
                ? "No tags in this project yet; milestones are tagged from the commit drawer in the G-Loom panel."
                : "A tag with a toolchain was made from the G-Loom panel; a null toolchain means a lightweight tag or " +
                  "one made elsewhere. definitionVersion is the last version of this definition the tag can see; " +
                  "isOnThisDefinition is false when the tagged commit changed other files only.",
        });
    }

    public static ToolResult ToolchainInfo(string? file, string? tag, LiveSnapshot? live, Toolchain? running)
    {
        var f = ProjectLocator.Locate(file, live);
        var tags = GLoomRepository.GetTags(f.RepoRoot);
        var runningNote = running is null
            ? "The running toolchain is only known inside Rhino; this endpoint is not running there, so nothing was compared."
            : null;

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var name = tag.Trim();
            var t = tags.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal))
                ?? throw new ToolArgumentException($"No tag named \"{name}\" in this project; gloom_tags lists the ones that exist.");
            var meta = TagMetadataJson.TryRead(t.Message)
                ?? throw new ToolArgumentException(
                    $"Tag \"{name}\" carries no toolchain metadata: it is a lightweight tag or was made outside the " +
                    "G-Loom panel, so the toolchain it was made with was never recorded.");
            var differences = running is null ? null : Differences(meta.Toolchain, running);

            return ToolResult.Json(new
            {
                projectRoot = f.RepoRoot,
                definitionPath = f.GhRel,
                tag = new { name = t.Name, sha = t.Sha, shortSha = VersionRef.Short(t.Sha) },
                pinned = meta.Toolchain,
                running,
                differences,
                matches = differences is null ? (bool?)null : differences.Count == 0,
                note = runningNote,
            });
        }

        var files = f.Files;
        var window = GLoomRepository.Log(f.RepoRoot, 200, files).ToDictionary(c => c.Sha, StringComparer.Ordinal);
        var pins = new List<object>();
        var truncated = false;
        foreach (var (t, meta) in tags
            .Select(t => (Tag: t, Meta: TagMetadataJson.TryRead(t.Message)))
            .Where(x => x.Meta is not null)
            .OrderByDescending(x => x.Meta!.CreatedAt)
            .ThenBy(x => x.Tag.Name, StringComparer.Ordinal))
        {
            // A milestone is usually tagged at HEAD, often another definition's commit; its pin
            // still covers the version of this definition HEAD held, so the tag maps to that one.
            var version = window.TryGetValue(t.Sha, out var own)
                ? own
                : GLoomRepository.Log(f.RepoRoot, 1, files, startingAt: t.Sha).FirstOrDefault();
            if (version is null || !window.ContainsKey(version.Sha)) continue;
            if (pins.Count == MaxPins)
            {
                truncated = true;
                break;
            }
            pins.Add(new
            {
                tag = t.Name,
                sha = t.Sha,
                shortSha = VersionRef.Short(t.Sha),
                createdAt = meta!.CreatedAt,
                toolchain = meta.Toolchain,
                definitionVersion = Stamp(version),
                isOnThisDefinition = version.Sha == t.Sha,
            });
        }

        return ToolResult.Json(new
        {
            projectRoot = f.RepoRoot,
            definitionPath = f.GhRel,
            running,
            returned = pins.Count,
            truncated,
            pins,
            note = Join(runningNote,
                "Pins cover tags with toolchain metadata whose commit is, or sees as its latest, one of the last 200 " +
                "versions of this definition on the current system option; definitionVersion is that version, and " +
                "isOnThisDefinition is false when the tagged commit changed other files only. gloom_tags lists every " +
                "tag in the project."),
        });
    }

    public static ToolResult DecisionRecord(string? file, int limit, bool includeChanges, bool newestFirst, LiveSnapshot? live) =>
        ToolResult.Text(DecisionRecordMarkdown(ProjectLocator.Locate(file, live), live, limit, includeChanges, newestFirst));

    public static string DecisionRecordMarkdown(LocatedFile f, LiveSnapshot? live, int limit, bool includeChanges, bool newestFirst)
    {
        limit = Math.Clamp(limit, 1, includeChanges ? DecisionRecordChangesCap : 200);
        var files = f.Files;
        var fetched = GLoomRepository.Log(f.RepoRoot, limit + 1, files);
        var truncated = fetched.Count > limit;
        var window = fetched.Take(limit).Reverse().ToList();
        var total = GLoomRepository.CountCommitsTouching(f.RepoRoot, files);
        var branch = GLoomRepository.GetStatus(f.RepoRoot, files).Branch;
        var currentSha = CurrentSha(f, live);
        var tags = GLoomRepository.GetTags(f.RepoRoot).ToLookup(t => t.Sha, StringComparer.Ordinal);
        var forks = GLoomRepository.GetForkPoints(f.RepoRoot, branch).ToLookup(fp => fp.ForkSha, fp => fp.ParentBranch, StringComparer.Ordinal);

        var head = new StringBuilder();
        head.Append("# ").Append(f.BaseName).Append(" — record of decisions\n\n");
        head.Append(f.GhRel).Append(" · project ").Append(new DirectoryInfo(f.RepoRoot).Name)
            .Append(" · branch ").Append(branch).Append(" · ").Append(total).Append(total == 1 ? " version" : " versions");
        if (truncated) head.Append(" (showing the latest ").Append(window.Count).Append(')');

        var sections = new List<string>();
        var previousDoc = includeChanges && truncated ? RecipeAt(f, fetched[limit].Sha) : null;
        var hasPrevious = truncated;
        foreach (var c in window)
        {
            var paragraphs = new List<string> { Header(c, c.Sha == currentSha), ChangeNarrative.MetaLine(c) };
            var text = CommitTrailers.Parse(c.Body).Text;
            if (text.Length > 0) paragraphs.Add(text);

            var facts = new List<string>();
            foreach (var t in tags[c.Sha].OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var meta = TagMetadataJson.TryRead(t.Message);
                facts.Add(meta is null ? $"Tagged: {t.Name}" : $"Tagged: {t.Name} — {ToolchainLine(meta.Toolchain)}");
                if (!string.IsNullOrWhiteSpace(meta?.Notes)) facts.Add($"Notes: {meta!.Notes.Trim()}");
            }
            if (forks.Contains(c.Sha)) facts.Add("↰ branched from " + string.Join(" · ", forks[c.Sha]));
            if (facts.Count > 0) paragraphs.Add(string.Join("\n", facts));

            if (includeChanges)
            {
                var doc = RecipeAt(f, c.Sha);
                paragraphs.Add(ChangesText(doc, previousDoc, hasPrevious));
                previousDoc = doc;
                hasPrevious = true;
            }
            sections.Add(string.Join("\n\n", paragraphs));
        }
        if (newestFirst) sections.Reverse();
        if (sections.Count == 0) sections.Add("No versions of this definition have been committed yet.");

        return head.Append("\n\n").Append(string.Join("\n\n", sections)).Append('\n').ToString();
    }

    private static string Header(GLoomRepository.CommitInfo c, bool isCurrent) =>
        $"## {CommitVersioning.ExtractVersionLabel(c) ?? VersionRef.Short(c.Sha)} — {c.Message}{(isCurrent ? " (current)" : "")}";

    private static string ChangesText(CanonicalDocument? doc, CanonicalDocument? previous, bool hasPrevious)
    {
        if (doc is null) return "Recipe unavailable at this version.";
        if (!hasPrevious) return $"First version: {doc.Objects.Count} objects, {doc.Groups.Count} groups.";
        if (previous is null) return $"Recipe unavailable at the previous version; this one holds {doc.Objects.Count} objects, {doc.Groups.Count} groups.";
        var diff = DocumentDiff.Compute(previous, doc);
        return diff.IsEmpty
            ? "Changes (0): the recipe is unchanged; only the .gh file differs."
            : $"Changes ({diff.TotalChanges}):\n{DiffSummaryText.Body(diff)}";
    }

    private static CanonicalDocument? RecipeAt(LocatedFile f, string sha) =>
        CanonicalJson.TryParse(GLoomRepository.ReadFileAtCommit(f.RepoRoot, sha, f.JsonRel));

    private static string ToolchainLine(Toolchain tc)
    {
        var parts = new List<string> { $"Rhino {tc.Rhino}", $"GH {tc.Grasshopper}" };
        if (!string.IsNullOrEmpty(tc.RhinoInsideRevit)) parts.Add($"RiR {tc.RhinoInsideRevit}");
        parts.Add($"G-Loom {tc.Gloom}");
        return string.Join(" · ", parts);
    }

    private static List<ToolchainDifference> Differences(Toolchain pinned, Toolchain running)
    {
        var list = new List<ToolchainDifference>();
        void Compare(string component, string? p, string? r)
        {
            if (!string.Equals(p ?? "", r ?? "", StringComparison.Ordinal)) list.Add(new ToolchainDifference(component, p, r));
        }
        Compare("Rhino", pinned.Rhino, running.Rhino);
        Compare("Grasshopper", pinned.Grasshopper, running.Grasshopper);
        Compare("RhinoInsideRevit", pinned.RhinoInsideRevit, running.RhinoInsideRevit);
        Compare("G-Loom", pinned.Gloom, running.Gloom);
        return list;
    }

    private static string? CurrentSha(LocatedFile f, LiveSnapshot? live) =>
        f.IsActiveDocument && live is not null ? live.CurrentSha
        : f.Exists ? GLoomRepository.FindCommitMatchingWorkingTree(f.RepoRoot, f.GhRel, f.JsonRel)
        : null;

    private static VersionStamp Stamp(GLoomRepository.CommitInfo c) =>
        new(c.Sha, VersionRef.Short(c.Sha), CommitVersioning.ExtractVersionLabel(c), c.Message, c.When);

    private static string Join(string? a, string b) => a is null ? b : a + " " + b;
}
