using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Memory;
using GLoom.Serialization;
using GLoom.Vcs;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class ResourcesAndPromptsTests
{
    private const string TowerGh = "Coding/tower.gh";
    private const string TowerJson = "Coding/tower.gloom.json";
    private const string SiteGh = "Coding/site.gh";
    private const string SiteJson = "Coding/site.gloom.json";
    private const string Tower = "gloom://definition/Coding/tower.gh/";

    private static readonly string[] Fixed = { "gloom://status", "gloom://branches", "gloom://tags" };

    private static string Commit(GitRepo repo, string gh, string json, decimal slider, int version)
    {
        var ghPath = repo.Write(gh, $"gh bytes {gh} {version}");
        var name = Path.GetFileNameWithoutExtension(gh);
        GLoomRepository.StageForCommit(repo.Root, CanonicalJson.Write(Doc(name, Slider(Guid(1), slider))), repo.Full(json), ghPath);
        return GLoomRepository.CommitStaged(repo.Root, $"{name} version {version}", $"Gloom-Version: {name}_V{version:D3}", "Tester", "t@example.com")!;
    }

    /// <summary>Two definitions, two versions each; the snapshot names tower as the active document.</summary>
    private static (GitRepo Repo, LiveSnapshot Live) Project()
    {
        var repo = GitRepo.Init();
        Commit(repo, TowerGh, TowerJson, 5, 1);
        Commit(repo, SiteGh, SiteJson, 1, 1);
        Commit(repo, SiteGh, SiteJson, 2, 2);
        var sha = Commit(repo, TowerGh, TowerJson, 9, 2);
        return (repo, new LiveSnapshot(repo.Full(TowerGh), repo.Root, false, sha, true));
    }

    private static ResourceContents Read(GloomResources r, string uri) => r.Read(uri, CancellationToken.None)!;

    private static decimal SliderValue(string json) =>
        CanonicalJson.TryParse(json)!.Objects[0].Persistent!.Slider!.Value;

    // --- the provider on its own ---

    [Fact]
    public void List_names_the_project_resources_and_three_per_definition()
    {
        var (repo, live) = Project();
        using var _ = repo;
        var provider = new GloomResources(() => live);

        var uris = provider.List().Select(r => r.Uri).ToArray();

        foreach (var uri in Fixed) Assert.Contains(uri, uris);
        foreach (var def in new[] { Tower, "gloom://definition/Coding/site.gh/" })
            foreach (var leaf in new[] { "record", "recipe", "changes" })
                Assert.Contains(def + leaf, uris);
        Assert.Equal(9, uris.Length);

        var record = provider.List().Single(r => r.Uri == Tower + "record");
        Assert.Equal("Coding/tower.gh/record", record.Name);
        Assert.Equal("tower.gh — decision record", record.Title);
        Assert.Equal("text/markdown", record.MimeType);
        Assert.Equal("application/json", provider.List().Single(r => r.Uri == Tower + "recipe").MimeType);

        var templates = provider.Templates();
        Assert.Equal(3, templates.Count);
        Assert.Contains(templates, t => t.UriTemplate == "gloom://definition/{+path}/recipe@{+version}");
        Assert.Contains(templates, t => t.UriTemplate == "gloom://definition/{+path}/changes@{+from}..{+to}");
        Assert.Contains(templates, t => t.UriTemplate == "gloom://definition/{+path}/record");
    }

    [Fact]
    public void Each_concrete_resource_reads_with_its_mime_type_and_content()
    {
        var (repo, live) = Project();
        using var _ = repo;
        var provider = new GloomResources(() => live);

        var status = Read(provider, "gloom://status");
        Assert.Equal("application/json", status.MimeType);
        Assert.Equal("Coding/tower.gh", JsonNode.Parse(status.Text)!["definitionPath"]!.GetValue<string>());
        Assert.Equal("main", JsonNode.Parse(Read(provider, "gloom://branches").Text)!["current"]!.GetValue<string>());
        Assert.Equal(0, JsonNode.Parse(Read(provider, "gloom://tags").Text)!["returned"]!.GetValue<int>());

        var record = Read(provider, Tower + "record");
        Assert.Equal("text/markdown", record.MimeType);
        Assert.Contains("V002", record.Text);
        Assert.Contains("V001", record.Text);

        var recipe = Read(provider, Tower + "recipe");
        Assert.Equal("application/json", recipe.MimeType);
        Assert.Equal(9m, SliderValue(recipe.Text));

        var changes = Read(provider, Tower + "changes");
        Assert.Equal("application/json", changes.MimeType);
        Assert.Contains("isEmpty", changes.Text);
        Assert.True(JsonNode.Parse(changes.Text)!["isEmpty"]!.GetValue<bool>());

        var site = Read(provider, "gloom://definition/Coding/site.gh/recipe");
        Assert.Equal(2m, SliderValue(site.Text));
        Assert.Equal("gloom://definition/Coding/site.gh/recipe", site.Uri);
    }

    [Fact]
    public void Templated_uris_resolve_a_version_and_a_range()
    {
        var (repo, live) = Project();
        using var _ = repo;
        var provider = new GloomResources(() => live);

        Assert.Equal(5m, SliderValue(Read(provider, Tower + "recipe@V001").Text));
        Assert.Equal(9m, SliderValue(Read(provider, Tower + "recipe@tower_V002").Text));
        Assert.Equal(9m, SliderValue(Read(provider, Tower + "recipe@working").Text));
        Assert.Equal(5m, SliderValue(Read(provider, Tower + "recipe@HEAD~1").Text));

        var range = JsonNode.Parse(Read(provider, Tower + "changes@V001..V002").Text)!;
        Assert.Equal("V001", range["from"]!["label"]!.GetValue<string>());
        Assert.Equal("V002", range["to"]!["label"]!.GetValue<string>());
        Assert.False(range["isEmpty"]!.GetValue<bool>());
        Assert.Contains("slider 5 → 9", range["modified"]![0]!["summary"]!.GetValue<string>());

        var toWorking = JsonNode.Parse(Read(provider, Tower + "changes@V001").Text)!;
        Assert.True(toWorking["to"]!["isWorkingTree"]!.GetValue<bool>());

        var escaped = Read(provider, "gloom://definition/Coding%2Ftower.gh/recipe@V001");
        Assert.Equal(5m, SliderValue(escaped.Text));
    }

    [Fact]
    public void Unknown_shapes_are_refused_with_the_list_of_shapes_and_foreign_schemes_are_not_ours()
    {
        var (repo, live) = Project();
        using var _ = repo;
        var provider = new GloomResources(() => live);

        foreach (var uri in new[]
        {
            "gloom://nope", "gloom://definition/", "gloom://definition/record", Tower + "history",
            Tower + "record@V001", Tower + "recipe@", Tower + "changes@V001..", Tower + "changes@..V002",
        })
        {
            var ex = Assert.Throws<ToolArgumentException>(() => provider.Read(uri, CancellationToken.None));
            Assert.Contains("gloom://definition/<path>/recipe@<version>", ex.Message);
        }

        var missing = Assert.Throws<ToolArgumentException>(() => provider.Read(Tower + "recipe@V009", CancellationToken.None));
        Assert.Contains("gloom_history", missing.Message);

        Assert.Null(provider.Read("file:///C:/tower.gh", CancellationToken.None));
        Assert.Null(provider.Read("https://example.com/gloom://status", CancellationToken.None));
    }

    [Fact]
    public void Without_an_active_document_nothing_is_listed_and_every_read_says_why()
    {
        var provider = new GloomResources(() => null);
        Assert.Empty(provider.List());
        Assert.Equal(3, provider.Templates().Count);
        foreach (var uri in new[] { "gloom://status", Tower + "record", Tower + "recipe@V001" })
            Assert.Contains("No active Grasshopper document", Assert.Throws<ToolArgumentException>(() => provider.Read(uri, CancellationToken.None)).Message);
        Assert.Null(provider.Read("file:///x", CancellationToken.None));
    }

    [Fact]
    public void A_version_may_hold_slashes_and_a_definition_a_non_ascii_name()
    {
        var (repo, live) = Project();
        using var _ = repo;
        GitRepo.Git(repo.Root, "branch", "option/a", "HEAD~3");
        const string umlautGh = "Coding/Übersicht tower.gh";
        Commit(repo, umlautGh, "Coding/Übersicht tower.gloom.json", 3, 1);
        var provider = new GloomResources(() => live);

        Assert.Equal(5m, SliderValue(Read(provider, Tower + "recipe@option/a").Text));
        var range = JsonNode.Parse(Read(provider, Tower + "changes@option/a..V002").Text)!;
        Assert.Equal("option/a", range["from"]!["reference"]!.GetValue<string>());
        Assert.Contains("slider 5 → 9", range["modified"]![0]!["summary"]!.GetValue<string>());

        const string umlaut = "gloom://definition/Coding/%C3%9Cbersicht%20tower.gh/";
        Assert.Contains(umlaut + "recipe", provider.List().Select(r => r.Uri));
        Assert.Equal(3m, SliderValue(Read(provider, umlaut + "recipe").Text));
        Assert.Equal(3m, SliderValue(Read(provider, "gloom://definition/" + umlautGh + "/recipe@V001").Text));
    }

    // --- through the dispatcher ---

    private static McpDispatcher Dispatcher(LiveSnapshot? live)
    {
        var d = new McpDispatcher("0.3.0", "x");
        d.Register(new GloomResources(() => live));
        GloomPrompts.Register(d, () => live);
        return d;
    }

    private static JsonObject Call(McpDispatcher d, string json, AgentAccess access = AgentAccess.ReadOnly)
    {
        var r = d.Handle(json, new DispatchContext(access, null, CancellationToken.None));
        Assert.Equal(200, r.HttpStatus);
        return (JsonObject)JsonNode.Parse(r.Body!)!;
    }

    private static string PromptText(JsonObject response) =>
        response["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>();

    [Fact]
    public void Resources_list_and_read_through_the_protocol()
    {
        var (repo, live) = Project();
        using var _ = repo;
        var d = Dispatcher(live);

        var listed = (JsonArray)Call(d, """{"jsonrpc":"2.0","id":1,"method":"resources/list"}""")["result"]!["resources"]!;
        var uris = listed.Select(r => r!["uri"]!.GetValue<string>()).ToArray();
        Assert.Contains("gloom://status", uris);
        Assert.Contains(Tower + "changes", uris);
        var recipe = listed.Single(r => r!["uri"]!.GetValue<string>() == Tower + "recipe")!;
        Assert.Equal("Coding/tower.gh/recipe", recipe["name"]!.GetValue<string>());
        Assert.Equal("tower.gh — recipe (working tree)", recipe["title"]!.GetValue<string>());

        var templates = (JsonArray)Call(d, """{"jsonrpc":"2.0","id":2,"method":"resources/templates/list"}""")["result"]!["resourceTemplates"]!;
        Assert.Equal(3, templates.Count);

        var read = Call(d, """{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"gloom://definition/Coding/tower.gh/record"}}""");
        var contents = read["result"]!["contents"]![0]!;
        Assert.Equal(Tower + "record", contents["uri"]!.GetValue<string>());
        Assert.Equal("text/markdown", contents["mimeType"]!.GetValue<string>());
        Assert.Contains("V002", contents["text"]!.GetValue<string>());

        var foreign = Call(d, """{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"file:///nope.gh"}}""");
        Assert.Equal(JsonRpcErrors.ResourceNotFound, foreign["error"]!["code"]!.GetValue<int>());

        var shape = Call(d, """{"jsonrpc":"2.0","id":5,"method":"resources/read","params":{"uri":"gloom://definition/Coding/tower.gh/nope"}}""");
        Assert.Equal(JsonRpcErrors.ResourceNotFound, shape["error"]!["code"]!.GetValue<int>());
        Assert.Contains("not a G-Loom resource", shape["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Prompts_list_both_with_their_arguments()
    {
        var prompts = (JsonArray)Call(Dispatcher(null), """{"jsonrpc":"2.0","id":1,"method":"prompts/list"}""")["result"]!["prompts"]!;
        var byName = prompts.ToDictionary(p => p!["name"]!.GetValue<string>(), p => (JsonObject)p!);
        Assert.Equal(new[] { "design-history", "review-changes" }, byName.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var review = byName["review-changes"];
        Assert.Equal("Review what changed in a definition", review["title"]!.GetValue<string>());
        var reviewArgs = ((JsonArray)review["arguments"]!).Select(a => a!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "file", "from", "to" }, reviewArgs);
        Assert.All((JsonArray)review["arguments"]!, a => Assert.False(a!["required"]!.GetValue<bool>()));

        var history = byName["design-history"];
        Assert.Equal("Tell the story of a definition", history["title"]!.GetValue<string>());
        Assert.Equal(new[] { "file", "limit" }, ((JsonArray)history["arguments"]!).Select(a => a!["name"]!.GetValue<string>()).ToArray());
        Assert.Contains("V012", ((JsonArray)review["arguments"]!)[1]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void Review_changes_embeds_the_diff_and_the_reviewer_instructions()
    {
        var (repo, live) = Project();
        using var _ = repo;
        repo.Write(TowerJson, CanonicalJson.Write(Doc("tower", Slider(Guid(1), 42))));
        var d = Dispatcher(live);

        var r = Call(d, """{"jsonrpc":"2.0","id":1,"method":"prompts/get","params":{"name":"review-changes","arguments":{}}}""");
        var text = PromptText(r);
        Assert.Contains("design reviewer", text);
        Assert.Contains("must NOT modify", text);
        Assert.Contains("\"isEmpty\": false", text);
        Assert.Contains("slider 9 → 42", text);
        Assert.Contains("\"definitionPath\": \"Coding/tower.gh\"", text);
        Assert.Contains("Uncommitted changes on disk", text);
        Assert.Contains("last committed version", r["result"]!["description"]!.GetValue<string>());

        var ranged = Call(d, """{"jsonrpc":"2.0","id":2,"method":"prompts/get","params":{"name":"review-changes","arguments":{"from":"V001","to":"V002"}}}""");
        var rangedText = PromptText(ranged);
        Assert.Contains("slider 5 → 9", rangedText);
        Assert.Contains("## tower V002", rangedText);
        Assert.Contains("from V001 to V002", ranged["result"]!["description"]!.GetValue<string>());

        var bad = Call(d, """{"jsonrpc":"2.0","id":3,"method":"prompts/get","params":{"name":"review-changes","arguments":{"from":"nope"}}}""");
        Assert.Equal(JsonRpcErrors.InvalidParams, bad["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Design_history_embeds_the_record_with_changes_oldest_first()
    {
        var (repo, live) = Project();
        using var _ = repo;
        var d = Dispatcher(live);

        var r = Call(d, """{"jsonrpc":"2.0","id":1,"method":"prompts/get","params":{"name":"design-history","arguments":{"limit":"10"}}}""");
        var text = PromptText(r);
        Assert.Contains("V002", text);
        Assert.Contains("turning points", text);
        Assert.Contains("tower_V012", text);
        Assert.Contains("slider 5 → 9", text);
        Assert.True(text.IndexOf("V001", StringComparison.Ordinal) < text.IndexOf("V002", StringComparison.Ordinal));

        var other = PromptText(Call(d, """{"jsonrpc":"2.0","id":2,"method":"prompts/get","params":{"name":"design-history","arguments":{"file":"Coding/site.gh"}}}"""));
        Assert.Contains("site.gh", other);
        Assert.Contains("slider 1 → 2", other);

        var bad = Call(d, """{"jsonrpc":"2.0","id":3,"method":"prompts/get","params":{"name":"design-history","arguments":{"limit":"lots"}}}""");
        Assert.Equal(JsonRpcErrors.InvalidParams, bad["error"]!["code"]!.GetValue<int>());
        Assert.Contains("limit", bad["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Access_off_refuses_resource_reads_and_prompts()
    {
        var (repo, live) = Project();
        using var _ = repo;
        var d = Dispatcher(live);

        var read = Call(d, """{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"gloom://status"}}""", AgentAccess.Off);
        Assert.Contains("switched off", read["error"]!["message"]!.GetValue<string>());
        Assert.Equal(JsonRpcErrors.AccessDenied, read["error"]!["code"]!.GetValue<int>());
        var prompt = Call(d, """{"jsonrpc":"2.0","id":2,"method":"prompts/get","params":{"name":"design-history"}}""", AgentAccess.Off);
        Assert.Contains("switched off", prompt["error"]!["message"]!.GetValue<string>());
        Assert.Equal(JsonRpcErrors.AccessDenied, prompt["error"]!["code"]!.GetValue<int>());

        var none = Call(Dispatcher(null), """{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"gloom://status"}}""");
        Assert.Equal(JsonRpcErrors.ResourceNotFound, none["error"]!["code"]!.GetValue<int>());
        Assert.Contains("No active Grasshopper document", none["error"]!["message"]!.GetValue<string>());
    }
}
