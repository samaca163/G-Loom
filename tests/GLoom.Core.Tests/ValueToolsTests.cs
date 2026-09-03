using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.State;
using GLoom.Mcp.Tools.Live;
using GLoom.Mcp.Tools.Memory;
using GLoom.Serialization;
using GLoom.Vcs;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class ValueToolsTests : IDisposable
{
    private const string Gh = "Coding/tower.gh";
    private const string Json = "Coding/tower.gloom.json";

    private readonly string _originalRoot = McpPaths.Root;
    private readonly string _stateRoot =
        Path.Combine(Path.GetTempPath(), "gloom-tests", "mcp-" + System.Guid.NewGuid().ToString("N"));

    public ValueToolsTests() => McpPaths.Root = _stateRoot;

    public void Dispose()
    {
        EnvelopeStore.Close();
        McpPaths.Root = _originalRoot;
        try { Directory.Delete(_stateRoot, true); } catch { }
    }

    private static string Seed(GitRepo repo)
    {
        GitRepo.Git(repo.Root, "config", "user.name", "Tester");
        GitRepo.Git(repo.Root, "config", "user.email", "t@example.com");
        var gh = repo.Write(Gh, "gh bytes");
        var json = CanonicalJson.Write(Doc("tower", Slider(Guid(1), 5)));
        GLoomRepository.StageForCommit(repo.Root, json, repo.Full(Json), gh);
        return GLoomRepository.CommitStaged(repo.Root, "First", "Gloom-Version: tower_V001", "Tester", "t@example.com")!;
    }

    private static LiveSnapshot Live(GitRepo repo, string sha) => new(repo.Full(Gh), repo.Root, false, sha, true);

    private static JsonArray Edits(params (string Target, JsonNode Value)[] edits)
    {
        var arr = new JsonArray();
        foreach (var (target, value) in edits)
            arr.Add(new JsonObject { ["object"] = target, ["value"] = value });
        return arr;
    }

    private static void Open(GitRepo repo, string sha) =>
        EnvelopeStore.Open(new EditEnvelope(
            repo.Root, Gh, sha, "claude-code", "s1", "raise the podium", DateTimeOffset.Now));

    [Fact]
    public void Refuses_without_an_open_envelope_so_there_is_always_a_checkpoint()
    {
        using var repo = GitRepo.Init();
        var sha = Seed(repo);
        var host = new FakeLiveHost();

        var r = ValueTools.SetValues(host, null, Edits(("Height", 12)), true, Live(repo, sha));

        Assert.True(r.IsError);
        Assert.Contains("gloom_begin_edit", r.Content[0].Text);
        Assert.Equal(0, host.CallsTo("SetValues"));
    }

    [Fact]
    public void Applies_a_batch_and_reports_before_and_after()
    {
        using var repo = GitRepo.Init();
        var sha = Seed(repo);
        Open(repo, sha);
        var host = new FakeLiveHost();

        var r = ValueTools.SetValues(
            host, null, Edits(("Height", 12), ("Label", "podium"), ("Enabled", true)), true, Live(repo, sha));

        Assert.False(r.IsError);
        var o = (JsonObject)r.StructuredContent!;
        Assert.Equal(3, (int)o["applied"]!);
        Assert.Equal(0, (int)o["failed"]!);
        Assert.True((bool)o["solved"]!);

        var first = (JsonObject)((JsonArray)o["edits"]!)[0]!;
        Assert.Equal("Height", (string)first["target"]!);
        Assert.Equal("0", (string)first["before"]!);
        Assert.Equal("12", (string)first["after"]!);
    }

    [Fact]
    public void Numbers_and_booleans_reach_the_host_as_invariant_text()
    {
        using var repo = GitRepo.Init();
        var sha = Seed(repo);
        Open(repo, sha);
        var host = new FakeLiveHost();

        ValueTools.SetValues(host, null, Edits(("Height", 3.75), ("Enabled", false)), true, Live(repo, sha));

        var edits = (IReadOnlyList<ValueEdit>)host.ArgsOf("SetValues")[1]!;
        Assert.Equal("3.75", edits[0].Value);
        Assert.Equal("false", edits[1].Value);
    }

    [Fact]
    public void One_refused_edit_does_not_lose_the_others()
    {
        using var repo = GitRepo.Init();
        var sha = Seed(repo);
        Open(repo, sha);

        var host = new FakeLiveHost();
        host.ValueResults = new List<ValueEditResult>
        {
            new("Height", true, Guid(1), "Height", "Height", "slider", "5", "12"),
            new("Gradient", false, Guid(2), "Gradient", "Gradient", "GH_GradientControl",
                Reason: "A gradient's stops cannot be set through G-Loom."),
        };

        var r = ValueTools.SetValues(host, null, Edits(("Height", 12), ("Gradient", "red")), true, Live(repo, sha));
        var o = (JsonObject)r.StructuredContent!;

        Assert.Equal(1, (int)o["applied"]!);
        Assert.Equal(1, (int)o["failed"]!);
        Assert.Contains("discard", (string)o["note"]!);
    }

    [Fact]
    public void An_empty_or_malformed_batch_is_rejected_before_the_host_is_touched()
    {
        using var repo = GitRepo.Init();
        var sha = Seed(repo);
        Open(repo, sha);
        var host = new FakeLiveHost();
        var live = Live(repo, sha);

        Assert.Throws<ToolArgumentException>(() => ValueTools.SetValues(host, null, new JsonArray(), true, live));
        Assert.Throws<ToolArgumentException>(() => ValueTools.SetValues(host, null, null, true, live));
        Assert.Throws<ToolArgumentException>(() => ValueTools.SetValues(
            host, null, new JsonArray { new JsonObject { ["value"] = 1 } }, true, live));

        Assert.Equal(0, host.CallsTo("SetValues"));
    }

    [Fact]
    public void An_envelope_on_another_definition_does_not_authorise_this_one()
    {
        using var repo = GitRepo.Init();
        var sha = Seed(repo);
        EnvelopeStore.Open(new EditEnvelope(
            repo.Root, "Coding/site.gh", sha, "claude-code", null, "grade the site", DateTimeOffset.Now));

        var r = ValueTools.SetValues(new FakeLiveHost(), null, Edits(("Height", 12)), true, Live(repo, sha));

        Assert.True(r.IsError);
        Assert.Contains("Coding/site.gh", r.Content[0].Text);
    }
}
