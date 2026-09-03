using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Memory;
using GLoom.Serialization;
using GLoom.Vcs;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class WriteToolsTests
{
    private const string Gh = "Coding/tower.gh";
    private const string Json = "Coding/tower.gloom.json";

    private static void Identity(GitRepo repo)
    {
        GitRepo.Git(repo.Root, "config", "user.name", "Tester");
        GitRepo.Git(repo.Root, "config", "user.email", "t@example.com");
    }

    private static string Commit(GitRepo repo, int version, decimal slider, string subject)
    {
        var gh = repo.Write(Gh, $"gh bytes {subject}");
        var json = CanonicalJson.Write(Doc("tower", Slider(Guid(1), slider)));
        GLoomRepository.StageForCommit(repo.Root, json, repo.Full(Json), gh);
        return GLoomRepository.CommitStaged(repo.Root, subject, $"Gloom-Version: tower_V{version:D3}", "Tester", "t@example.com")!;
    }

    private static JsonObject Structured(ToolResult r) => (JsonObject)r.StructuredContent!;

    [Fact]
    public void Commit_creates_the_next_version_attributed_to_the_configured_identity()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var baseSha = Commit(repo, 1, 5, "First massing");
        var ghFull = repo.Full(Gh);
        var live = new LiveSnapshot(ghFull, repo.Root, true, baseSha, true);
        var refreshes = 0;

        var r = Structured(WriteTools.Commit(
            null, "Reroute the tower geometry", "client asked for more light", "opencode", "adjust massing",
            live, () => CanonicalJson.Write(Doc("tower", Slider(Guid(1), 7))), () => refreshes++));

        Assert.True(r["committed"]!.GetValue<bool>());
        Assert.Equal("tower_V002", r["version"]!.GetValue<string>());
        var sha = r["sha"]!.GetValue<string>();
        Assert.Equal(1, refreshes);

        var commit = GLoomRepository.Log(repo.Root, 1, null).First();
        Assert.Equal(sha, commit.Sha);
        Assert.Equal("Tester", commit.Author);
        var trailers = CommitTrailers.Parse(commit.Body).Trailers;
        Assert.Equal("tower_V002", trailers["Gloom-Version"]);
        Assert.Equal("opencode", trailers["Gloom-Agent"]);
        Assert.Equal("adjust massing", trailers["Gloom-Intent"]);
    }

    [Fact]
    public void Commit_requires_a_subject_and_only_the_active_document()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var sha = Commit(repo, 1, 5, "First");
        var live = new LiveSnapshot(repo.Full(Gh), repo.Root, true, sha, true);
        var serialize = () => CanonicalJson.Write(Doc("tower", Slider(Guid(1), 6)));

        // A missing subject is rejected before touching the canvas.
        Assert.Throws<ToolArgumentException>(() =>
            WriteTools.Commit(null, " ", null, null, null, live, serialize, () => { }));

        // The active document is other.gh; asking to commit tower.gh is refused.
        var otherActive = new LiveSnapshot(repo.Full("Coding/other.gh"), repo.Root, true, sha, true);
        var ex = Assert.Throws<ToolArgumentException>(() =>
            WriteTools.Commit(repo.Full(Gh), "msg", null, null, null, otherActive, serialize, () => { }));
        Assert.Contains("not the active", ex.Message);
    }

    [Fact]
    public void Commit_reports_nothing_to_commit_when_the_recipe_is_unchanged()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var jsonV1 = CanonicalJson.Write(Doc("tower", Slider(Guid(1), 5)));
        var gh = repo.Write(Gh, "gh bytes v1");
        GLoomRepository.StageForCommit(repo.Root, jsonV1, repo.Full(Json), gh);
        var sha1 = GLoomRepository.CommitStaged(repo.Root, "First", "Gloom-Version: tower_V001", "Tester", "t@example.com")!;
        var live = new LiveSnapshot(repo.Full(Gh), repo.Root, false, sha1, true);

        var refreshes = 0;
        var r = WriteTools.Commit(null, "No change", null, null, null, live, () => jsonV1, () => refreshes++);

        Assert.False(r.IsError);
        Assert.Contains("Nothing to commit", r.Content[0].Text);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public void Revert_restores_a_previous_version_and_reloads_the_canvas()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var jsonV1 = CanonicalJson.Write(Doc("tower", Slider(Guid(1), 5)));
        Commit(repo, 1, 5, "First");
        var sha2 = Commit(repo, 2, 9, "Wider atrium");
        var live = new LiveSnapshot(repo.Full(Gh), repo.Root, true, sha2, true);
        var reloaded = new List<string>();

        var r = Structured(WriteTools.Revert(null, "V001", live, path => reloaded.Add(path)));

        Assert.True(r["reverted"]!.GetValue<bool>());
        Assert.Equal("V001", r["restoredTo"]!["version"]!.GetValue<string>());
        Assert.Equal(jsonV1, repo.Read(Json));
        Assert.Equal(new[] { repo.Full(Gh) }, reloaded);
    }

    [Fact]
    public void SwitchBranch_moves_head_and_reloads_the_project()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        Commit(repo, 1, 5, "First");
        GitRepo.Git(repo.Root, "branch", "envelope_b");
        var live = new LiveSnapshot(repo.Full(Gh), repo.Root, false, null, true);
        var reloaded = new List<string>();

        var r = Structured(WriteTools.SwitchBranch(null, "envelope_b", live, root => reloaded.Add(root)));

        Assert.True(r["switched"]!.GetValue<bool>());
        Assert.Equal("envelope_b", r["branch"]!.GetValue<string>());
        Assert.False(r["wasAlreadyCurrent"]!.GetValue<bool>());
        Assert.Equal("envelope_b", GitRepo.Git(repo.Root, "rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.Equal(new[] { repo.Root }, reloaded);

        Assert.Throws<ToolArgumentException>(() =>
            WriteTools.SwitchBranch(null, "nope", live, _ => { }));
    }

    [Fact]
    public void Tag_pins_a_version_with_the_toolchain_and_attribution()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var sha = Commit(repo, 1, 5, "First");
        var live = new LiveSnapshot(repo.Full(Gh), repo.Root, false, sha, true);
        var toolchain = new Toolchain("8.0", "1.0", null, "0.3.0");
        var refreshes = 0;

        var r = Structured(WriteTools.Tag(null, "release 03", null, "milestone signoff", live, () => toolchain, () => refreshes++));

        Assert.True(r["tagged"]!.GetValue<bool>());
        Assert.Equal(1, refreshes);
        Assert.Equal("release-03", r["tag"]!.GetValue<string>());
        Assert.Equal("Tester", r["createdBy"]!.GetValue<string>());
        Assert.Equal(sha, r["commit"]!["sha"]!.GetValue<string>());

        var tag = GitRepo.Git(repo.Root, "cat-file", "-p", "refs/tags/release-03");
        Assert.Contains("8.0", tag);
        Assert.Contains("milestone signoff", tag);
        Assert.Contains("Tester", tag);
    }
}
