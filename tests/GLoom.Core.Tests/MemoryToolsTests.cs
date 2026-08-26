using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Memory;
using GLoom.Serialization;
using GLoom.Vcs;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class MemoryToolsTests
{
    private const string Gh = "Coding/tower.gh";
    private const string Json = "Coding/tower.gloom.json";

    private static string Commit(GitRepo repo, decimal slider, string subject, string body)
    {
        var gh = repo.Write(Gh, $"gh bytes {subject}");
        GLoomRepository.StageForCommit(repo.Root, CanonicalJson.Write(Doc("tower", Slider(Guid(1), slider))), repo.Full(Json), gh);
        return GLoomRepository.CommitStaged(repo.Root, subject, body, "Tester", "t@example.com")!;
    }

    private static JsonObject Structured(ToolResult r) => (JsonObject)r.StructuredContent!;

    [Fact]
    public void History_reads_commits_with_versions_trailers_and_the_current_marker()
    {
        using var repo = GitRepo.Init();
        var sha1 = Commit(repo, 5, "First massing", "Gloom-Version: tower_V001");
        var sha2 = Commit(repo, 9, "Wider atrium", "Client asked for light.\n\nGloom-Version: tower_V002\nGloom-Agent: claude-code/2.1");
        var live = new LiveSnapshot(repo.Full(Gh), repo.Root, false, sha2, true);

        var r = Structured(MemoryTools.History(null, 10, live));

        Assert.Equal("main", r["branch"]!.GetValue<string>());
        Assert.Equal(2, r["returned"]!.GetValue<int>());
        var commits = (JsonArray)r["commits"]!;
        Assert.Equal(sha2, commits[0]!["sha"]!.GetValue<string>());
        Assert.Equal("V002", commits[0]!["version"]!.GetValue<string>());
        Assert.Equal("Client asked for light.", commits[0]!["description"]!.GetValue<string>());
        Assert.Equal("claude-code/2.1", commits[0]!["trailers"]!["Gloom-Agent"]!.GetValue<string>());
        Assert.True(commits[0]!["isCurrent"]!.GetValue<bool>());
        Assert.Equal(sha1, commits[1]!["sha"]!.GetValue<string>());
        Assert.False(commits[1]!["isCurrent"]!.GetValue<bool>());
        Assert.False(r["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Status_of_the_active_document_uses_the_live_snapshot()
    {
        using var repo = GitRepo.Init();
        var sha = Commit(repo, 5, "First", "Gloom-Version: tower_V001");
        var live = new LiveSnapshot(repo.Full(Gh), repo.Root, true, sha, true);

        var r = Structured(MemoryTools.Status(null, live));

        Assert.True(r["isActiveDocument"]!.GetValue<bool>());
        Assert.Equal(repo.Root, r["projectRoot"]!.GetValue<string>());
        Assert.Equal("Coding/tower.gh", r["definitionPath"]!.GetValue<string>());
        Assert.Equal("Coding/tower.gloom.json", r["recipePath"]!.GetValue<string>());
        Assert.Equal(1, r["commitCount"]!.GetValue<int>());
        Assert.Equal("tower_V002", r["nextVersion"]!.GetValue<string>());
        Assert.Equal("V001", r["currentVersion"]!["version"]!.GetValue<string>());
        Assert.True(r["unsavedEdits"]!.GetValue<bool>());
        Assert.Equal(sha, r["lastCommit"]!["sha"]!.GetValue<string>());
    }

    [Fact]
    public void Status_of_another_file_matches_the_working_tree_against_history_itself()
    {
        using var repo = GitRepo.Init();
        var sha1 = Commit(repo, 5, "First", "");
        Commit(repo, 9, "Second", "");
        GLoomRepository.Restore(repo.Root, sha1, new[] { Gh, Json });

        var r = Structured(MemoryTools.Status(repo.Full(Gh), live: null));
        Assert.False(r["isActiveDocument"]!.GetValue<bool>());
        Assert.Equal(sha1, r["currentVersion"]!["sha"]!.GetValue<string>());
        Assert.Equal("First", r["currentVersion"]!["subject"]!.GetValue<string>());
        Assert.Null(r["unsavedEdits"]);

        repo.Write(Json, "{}");
        var dirty = Structured(MemoryTools.Status(repo.Full(Gh), live: null));
        Assert.Null(dirty["currentVersion"]);
        Assert.Contains("does not match", dirty["note"]!.GetValue<string>());
    }

    [Fact]
    public void Relative_paths_resolve_against_the_active_project_and_nothing_else()
    {
        using var repo = GitRepo.Init();
        Commit(repo, 5, "First", "");
        var live = new LiveSnapshot(repo.Full("Coding/other.gh"), repo.Root, false, null, true);

        var located = ProjectLocator.Locate("Coding/tower.gh", live);
        Assert.Equal(repo.Full(Gh), located.GhFullPath);
        Assert.False(located.IsActiveDocument);

        var ex = Assert.Throws<ToolArgumentException>(() => ProjectLocator.Locate("Coding/tower.gh", null));
        Assert.Contains("relative", ex.Message);
        Assert.Contains("No active", Assert.Throws<ToolArgumentException>(() => ProjectLocator.Locate(null, null)).Message);
    }

    [Fact]
    public void A_file_outside_any_repository_is_refused_with_a_reason()
    {
        using var repo = GitRepo.Empty();
        var path = repo.Write("loose.gh", "x");
        var ex = Assert.Throws<ToolArgumentException>(() => MemoryTools.Status(path, null));
        Assert.Contains("not inside a git repository", ex.Message);
    }

    [Fact]
    public void A_deleted_definition_still_has_history_and_a_wrong_path_says_so()
    {
        using var repo = GitRepo.Init();
        Commit(repo, 5, "First", "");
        File.Delete(repo.Full(Gh));

        var r = Structured(MemoryTools.History(repo.Full(Gh), 5, null));
        Assert.Equal(1, r["returned"]!.GetValue<int>());
        Assert.False(r["exists"]!.GetValue<bool>());
        Assert.Null(r["note"]);
        Assert.False(Structured(MemoryTools.Status(repo.Full(Gh), null))["exists"]!.GetValue<bool>());

        var typo = Structured(MemoryTools.History(repo.Full("Coding/towr.gh"), 5, null));
        Assert.Equal(0, typo["returned"]!.GetValue<int>());
        Assert.Contains("check the path", typo["note"]!.GetValue<string>());
    }

    [Fact]
    public void Truncation_is_reported_only_when_more_history_exists()
    {
        using var repo = GitRepo.Init();
        Commit(repo, 1, "One", "");
        Commit(repo, 2, "Two", "");
        Commit(repo, 3, "Three", "");

        Assert.False(Structured(MemoryTools.History(repo.Full(Gh), 3, null))["truncated"]!.GetValue<bool>());
        var two = Structured(MemoryTools.History(repo.Full(Gh), 2, null));
        Assert.True(two["truncated"]!.GetValue<bool>());
        Assert.Equal(2, two["returned"]!.GetValue<int>());
    }
}
