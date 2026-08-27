using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Memory;
using GLoom.Serialization;
using GLoom.Vcs;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class RecordToolsTests
{
    private const string Gh = "Coding/tower.gh";
    private const string Json = "Coding/tower.gloom.json";
    private const string OtherGh = "Coding/other.gh";
    private const string OtherJson = "Coding/other.gloom.json";

    private static readonly Toolchain Pinned = new("8.19", "1.0.0.8", null, "0.3.0");

    private static string Commit(GitRepo repo, decimal slider, string subject, string body,
        string gh = Gh, string json = Json)
    {
        var ghPath = repo.Write(gh, $"gh bytes {subject}");
        var name = Path.GetFileNameWithoutExtension(gh);
        GLoomRepository.StageForCommit(repo.Root, CanonicalJson.Write(Doc(name, Slider(Guid(1), slider))), repo.Full(json), ghPath);
        return GLoomRepository.CommitStaged(repo.Root, subject, body, "Tester", "t@example.com")!;
    }

    private static void AnnotatedTag(GitRepo repo, string name, string sha, DateTimeOffset createdAt, string? notes = null)
    {
        var meta = new TagMetadata(2, name, sha, createdAt, "Tester", Pinned, Notes: notes);
        GLoomRepository.CreateAnnotatedTag(repo.Root, name, sha, TagMetadataJson.Write(meta), "Tester", "t@example.com");
    }

    private static JsonObject Structured(ToolResult r) => (JsonObject)r.StructuredContent!;

    private static string Str(JsonNode? n) => n!.GetValue<string>();

    [Fact]
    public void Branches_report_the_fork_point_the_default_and_the_last_version_per_option()
    {
        using var repo = GitRepo.Init();
        var sha1 = Commit(repo, 5, "First massing", "Gloom-Version: tower_V001");
        GitRepo.Git(repo.Root, "checkout", "-q", "-b", "envelope-mullion");
        var sha2 = Commit(repo, 9, "Mullion option", "Gloom-Version: tower_V002");

        var r = Structured(RecordTools.Branches(repo.Full(Gh), null));

        Assert.Equal("envelope-mullion", Str(r["current"]));
        Assert.Equal("main", Str(r["defaultBranch"]));
        Assert.Null(r["upstream"]);
        Assert.Empty((JsonArray)r["remotes"]!);

        var fork = Assert.Single((JsonArray)r["branchedFrom"]!);
        Assert.Equal("main", Str(fork!["branch"]));
        Assert.Equal(sha1, Str(fork["sha"]));
        Assert.Equal("V001", Str(fork["versionLabel"]));
        Assert.Equal("First massing", Str(fork["subject"]));

        var branches = ((JsonArray)r["branches"]!).ToDictionary(b => Str(b!["name"]), b => (JsonObject)b!);
        Assert.True(branches["main"]["isDefault"]!.GetValue<bool>());
        Assert.False(branches["main"]["isCurrent"]!.GetValue<bool>());
        Assert.Equal(sha1, Str(branches["main"]["lastVersion"]!["sha"]));
        Assert.True(branches["envelope-mullion"]["isCurrent"]!.GetValue<bool>());
        Assert.False(branches["envelope-mullion"]["isDefault"]!.GetValue<bool>());
        Assert.Equal(sha2, Str(branches["envelope-mullion"]["lastVersion"]!["sha"]));
        Assert.Equal("V002", Str(branches["envelope-mullion"]["lastVersion"]!["versionLabel"]));
        Assert.Contains("system options", Str(r["note"]));
    }

    [Fact]
    public void Tags_parse_toolchains_resolve_the_definition_version_and_sort_newest_first()
    {
        using var repo = GitRepo.Init();
        var sha1 = Commit(repo, 5, "First", "Gloom-Version: tower_V001");
        var sha2 = Commit(repo, 9, "Second", "Gloom-Version: tower_V002");
        var sha3 = Commit(repo, 1, "Other definition", "", OtherGh, OtherJson);
        AnnotatedTag(repo, "milestone-1", sha1, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), notes: "DD submittal");
        GitRepo.Git(repo.Root, "tag", "lightweight", sha2);
        AnnotatedTag(repo, "old-other", sha3, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var r = Structured(RecordTools.Tags(repo.Full(Gh), 50, null));

        Assert.Equal(3, r["returned"]!.GetValue<int>());
        Assert.False(r["truncated"]!.GetValue<bool>());
        var tags = (JsonArray)r["tags"]!;
        Assert.Equal(new[] { "milestone-1", "lightweight", "old-other" }, tags.Select(t => Str(t!["name"])).ToArray());

        var milestone = (JsonObject)tags[0]!;
        Assert.True(milestone["isAnnotated"]!.GetValue<bool>());
        Assert.Equal(sha1, Str(milestone["sha"]));
        Assert.Equal("8.19", Str(milestone["toolchain"]!["rhino"]));
        Assert.Equal("1.0.0.8", Str(milestone["toolchain"]!["grasshopper"]));
        Assert.Null(milestone["toolchain"]!["rhinoInsideRevit"]);
        Assert.Equal("0.3.0", Str(milestone["toolchain"]!["gloom"]));
        Assert.Equal("DD submittal", Str(milestone["notes"]));
        Assert.Equal("Tester", Str(milestone["createdBy"]));
        Assert.Equal("V001", Str(milestone["commit"]!["versionLabel"]));
        Assert.Equal(sha1, Str(milestone["definitionVersion"]!["sha"]));
        Assert.True(milestone["isOnThisDefinition"]!.GetValue<bool>());

        var lightweight = (JsonObject)tags[1]!;
        Assert.False(lightweight["isAnnotated"]!.GetValue<bool>());
        Assert.Null(lightweight["toolchain"]);
        Assert.Null(lightweight["createdAt"]);
        Assert.Equal("Second", Str(lightweight["commit"]!["subject"]));
        Assert.Equal(sha2, Str(lightweight["definitionVersion"]!["sha"]));
        Assert.True(lightweight["isOnThisDefinition"]!.GetValue<bool>());

        var other = (JsonObject)tags[2]!;
        Assert.Equal(sha3, Str(other["sha"]));
        Assert.Equal(sha2, Str(other["definitionVersion"]!["sha"]));
        Assert.False(other["isOnThisDefinition"]!.GetValue<bool>());

        var page = Structured(RecordTools.Tags(repo.Full(Gh), 2, null));
        Assert.Equal(2, page["returned"]!.GetValue<int>());
        Assert.True(page["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Toolchain_compares_a_pinned_tag_against_the_running_toolchain()
    {
        using var repo = GitRepo.Init();
        var sha1 = Commit(repo, 5, "First", "Gloom-Version: tower_V001");
        AnnotatedTag(repo, "milestone-1", sha1, DateTimeOffset.Now);
        var running = new Toolchain("8.20", "1.0.0.8", null, "0.3.0");

        var r = Structured(RecordTools.ToolchainInfo(repo.Full(Gh), "milestone-1", null, running));

        Assert.Equal("milestone-1", Str(r["tag"]!["name"]));
        Assert.Equal(sha1, Str(r["tag"]!["sha"]));
        Assert.Equal("8.19", Str(r["pinned"]!["rhino"]));
        Assert.Equal("8.20", Str(r["running"]!["rhino"]));
        Assert.False(r["matches"]!.GetValue<bool>());
        var diff = Assert.Single((JsonArray)r["differences"]!);
        Assert.Equal("Rhino", Str(diff!["component"]));
        Assert.Equal("8.19", Str(diff["pinned"]));
        Assert.Equal("8.20", Str(diff["running"]));
        Assert.Null(r["note"]);

        var same = Structured(RecordTools.ToolchainInfo(repo.Full(Gh), "milestone-1", null, Pinned));
        Assert.True(same["matches"]!.GetValue<bool>());
        Assert.Empty((JsonArray)same["differences"]!);

        var unknown = Structured(RecordTools.ToolchainInfo(repo.Full(Gh), "milestone-1", null, null));
        Assert.Null(unknown["matches"]);
        Assert.Null(unknown["differences"]);
        Assert.Null(unknown["running"]);
        Assert.Contains("only known inside Rhino", Str(unknown["note"]));
    }

    [Fact]
    public void Toolchain_refuses_a_tag_without_a_pin_and_an_unknown_tag()
    {
        using var repo = GitRepo.Init();
        var sha1 = Commit(repo, 5, "First", "");
        GitRepo.Git(repo.Root, "tag", "lightweight", sha1);

        var ex = Assert.Throws<ToolArgumentException>(() => RecordTools.ToolchainInfo(repo.Full(Gh), "lightweight", null, Pinned));
        Assert.Contains("lightweight", ex.Message);
        Assert.Contains("outside the G-Loom panel", ex.Message);

        var missing = Assert.Throws<ToolArgumentException>(() => RecordTools.ToolchainInfo(repo.Full(Gh), "nope", null, Pinned));
        Assert.Contains("gloom_tags", missing.Message);
    }

    [Fact]
    public void Toolchain_without_a_tag_lists_the_pins_that_cover_this_definition()
    {
        using var repo = GitRepo.Init();
        var sha1 = Commit(repo, 5, "First", "");
        var sha2 = Commit(repo, 9, "Second", "");
        var sha3 = Commit(repo, 1, "Other definition", "", OtherGh, OtherJson);
        AnnotatedTag(repo, "early", sha1, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        AnnotatedTag(repo, "late", sha2, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        AnnotatedTag(repo, "other", sha3, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        GitRepo.Git(repo.Root, "tag", "lightweight", sha2);
        GitRepo.Git(repo.Root, "branch", "elsewhere", sha1);
        GitRepo.Git(repo.Root, "checkout", "-q", "elsewhere");
        var sha4 = Commit(repo, 7, "On another option", "");
        AnnotatedTag(repo, "elsewhere-tag", sha4, new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero));
        GitRepo.Git(repo.Root, "checkout", "-q", "main");

        var r = Structured(RecordTools.ToolchainInfo(repo.Full(Gh), null, null, null));

        var pins = (JsonArray)r["pins"]!;
        Assert.Equal(new[] { "other", "late", "early" }, pins.Select(p => Str(p!["tag"])).ToArray());
        Assert.Equal(3, r["returned"]!.GetValue<int>());
        Assert.False(r["truncated"]!.GetValue<bool>());
        Assert.Equal(sha3, Str(pins[0]!["sha"]));
        Assert.Equal(sha2, Str(pins[0]!["definitionVersion"]!["sha"]));
        Assert.False(pins[0]!["isOnThisDefinition"]!.GetValue<bool>());
        Assert.Equal(sha2, Str(pins[1]!["sha"]));
        Assert.True(pins[1]!["isOnThisDefinition"]!.GetValue<bool>());
        Assert.Equal("8.19", Str(pins[1]!["toolchain"]!["rhino"]));
        Assert.Null(r["running"]);
        Assert.Contains("only known inside Rhino", Str(r["note"]));
        Assert.Contains("gloom_tags", Str(r["note"]));
    }

    [Fact]
    public void Decision_record_tells_the_story_oldest_first_with_tags_forks_agents_and_the_current_marker()
    {
        using var repo = GitRepo.Init();
        var sha1 = Commit(repo, 5, "First massing", "Gloom-Version: tower_V001");
        GitRepo.Git(repo.Root, "checkout", "-q", "-b", "envelope-mullion");
        var sha2 = Commit(repo, 9, "Wider atrium",
            "Client asked for light.\n\nGloom-Version: tower_V002\nGloom-Agent: claude-code/2.1\nGloom-Agent-Session: s-42\n" +
            $"Gloom-Checkpoint-Base: {sha1}\nGloom-Intent: more daylight");
        AnnotatedTag(repo, "milestone-1", sha1, DateTimeOffset.Now, notes: "DD submittal");
        var live = new LiveSnapshot(repo.Full(Gh), repo.Root, false, sha2, true);

        var md = RecordTools.DecisionRecord(null, 50, false, false, live).Content[0].Text!;

        Assert.StartsWith("# tower — record of decisions", md);
        Assert.Contains("Coding/tower.gh · project " + new DirectoryInfo(repo.Root).Name + " · branch envelope-mullion · 2 versions", md);
        Assert.DoesNotContain("showing the latest", md);

        var v1 = md.IndexOf("## V001 — First massing", StringComparison.Ordinal);
        var v2 = md.IndexOf("## V002 — Wider atrium (current)", StringComparison.Ordinal);
        Assert.True(v1 >= 0 && v2 > v1);
        Assert.DoesNotContain("## V001 — First massing (current)", md);

        var first = md[v1..v2];
        Assert.Contains("· by Tester", first);
        Assert.Contains("Tagged: milestone-1 — Rhino 8.19 · GH 1.0.0.8 · G-Loom 0.3.0", first);
        Assert.Contains("Notes: DD submittal", first);
        Assert.Contains("↰ branched from main", first);
        Assert.DoesNotContain("agent", first);

        var second = md[v2..];
        Assert.Contains($"· by Tester · agent claude-code/2.1 · session s-42 · checkpoint base {sha1[..7]} · intent: more daylight", second);
        Assert.Contains("Client asked for light.", second);
        Assert.DoesNotContain("Gloom-Version", second);
        Assert.DoesNotContain("Tagged:", second);
        Assert.DoesNotContain("Changes (", md);
    }

    [Fact]
    public void Decision_record_can_include_changes_and_run_newest_first()
    {
        using var repo = GitRepo.Init();
        Commit(repo, 5, "First massing", "Gloom-Version: tower_V001");
        Commit(repo, 9, "Wider atrium", "Gloom-Version: tower_V002");

        var md = RecordTools.DecisionRecord(repo.Full(Gh), 50, true, false, null).Content[0].Text!;

        var v1 = md.IndexOf("## V001", StringComparison.Ordinal);
        var v2 = md.IndexOf("## V002", StringComparison.Ordinal);
        Assert.True(v1 >= 0 && v2 > v1);
        Assert.Contains("First version: 1 objects, 0 groups.", md[v1..v2]);
        var second = md[v2..];
        Assert.Contains("Changes (1):", second);
        Assert.Contains("Modified:", second);
        Assert.Contains("  - Slider — ", second);
        Assert.Contains("5", second);
        Assert.Contains("9", second);
        Assert.Contains("(current)", second);

        var reversed = RecordTools.DecisionRecordMarkdown(ProjectLocator.Locate(repo.Full(Gh), null), null, 50, true, true);
        Assert.True(reversed.IndexOf("## V002", StringComparison.Ordinal) < reversed.IndexOf("## V001", StringComparison.Ordinal));
        Assert.Contains("First version: 1 objects, 0 groups.", reversed);

        var latest = RecordTools.DecisionRecordMarkdown(ProjectLocator.Locate(repo.Full(Gh), null), null, 1, true, false);
        Assert.Contains("2 versions (showing the latest 1)", latest);
        Assert.DoesNotContain("## V001", latest);
        Assert.Contains("Changes (1):", latest);
    }
}
