using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Memory;
using GLoom.Serialization;
using GLoom.Vcs;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class VersionToolsTests
{
    private const string Gh = "Coding/tower.gh";
    private const string Json = "Coding/tower.gloom.json";

    private static string Commit(GitRepo repo, CanonicalDocument doc, string subject, string body)
    {
        var gh = repo.Write(Gh, $"gh bytes {subject}");
        GLoomRepository.StageForCommit(repo.Root, CanonicalJson.Write(doc), repo.Full(Json), gh);
        return GLoomRepository.CommitStaged(repo.Root, subject, body, "Tester", "t@example.com")!;
    }

    private static string CommitSlider(GitRepo repo, decimal value, int version, string subject = "") =>
        Commit(repo, Doc("tower", Slider(Guid(1), value)), subject.Length > 0 ? subject : $"Version {version}", $"Gloom-Version: tower_V{version:D3}");

    private static void WriteWorking(GitRepo repo, CanonicalDocument doc) => repo.Write(Json, CanonicalJson.Write(doc));

    private static JsonObject Structured(ToolResult r) => (JsonObject)r.StructuredContent!;

    private static string Text(ToolResult r) => r.Content[0].Text!;

    private static string[] Names(JsonNode? array) => ((JsonArray)array!).Select(n => n!["name"]!.GetValue<string>()).ToArray();

    private static CanonicalDocument FiveSliders() =>
        Doc("tower", Slider(Guid(1), 1), Slider(Guid(2), 2), Slider(Guid(3), 3), Slider(Guid(4), 4), Slider(Guid(5), 5));

    // --- gloom_read_version ---

    [Fact]
    public void ReadVersion_pages_objects_and_reports_totals()
    {
        using var repo = GitRepo.Init();
        CommitSlider(repo, 0, 1);
        WriteWorking(repo, FiveSliders());

        var first = Structured(VersionTools.ReadVersion(repo.Full(Gh), null, 0, 2, null, null, null));
        Assert.True(first["version"]!["isWorkingTree"]!.GetValue<bool>());
        Assert.Null(first["version"]!["sha"]);
        Assert.Equal(5, first["totals"]!["objects"]!.GetValue<int>());
        Assert.Equal(5, first["totals"]!["byKind"]!["param"]!.GetValue<int>());
        Assert.Equal(2, first["page"]!["returned"]!.GetValue<int>());
        Assert.True(first["page"]!["hasMore"]!.GetValue<bool>());
        Assert.Equal(2, first["page"]!["nextOffset"]!.GetValue<int>());
        Assert.Equal(Guid(1), first["objects"]![0]!["instanceGuid"]!.GetValue<string>());
        Assert.Null(first["filter"]);
        Assert.Contains("gloom_status", first["note"]!.GetValue<string>());

        var last = Structured(VersionTools.ReadVersion(repo.Full(Gh), "working", 4, 2, null, null, null));
        Assert.Equal(1, last["page"]!["returned"]!.GetValue<int>());
        Assert.False(last["page"]!["hasMore"]!.GetValue<bool>());
        Assert.Null(last["page"]!["nextOffset"]);
        Assert.Equal(Guid(5), last["objects"]![0]!["instanceGuid"]!.GetValue<string>());
    }

    [Fact]
    public void ReadVersion_filters_by_query_and_kind()
    {
        using var repo = GitRepo.Init();
        var doc = Doc("tower", Slider(Guid(1), 5), Component(Guid(2), "Circle"), Component(Guid(3), "Extrude"));
        Commit(repo, doc, "First", "");

        var byName = Structured(VersionTools.ReadVersion(repo.Full(Gh), "HEAD", 0, 50, "CIRC", null, null));
        Assert.Equal(1, byName["filter"]!["matched"]!.GetValue<int>());
        Assert.Equal("CIRC", byName["filter"]!["query"]!.GetValue<string>());
        Assert.Equal(new[] { "Circle" }, Names(byName["objects"]));
        Assert.Equal(3, byName["totals"]!["objects"]!.GetValue<int>());

        var byKind = Structured(VersionTools.ReadVersion(repo.Full(Gh), "HEAD", 0, 50, null, "component", null));
        Assert.Equal(new[] { "Circle", "Extrude" }, Names(byKind["objects"]));

        var byGuid = Structured(VersionTools.ReadVersion(repo.Full(Gh), "HEAD", 0, 50, "000000000001", "param", null));
        Assert.Equal(new[] { "Number Slider" }, Names(byGuid["objects"]));
        Assert.Null(byGuid["note"]);
    }

    [Fact]
    public void ReadVersion_resolves_labels_shas_refs_tags_and_the_working_tree()
    {
        using var repo = GitRepo.Init();
        var sha1 = CommitSlider(repo, 5, 1, "First massing");
        var sha2 = CommitSlider(repo, 9, 2, "Wider");
        GitRepo.Git(repo.Root, "tag", "milestone", sha1);
        WriteWorking(repo, Doc("tower", Slider(Guid(1), 42)));

        foreach (var reference in new[] { "V001", "tower_V001", sha1, sha1[..7], "HEAD~1", "milestone" })
        {
            var r = Structured(VersionTools.ReadVersion(repo.Full(Gh), reference, 0, 50, null, null, null));
            Assert.Equal(sha1, r["version"]!["sha"]!.GetValue<string>());
            Assert.Equal("V001", r["version"]!["versionLabel"]!.GetValue<string>());
            Assert.Equal("First massing", r["version"]!["subject"]!.GetValue<string>());
            Assert.Equal(reference, r["version"]!["reference"]!.GetValue<string>());
            Assert.Equal(5m, r["objects"]![0]!["persistent"]!["slider"]!["value"]!.GetValue<decimal>());
        }

        var head = Structured(VersionTools.ReadVersion(repo.Full(Gh), "HEAD", 0, 50, null, null, null));
        Assert.Equal(sha2, head["version"]!["sha"]!.GetValue<string>());

        var working = Structured(VersionTools.ReadVersion(repo.Full(Gh), null, 0, 50, null, null, null));
        Assert.Equal("working tree", working["version"]!["label"]!.GetValue<string>());
        Assert.Equal(42m, working["objects"]![0]!["persistent"]!["slider"]!["value"]!.GetValue<decimal>());
    }

    [Fact]
    public void ReadVersion_refuses_unknown_versions_and_versions_without_a_recipe()
    {
        using var repo = GitRepo.Init();
        CommitSlider(repo, 5, 1);
        var bad = Assert.Throws<ToolArgumentException>(() => VersionTools.ReadVersion(repo.Full(Gh), "nope", 0, 50, null, null, null));
        Assert.Contains("gloom_history", bad.Message);
        var missing = Assert.Throws<ToolArgumentException>(() => VersionTools.ReadVersion(repo.Full(Gh), "V007", 0, 50, null, null, null));
        Assert.Contains("gloom_history", missing.Message);

        repo.Write(Gh, "gh only");
        GitRepo.Git(repo.Root, "add", "--", Gh);
        GitRepo.Git(repo.Root, "-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "-m", "gh only");
        File.Delete(repo.Full(Json));
        GitRepo.Git(repo.Root, "add", "--", Json);
        GitRepo.Git(repo.Root, "-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "-m", "drop recipe");

        var ex = Assert.Throws<ToolArgumentException>(() => VersionTools.ReadVersion(repo.Full(Gh), "HEAD", 0, 50, null, null, null));
        Assert.Contains("No recipe", ex.Message);
        var disk = Assert.Throws<ToolArgumentException>(() => VersionTools.ReadVersion(repo.Full(Gh), null, 0, 50, null, null, null));
        Assert.Contains("No recipe on disk", disk.Message);
    }

    // --- gloom_diff ---

    private static (CanonicalDocument From, CanonicalDocument To) DrawerScenario()
    {
        var height = Slider(Guid(1), 5) with { Nickname = "Height" };
        var radius = Slider(Guid(4), 7) with { Nickname = "Radius" };
        var from = Doc("tower",
            height,
            Component(Guid(2), "Circle", 10, 10, Input(Guid(20), "R", Guid(1))),
            Component(Guid(3), "Loft"),
            radius);
        var to = Doc("tower",
            height with { Persistent = new PersistentData("slider", new SliderValue(9, 0, 100, 0, "integer")) },
            Component(Guid(2), "Circle", 50, 60, Input(Guid(20), "R", Guid(4))),
            Component(Guid(5), "Extrude"),
            radius with { Nickname = "Rad" });
        return (from, to);
    }

    [Fact]
    public void Diff_matches_the_panel_drawer_verbatim()
    {
        using var repo = GitRepo.Init();
        var (from, to) = DrawerScenario();
        var sha = Commit(repo, from, "First", "Gloom-Version: tower_V001");
        WriteWorking(repo, to);
        var expected = DocumentDiff.Compute(from, to);

        var r = Structured(VersionTools.Diff(repo.Full(Gh), null, null, 200, null));

        Assert.Equal(sha, r["from"]!["sha"]!.GetValue<string>());
        Assert.Equal("V001", r["from"]!["label"]!.GetValue<string>());
        Assert.True(r["to"]!["isWorkingTree"]!.GetValue<bool>());
        Assert.False(r["isEmpty"]!.GetValue<bool>());
        Assert.Equal(expected.TotalChanges, r["totalChanges"]!.GetValue<int>());
        Assert.Equal(DiffSummaryText.Headline(expected, "tower"), r["headline"]!.GetValue<string>());
        Assert.Equal(expected.ObjectsAdded.Select(DocumentDiff.DisplayName).ToArray(), Names(r["added"]));
        Assert.Equal(expected.ObjectsRemoved.Select(DocumentDiff.DisplayName).ToArray(), Names(r["removed"]));
        Assert.Equal(expected.ObjectsModified.Select(c => DocumentDiff.DisplayName(c.To)).ToArray(), Names(r["modified"]));
        Assert.Equal(
            expected.ObjectsModified.Select(c => c.Summary).ToArray(),
            ((JsonArray)r["modified"]!).Select(n => n!["summary"]!.GetValue<string>()).ToArray());
        Assert.Equal(3, r["counts"]!["modified"]!.GetValue<int>());
        Assert.Equal("Loft", r["removed"]![0]!["componentName"]!.GetValue<string>());
        Assert.Equal("component", r["added"]![0]!["kind"]!.GetValue<string>());
        Assert.False(r["truncated"]!.GetValue<bool>());
        Assert.Contains("gloom_status", r["note"]!.GetValue<string>());
    }

    [Fact]
    public void Diff_details_name_wire_sources_moves_renames_and_values()
    {
        using var repo = GitRepo.Init();
        var (from, to) = DrawerScenario();
        Commit(repo, from, "First", "");
        WriteWorking(repo, to);

        var modified = (JsonArray)Structured(VersionTools.Diff(repo.Full(Gh), "HEAD", "working", 200, null))["modified"]!;
        var byName = modified.ToDictionary(n => n!["name"]!.GetValue<string>(), n => n!);

        var circle = byName["Circle"];
        Assert.Equal(new[] { "moved", "wiresChanged" }, ((JsonArray)circle["kinds"]!).Select(k => k!.GetValue<string>()).ToArray());
        Assert.Equal(10f, circle["details"]!["moved"]!["from"]!["x"]!.GetValue<float>());
        Assert.Equal(60f, circle["details"]!["moved"]!["to"]!["y"]!.GetValue<float>());
        var wire = circle["details"]!["wires"]![0]!;
        Assert.Equal("R", wire["input"]!["name"]!.GetValue<string>());
        Assert.Equal(Guid(20), wire["input"]!["instanceGuid"]!.GetValue<string>());
        Assert.Equal(Guid(1), wire["before"]![0]!.GetValue<string>());
        Assert.Equal(Guid(4), wire["after"]![0]!.GetValue<string>());
        Assert.Equal("Rad", wire["connected"]![0]!["sourceObject"]!.GetValue<string>());
        Assert.Equal("Height", wire["disconnected"]![0]!["sourceObject"]!.GetValue<string>());
        Assert.Null(circle["details"]!["persistent"]);

        var height = byName["Height"];
        Assert.Equal(new[] { "persistentChanged" }, ((JsonArray)height["kinds"]!).Select(k => k!.GetValue<string>()).ToArray());
        Assert.Equal(5m, height["details"]!["persistent"]!["before"]!["slider"]!["value"]!.GetValue<decimal>());
        Assert.Equal(9m, height["details"]!["persistent"]!["after"]!["slider"]!["value"]!.GetValue<decimal>());

        var rad = byName["Rad"];
        Assert.Equal("Radius", rad["details"]!["renamed"]!["from"]!.GetValue<string>());
        Assert.Equal("Rad", rad["details"]!["renamed"]!["to"]!.GetValue<string>());
    }

    [Fact]
    public void Diff_between_two_committed_versions_and_without_any_commit()
    {
        using var repo = GitRepo.Init();
        CommitSlider(repo, 5, 1);
        CommitSlider(repo, 9, 2);

        var r = Structured(VersionTools.Diff(repo.Full(Gh), "V001", "V002", 200, null));
        Assert.Equal("V001", r["from"]!["label"]!.GetValue<string>());
        Assert.Equal("V002", r["to"]!["label"]!.GetValue<string>());
        Assert.False(r["to"]!["isWorkingTree"]!.GetValue<bool>());
        Assert.Equal("slider 5 → 9", r["modified"]![0]!["summary"]!.GetValue<string>());
        Assert.Null(r["note"]);

        var same = Structured(VersionTools.Diff(repo.Full(Gh), "V002", "HEAD", 200, null));
        Assert.True(same["isEmpty"]!.GetValue<bool>());

        using var fresh = GitRepo.Init();
        fresh.Write(Gh, "unsaved");
        var ex = Assert.Throws<ToolArgumentException>(() => VersionTools.Diff(fresh.Full(Gh), null, null, 200, null));
        Assert.Contains("no committed versions", ex.Message);
    }

    [Fact]
    public void Diff_truncates_each_category_at_maxItems_but_counts_everything()
    {
        using var repo = GitRepo.Init();
        Commit(repo, Doc("tower"), "Empty", "");
        WriteWorking(repo, FiveSliders());

        var r = Structured(VersionTools.Diff(repo.Full(Gh), null, null, 2, null));
        Assert.Equal(2, ((JsonArray)r["added"]!).Count);
        Assert.Equal(5, r["counts"]!["added"]!.GetValue<int>());
        Assert.True(r["truncated"]!.GetValue<bool>());
        Assert.Contains("maxItems", r["note"]!.GetValue<string>());
    }

    // --- gloom_explain_changes ---

    [Fact]
    public void Explain_one_version_names_it_its_author_its_agent_and_its_changes()
    {
        using var repo = GitRepo.Init();
        CommitSlider(repo, 5, 1, "First massing");
        Commit(repo, Doc("tower", Slider(Guid(1), 9), Component(Guid(2), "Circle")), "Wider atrium",
            "Client asked for light.\n\nGloom-Version: tower_V002\nGloom-Agent: claude-code/2.1\nGloom-Intent: more daylight");

        var text = Text(VersionTools.ExplainChanges(repo.Full(Gh), "V002", null, null, null));

        Assert.Contains("## tower V002 — Wider atrium", text);
        Assert.Contains("by Tester · agent claude-code/2.1 · intent: more daylight", text);
        Assert.Contains("Client asked for light.", text);
        Assert.Contains("Changes (2):", text);
        Assert.Contains("  - Circle", text);
        Assert.Contains("  - Slider — slider 5 → 9", text);
        Assert.DoesNotContain("First version", text);

        Assert.Equal(text, Text(VersionTools.ExplainChanges(repo.Full(Gh), null, null, null, null)));
    }

    [Fact]
    public void Explain_the_first_version_describes_it_whole()
    {
        using var repo = GitRepo.Init();
        Commit(repo, Doc("tower", Slider(Guid(1), 5), Component(Guid(2), "Circle")), "First massing", "Gloom-Version: tower_V001");

        var text = Text(VersionTools.ExplainChanges(repo.Full(Gh), "V001", null, null, null));

        Assert.Contains("## tower V001 — First massing", text);
        Assert.Contains("First version of this definition.", text);
        Assert.Contains("2 objects (1 component, 1 param), 0 groups.", text);
        Assert.Contains("Added Circle; added Slider", text);
    }

    [Fact]
    public void Explain_a_range_walks_oldest_to_newest_and_ends_with_the_working_tree()
    {
        using var repo = GitRepo.Init();
        CommitSlider(repo, 1, 1, "One");
        CommitSlider(repo, 2, 2, "Two");
        CommitSlider(repo, 3, 3, "Three");

        var range = Text(VersionTools.ExplainChanges(repo.Full(Gh), null, "V001", "V003", null));
        Assert.DoesNotContain("## tower V001", range);
        var two = range.IndexOf("## tower V002 — Two", StringComparison.Ordinal);
        var three = range.IndexOf("## tower V003 — Three", StringComparison.Ordinal);
        Assert.True(two >= 0 && three > two);
        Assert.Contains("slider 1 → 2", range);
        Assert.Contains("slider 2 → 3", range);
        Assert.DoesNotContain("Uncommitted", range);

        WriteWorking(repo, Doc("tower", Slider(Guid(1), 10)));
        var tail = Text(VersionTools.ExplainChanges(repo.Full(Gh), null, "V002", null, null));
        Assert.DoesNotContain("## tower V002", tail);
        var section = tail.IndexOf("## tower — Uncommitted changes on disk", StringComparison.Ordinal);
        Assert.True(section > tail.IndexOf("## tower V003", StringComparison.Ordinal));
        Assert.Contains("Compared with V003 — Three", tail);
        Assert.Contains("slider 3 → 10", tail);

        var backwards = Text(VersionTools.ExplainChanges(repo.Full(Gh), null, "V003", "V001", null));
        Assert.Contains("not an earlier version", backwards);
        Assert.DoesNotContain("##", backwards);

        Assert.Contains("not both", Assert.Throws<ToolArgumentException>(
            () => VersionTools.ExplainChanges(repo.Full(Gh), "V002", "V001", null, null)).Message);
    }

    [Fact]
    public void Explain_says_when_a_recipe_is_unavailable_instead_of_failing()
    {
        using var repo = GitRepo.Init();
        repo.Write(Gh, "gh only");
        GitRepo.Git(repo.Root, "add", "--", Gh);
        GitRepo.Git(repo.Root, "-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "-m", "gh only");
        Commit(repo, Doc("tower", Slider(Guid(1), 5)), "With recipe", "Gloom-Version: tower_V002");

        var text = Text(VersionTools.ExplainChanges(repo.Full(Gh), "V002", null, null, null));
        Assert.Contains("Recipe unavailable at the previous version", text);
        Assert.Contains("1 object (1 param), 0 groups.", text);

        var older = Text(VersionTools.ExplainChanges(repo.Full(Gh), "HEAD~1", null, null, null));
        Assert.Contains("Recipe unavailable at this version", older);
    }

    [Fact]
    public void ReadVersion_prefers_a_ref_that_merely_ends_in_a_label_and_refuses_unknown_kinds()
    {
        using var repo = GitRepo.Init();
        var sha1 = CommitSlider(repo, 5, 1);
        var sha2 = CommitSlider(repo, 9, 2);
        GitRepo.Git(repo.Root, "tag", "site_V2", sha1);

        string ShaOf(string reference) =>
            Structured(VersionTools.ReadVersion(repo.Full(Gh), reference, 0, 50, null, null, null))["version"]!["sha"]!.GetValue<string>();

        Assert.Equal(sha1, ShaOf("site_V2"));
        Assert.Equal(sha2, ShaOf("tower_V2"));
        Assert.Equal(sha2, ShaOf("V2"));
        Assert.Equal(sha1, ShaOf("renamed_V1"));
        Assert.Contains("not a tag or branch either", Assert.Throws<ToolArgumentException>(() => ShaOf("other_V9")).Message);

        var kind = Assert.Throws<ToolArgumentException>(() => VersionTools.ReadVersion(repo.Full(Gh), null, 0, 50, null, "slider", null));
        Assert.Contains("\"param\"", kind.Message);
    }

    [Fact]
    public void Explain_a_ref_that_changed_other_files_narrates_the_version_it_holds()
    {
        using var repo = GitRepo.Init();
        var sha1 = CommitSlider(repo, 5, 1, "One");
        var sha2 = CommitSlider(repo, 9, 2, "Two");
        repo.Write("Coding/site.gh", "site");
        GitRepo.Git(repo.Root, "add", "--", "Coding/site.gh");
        GitRepo.Git(repo.Root, "-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "-m", "site version 1");
        var head = GitRepo.Git(repo.Root, "rev-parse", "HEAD").Trim();

        var f = ProjectLocator.Locate(repo.Full(Gh), null);
        Assert.Equal(sha2, VersionRef.PreviousTouching(f, head)!.Sha);
        Assert.Equal(sha1, VersionRef.PreviousTouching(f, sha2)!.Sha);
        Assert.Null(VersionRef.PreviousTouching(f, sha1));

        var text = Text(VersionTools.ExplainChanges(repo.Full(Gh), "HEAD", null, null, null));
        Assert.Contains($"HEAD ({head[..7]}) did not change tower", text);
        Assert.Contains("## tower V002 — Two", text);
        Assert.Contains("slider 5 → 9", text);
        Assert.DoesNotContain("site version 1", text);
    }

    [Fact]
    public void Explain_a_range_from_before_the_definition_existed_starts_with_its_first_version()
    {
        using var repo = GitRepo.Init();
        repo.Write("README.md", "hello");
        GitRepo.Git(repo.Root, "add", "--", "README.md");
        GitRepo.Git(repo.Root, "-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "-m", "root");
        var root = GitRepo.Git(repo.Root, "rev-parse", "HEAD").Trim();
        CommitSlider(repo, 1, 1, "One");
        CommitSlider(repo, 2, 2, "Two");

        var text = Text(VersionTools.ExplainChanges(repo.Full(Gh), null, root, "V002", null));
        Assert.Contains("## tower V001 — One", text);
        Assert.Contains("First version of this definition.", text);
        Assert.DoesNotContain("Recipe unavailable", text);
        Assert.Contains("slider 1 → 2", text);
    }
}
