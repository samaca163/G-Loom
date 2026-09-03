using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.State;
using GLoom.Mcp.Tools.Memory;
using GLoom.Serialization;
using GLoom.Vcs;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class EnvelopeToolsTests : IDisposable
{
    private const string Gh = "Coding/tower.gh";
    private const string Json = "Coding/tower.gloom.json";

    private readonly string _originalRoot = McpPaths.Root;
    private readonly string _stateRoot =
        Path.Combine(Path.GetTempPath(), "gloom-tests", "mcp-" + System.Guid.NewGuid().ToString("N"));

    public EnvelopeToolsTests() => McpPaths.Root = _stateRoot;

    public void Dispose()
    {
        EnvelopeStore.Close();
        McpPaths.Root = _originalRoot;
        try { Directory.Delete(_stateRoot, true); } catch { }
    }

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
        return GLoomRepository.CommitStaged(
            repo.Root, subject, $"Gloom-Version: tower_V{version:D3}", "Tester", "t@example.com")!;
    }

    private static LiveSnapshot Live(GitRepo repo, string sha) =>
        new(repo.Full(Gh), repo.Root, false, sha, true);

    private static JsonObject Structured(ToolResult r) => (JsonObject)r.StructuredContent!;

    private static string Serialize(decimal slider) =>
        CanonicalJson.Write(Doc("tower", Slider(Guid(1), slider)));

    [Fact]
    public void Begin_checkpoints_the_last_version_and_aims_the_overlay_at_it()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var sha = Commit(repo, 1, 5, "First massing");

        var overlay = new List<string>();
        var r = Structured(EnvelopeTools.Begin(
            "claude-code", "s1", "raise the podium", Live(repo, sha),
            () => Serialize(5), overlay.Add, () => { }));

        Assert.True((bool)r["opened"]!);
        Assert.Equal(sha, (string)r["checkpoint"]!["sha"]!);
        Assert.False((bool)r["checkpoint"]!["committedNow"]!);

        // The overlay is pointed at the checkpoint so the human sees the changes as they land.
        Assert.Equal(new[] { sha }, overlay);
        Assert.Equal("raise the podium", EnvelopeStore.Current!.Intent);
    }

    [Fact]
    public void Begin_commits_the_humans_unsaved_work_so_the_checkpoint_is_returnable()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var first = Commit(repo, 1, 5, "First massing");

        // The canvas has moved on since that commit; serializing yields a different recipe.
        var r = Structured(EnvelopeTools.Begin(
            "claude-code", null, "raise the podium", Live(repo, first),
            () => Serialize(9), _ => { }, () => { }));

        Assert.True((bool)r["checkpoint"]!["committedNow"]!);
        var checkpoint = (string)r["checkpoint"]!["sha"]!;
        Assert.NotEqual(first, checkpoint);

        var head = GLoomRepository.Log(repo.Root, 1)[0];
        Assert.Equal(checkpoint, head.Sha);
        Assert.Contains("Checkpoint before claude-code edits tower", head.Message);
        Assert.Equal("Tester", head.Author);
    }

    [Fact]
    public void Begin_refuses_a_second_envelope_and_names_who_holds_it()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var sha = Commit(repo, 1, 5, "First massing");

        EnvelopeTools.Begin("claude-code", null, "raise the podium", Live(repo, sha),
            () => Serialize(5), _ => { }, () => { });

        var second = EnvelopeTools.Begin("cursor", null, "something else", Live(repo, sha),
            () => Serialize(5), _ => { }, () => { });

        Assert.True(second.IsError);
        Assert.Contains("claude-code", second.Content[0].Text);
        Assert.Contains("raise the podium", second.Content[0].Text);
    }

    [Fact]
    public void Begin_requires_an_intent()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var sha = Commit(repo, 1, 5, "First massing");

        Assert.Throws<ToolArgumentException>(() => EnvelopeTools.Begin(
            "claude-code", null, "   ", Live(repo, sha), () => Serialize(5), _ => { }, () => { }));
    }

    [Fact]
    public void End_commits_with_the_agent_intent_and_checkpoint_trailers()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var sha = Commit(repo, 1, 5, "First massing");

        EnvelopeTools.Begin("claude-code", "s1", "raise the podium", Live(repo, sha),
            () => Serialize(5), _ => { }, () => { });

        var overlay = new List<string>();
        var r = Structured(EnvelopeTools.End(
            "Raise the podium to four storeys", "the client asked for more retail frontage", false,
            Live(repo, sha), () => Serialize(12), overlay.Add, _ => { }, () => { }));

        Assert.True((bool)r["committed"]!);

        var head = GLoomRepository.Log(repo.Root, 1)[0];
        var trailers = CommitTrailers.Parse(head.Body).Trailers;
        Assert.Equal("claude-code", trailers["Gloom-Agent"]);
        Assert.Equal("s1", trailers["Gloom-Agent-Session"]);
        Assert.Equal("raise the podium", trailers["Gloom-Intent"]);
        Assert.Equal(sha, trailers["Gloom-Checkpoint-Base"]);

        // The commit is the human's; the agent only appears in the trailers.
        Assert.Equal("Tester", head.Author);
        Assert.Equal("Raise the podium to four storeys", head.Message);

        // The envelope is closed and the overlay is back on the latest version.
        Assert.Null(EnvelopeStore.Current);
        Assert.Equal(new[] { "HEAD" }, overlay);
    }

    [Fact]
    public void End_with_discard_restores_the_checkpoint_and_reloads_the_canvas()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var sha = Commit(repo, 1, 5, "First massing");

        EnvelopeTools.Begin("claude-code", null, "raise the podium", Live(repo, sha),
            () => Serialize(5), _ => { }, () => { });

        // The agent's edit reaches disk the way another server's would: written, not committed.
        repo.Write(Gh, "gh bytes edited by an agent");

        var reloaded = new List<string>();
        var r = Structured(EnvelopeTools.End(
            null, null, true, Live(repo, sha), () => Serialize(12), _ => { }, reloaded.Add, () => { }));

        Assert.True((bool)r["discarded"]!);
        Assert.Equal("gh bytes First massing", repo.Read(Gh));
        Assert.Equal(new[] { repo.Full(Gh) }, reloaded);
        Assert.Null(EnvelopeStore.Current);
    }

    [Fact]
    public void End_closes_cleanly_when_nothing_changed()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var sha = Commit(repo, 1, 5, "First massing");

        EnvelopeTools.Begin("claude-code", null, "look around", Live(repo, sha),
            () => Serialize(5), _ => { }, () => { });

        var r = EnvelopeTools.End("Nothing", null, false, Live(repo, sha),
            () => Serialize(5), _ => { }, _ => { }, () => { });

        Assert.False(r.IsError);
        Assert.Contains("Nothing changed since the checkpoint", r.Content[0].Text);
        Assert.Null(EnvelopeStore.Current);
    }

    [Fact]
    public void A_canvas_mutation_is_refused_while_no_envelope_is_open()
    {
        using var repo = GitRepo.Init();
        Identity(repo);
        var sha = Commit(repo, 1, 5, "First massing");
        var f = ProjectLocator.Locate(null, Live(repo, sha));

        var refusal = EnvelopeTools.RequireOpen(f);
        Assert.NotNull(refusal);
        Assert.Contains("gloom_begin_edit", refusal);

        EnvelopeTools.Begin("claude-code", null, "raise the podium", Live(repo, sha),
            () => Serialize(5), _ => { }, () => { });

        Assert.Null(EnvelopeTools.RequireOpen(f));
    }
}
