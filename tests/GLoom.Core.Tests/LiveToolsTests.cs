using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Live;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class LiveToolsTests : IDisposable
{
    private readonly GitRepo _repo = GitRepo.Init();
    private readonly FakeLiveHost _host;

    public LiveToolsTests() => _host = FakeLiveHost.Canned(_repo);

    public void Dispose() => _repo.Dispose();

    private string TrackedPath => _repo.Full(FakeLiveHost.TrackedGh);

    private static JsonObject S(ToolResult r) => (JsonObject)r.StructuredContent!;

    private static string[] Guids(JsonNode? array) => ((JsonArray)array!).Select(n => n!["instanceGuid"]!.GetValue<string>()).ToArray();

    private static string[] Names(JsonNode? array) => ((JsonArray)array!).Select(n => n!["name"]!.GetValue<string>()).ToArray();

    private McpDispatcher Dispatcher()
    {
        var d = new McpDispatcher("0.3.0", "");
        LiveTools.Register(d, _host);
        return d;
    }

    private static JsonObject Call(McpDispatcher d, string tool, string args, AgentAccess access = AgentAccess.ReadOnly)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(args) },
        }.ToJsonString();
        var r = d.Handle(body, new DispatchContext(access, null, CancellationToken.None));
        Assert.Equal(200, r.HttpStatus);
        return (JsonObject)JsonNode.Parse(r.Body!)!["result"]!;
    }

    private static string FirstText(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    // --- registration ---

    [Fact]
    public void Register_lists_the_seven_live_tools_and_only_solve_writes()
    {
        var names = Dispatcher().Tools.Select(t => t.Name).ToArray();
        foreach (var n in new[] { "gloom_documents", "gloom_read_document", "gloom_read_outputs", "gloom_solve", "gloom_catalogue", "gloom_canvas_image", "gloom_rhino_context" })
            Assert.Contains(n, names);
        Assert.All(Dispatcher().Tools, t => Assert.Equal(t.Name == "gloom_solve" ? ToolAccess.Write : ToolAccess.Read, t.Access));
    }

    // --- gloom_documents ---

    [Fact]
    public void Documents_resolves_the_project_and_leaves_unsaved_documents_without_a_path()
    {
        var r = S(LiveTools.Documents(_host));
        var docs = (JsonArray)r["documents"]!;

        Assert.Equal(2, docs.Count);
        Assert.Equal(TrackedPath, docs[0]!["filePath"]!.GetValue<string>());
        Assert.Equal(_repo.Root, docs[0]!["projectRoot"]!.GetValue<string>());
        Assert.Equal("Coding/tower.gh", docs[0]!["definitionPath"]!.GetValue<string>());
        Assert.True(docs[0]!["isActive"]!.GetValue<bool>());
        Assert.True(docs[0]!["isModified"]!.GetValue<bool>());
        Assert.Equal("PostProcess", docs[0]!["solutionState"]!.GetValue<string>());
        Assert.Equal(1, docs[0]!["errorCount"]!.GetValue<int>());

        Assert.Null(docs[1]!["filePath"]);
        Assert.Null(docs[1]!["projectRoot"]);
        Assert.Null(docs[1]!["definitionPath"]);
        Assert.Equal("Untitled", docs[1]!["displayName"]!.GetValue<string>());
        Assert.False(docs[1]!["isActive"]!.GetValue<bool>());

        Assert.Equal(TrackedPath, r["active"]!.GetValue<string>());
        Assert.Contains("filePath", r["note"]!.GetValue<string>());
    }

    [Fact]
    public void Documents_says_so_when_nothing_is_open()
    {
        _host.DocumentList.Clear();
        var r = S(LiveTools.Documents(_host));
        Assert.Empty((JsonArray)r["documents"]!);
        Assert.Null(r["active"]);
        Assert.Contains("No Grasshopper document is open", r["note"]!.GetValue<string>());
    }

    // --- gloom_read_document ---

    [Fact]
    public void ReadDocument_joins_runtime_by_guid_and_lists_problems_up_front()
    {
        var r = S(LiveTools.ReadDocument(_host, null, 0, 50, null, null, false, 3));
        var objects = (JsonArray)r["objects"]!;

        Assert.Equal(TrackedPath, r["file"]!.GetValue<string>());
        Assert.Equal("tower", r["displayName"]!.GetValue<string>());
        Assert.True(r["isModified"]!.GetValue<bool>());
        Assert.Equal(12.5, r["solutionMs"]!.GetValue<double>());
        Assert.Equal(new[] { Guid(1), Guid(2), Guid(3), Guid(4), Guid(5) }, Guids(objects));

        var circle = objects[1]!;
        Assert.Equal("Circle", circle["name"]!.GetValue<string>());
        Assert.Equal(Guid(20), circle["inputs"]![0]!["instanceGuid"]!.GetValue<string>());
        Assert.Equal("error", circle["runtime"]!["level"]!.GetValue<string>());
        Assert.Equal(2, ((JsonArray)circle["runtime"]!["messages"]!).Count);
        Assert.Equal("Computed", circle["runtime"]!["phase"]!.GetValue<string>());
        Assert.Equal("R", circle["runtime"]!["inputs"]![0]!["nickname"]!.GetValue<string>());
        Assert.Equal("Curve", circle["runtime"]!["outputs"]![0]!["typeName"]!.GetValue<string>());
        Assert.Equal("Grasshopper", circle["runtime"]!["library"]!.GetValue<string>());
        Assert.Equal(5m, objects[0]!["persistent"]!["slider"]!["value"]!.GetValue<decimal>());
        Assert.Equal("5", objects[0]!["runtime"]!["outputs"]![0]!["preview"]![0]!.GetValue<string>());

        Assert.Null(objects[3]!["runtime"]);
        Assert.Equal("Extrude", objects[3]!["name"]!.GetValue<string>());

        var problems = (JsonArray)r["problems"]!;
        Assert.Equal(new[] { FakeLiveHost.ErrorGuid, FakeLiveHost.WarningGuid }, Guids(problems));
        Assert.Equal("error", problems[0]!["level"]!.GetValue<string>());
        Assert.Equal("Radius must be positive", problems[0]!["messages"]![1]!.GetValue<string>());
        Assert.Equal("warning", problems[1]!["level"]!.GetValue<string>());
        Assert.Equal("Loft", problems[1]!["nickname"]!.GetValue<string>());
        Assert.False(r["problemsTruncated"]!.GetValue<bool>());
        Assert.Null(r["filter"]);
        Assert.Contains("gloom_read_version", r["note"]!.GetValue<string>());
    }

    [Fact]
    public void ReadDocument_totals_count_objects_groups_kinds_and_levels()
    {
        var totals = S(LiveTools.ReadDocument(_host, null, 0, 1, null, null, false, 3))["totals"]!;
        Assert.Equal(5, totals["objects"]!.GetValue<int>());
        Assert.Equal(0, totals["groups"]!.GetValue<int>());
        Assert.Equal(3, totals["byKind"]!["component"]!.GetValue<int>());
        Assert.Equal(2, totals["byKind"]!["param"]!.GetValue<int>());
        Assert.Equal(1, totals["errors"]!.GetValue<int>());
        Assert.Equal(1, totals["warnings"]!.GetValue<int>());
        Assert.Equal(1, totals["remarks"]!.GetValue<int>());
    }

    [Fact]
    public void ReadDocument_pages_while_problems_stay_whole()
    {
        var mid = S(LiveTools.ReadDocument(_host, null, 3, 1, null, null, false, 3));
        Assert.Equal(new[] { Guid(4) }, Guids(mid["objects"]));
        Assert.Equal(2, ((JsonArray)mid["problems"]!).Count);
        Assert.Equal(1, mid["page"]!["returned"]!.GetValue<int>());
        Assert.True(mid["page"]!["hasMore"]!.GetValue<bool>());
        Assert.Equal(4, mid["page"]!["nextOffset"]!.GetValue<int>());

        var last = S(LiveTools.ReadDocument(_host, null, 4, 1, null, null, false, 3));
        Assert.Equal(new[] { Guid(5) }, Guids(last["objects"]));
        Assert.False(last["page"]!["hasMore"]!.GetValue<bool>());
        Assert.Null(last["page"]!["nextOffset"]);
        Assert.Equal(2, ((JsonArray)last["problems"]!).Count);
    }

    [Fact]
    public void ReadDocument_filters_by_problems_kind_and_query()
    {
        var problems = S(LiveTools.ReadDocument(_host, null, 0, 50, null, null, true, 3));
        Assert.Equal(new[] { Guid(2), Guid(3) }, Guids(problems["objects"]));
        Assert.True(problems["filter"]!["onlyProblems"]!.GetValue<bool>());
        Assert.Equal(2, problems["filter"]!["matched"]!.GetValue<int>());
        Assert.Equal(5, problems["totals"]!["objects"]!.GetValue<int>());

        var byKind = S(LiveTools.ReadDocument(_host, null, 0, 50, null, "PARAM", false, 3));
        Assert.Equal(new[] { Guid(1), Guid(5) }, Guids(byKind["objects"]));
        Assert.Equal("PARAM", byKind["filter"]!["kind"]!.GetValue<string>());

        var byName = S(LiveTools.ReadDocument(_host, null, 0, 50, "loft", null, false, 3));
        Assert.Equal(new[] { "Loft" }, Names(byName["objects"]));

        var byGuid = S(LiveTools.ReadDocument(_host, null, 0, 50, "000000000004", "component", false, 3));
        Assert.Equal(new[] { "Extrude" }, Names(byGuid["objects"]));

        var both = S(LiveTools.ReadDocument(_host, null, 0, 50, "loft", "param", false, 3));
        Assert.Empty((JsonArray)both["objects"]!);
        Assert.Equal(0, both["filter"]!["matched"]!.GetValue<int>());

        var kind = Assert.Throws<ToolArgumentException>(() => LiveTools.ReadDocument(_host, null, 0, 50, null, "slider", false, 3));
        Assert.Contains("\"param\"", kind.Message);
        Assert.Equal(5, _host.CallsTo("ReadDocument"));
    }

    [Fact]
    public void ReadDocument_reads_the_host_once_and_clamps_limit_and_previewItems()
    {
        var r = S(LiveTools.ReadDocument(_host, "Coding/tower.gh", 0, 999, null, null, false, 50));
        Assert.Equal(1, _host.CallsTo("ReadDocument"));
        Assert.Equal("Coding/tower.gh", _host.ArgsOf("ReadDocument")[0]);
        Assert.Equal(20, _host.ArgsOf("ReadDocument")[1]);
        Assert.Equal(200, r["page"]!["limit"]!.GetValue<int>());

        LiveTools.ReadDocument(_host, null, -5, 0, null, null, false, -1);
        Assert.Equal(0, _host.ArgsOf("ReadDocument")[1]);
    }

    // --- gloom_read_outputs ---

    [Fact]
    public void ReadOutputs_shapes_branches_and_items_and_explains_paths()
    {
        var r = S(LiveTools.ReadOutputs(_host, null, " Circle ", null, 100, 200));

        Assert.Equal(Guid(2), r["object"]!["instanceGuid"]!.GetValue<string>());
        Assert.Equal("component", r["object"]!["kind"]!.GetValue<string>());
        var p = r["params"]![0]!;
        Assert.Equal("C", p["nickname"]!.GetValue<string>());
        Assert.Equal("Curve", p["typeName"]!.GetValue<string>());
        Assert.Equal(3, p["dataCount"]!.GetValue<int>());
        Assert.Equal(2, p["pathCount"]!.GetValue<int>());
        Assert.False(p["truncated"]!.GetValue<bool>());
        var first = p["branches"]![0]!;
        Assert.Equal("{0;0}", first["path"]!.GetValue<string>());
        Assert.Equal(2, first["count"]!.GetValue<int>());
        Assert.Equal("Curve", first["items"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(-1.0, first["items"]![0]!["boundsMin"]![0]!.GetValue<double>());
        Assert.Equal(2.0, first["items"]![1]!["boundsMax"]![1]!.GetValue<double>());
        var second = p["branches"]![1]!;
        Assert.Equal("{0;1}", second["path"]!.GetValue<string>());
        Assert.Equal("3.5", second["items"]![0]!["text"]!.GetValue<string>());
        Assert.Null(second["items"]![0]!["boundsMin"]);
        var note = r["note"]!.GetValue<string>();
        Assert.Contains("{0;1}", note);
        Assert.Contains("bounding box", note);
        Assert.DoesNotContain("Some outputs were truncated", note);

        var args = _host.ArgsOf("ReadOutputs");
        Assert.Equal("Circle", args[1]);
        Assert.Null(args[2]);
        Assert.Equal(100, args[3]);
        Assert.Equal(200, args[4]);
    }

    [Fact]
    public void ReadOutputs_clamps_caps_forwards_param_and_flags_truncation()
    {
        _host.Outputs = _host.Outputs! with { Params = new[] { _host.Outputs!.Params[0] with { Truncated = true } } };
        var r = S(LiveTools.ReadOutputs(_host, TrackedPath, Guid(2), "C", 5000, 99999));
        var args = _host.ArgsOf("ReadOutputs");
        Assert.Equal(TrackedPath, args[0]);
        Assert.Equal("C", args[2]);
        Assert.Equal(2000, args[3]);
        Assert.Equal(4000, args[4]);
        Assert.Contains("Some outputs were truncated", r["note"]!.GetValue<string>());
    }

    [Fact]
    public void ReadOutputs_requires_an_object_and_relays_the_hosts_refusal()
    {
        Assert.Contains("required", Assert.Throws<ToolArgumentException>(
            () => LiveTools.ReadOutputs(_host, null, " ", null, 100, 200)).Message);
        Assert.Equal(0, _host.CallsTo("ReadOutputs"));

        _host.Rejections["ReadOutputs"] = "\"Slider\" is ambiguous: 2 objects match (Slider, Slider).";
        var r = Call(Dispatcher(), "gloom_read_outputs", """{"object":"Slider"}""");
        Assert.True(r["isError"]!.GetValue<bool>());
        Assert.StartsWith("Invalid arguments: \"Slider\" is ambiguous", FirstText(r));
    }

    // --- gloom_solve ---

    [Fact]
    public void Solve_is_refused_in_read_only_mode_and_runs_in_read_write()
    {
        var d = Dispatcher();

        var refused = Call(d, "gloom_solve", "{}", AgentAccess.ReadOnly);
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("read-only", FirstText(refused));
        Assert.Equal(0, _host.CallsTo("Solve"));

        var ran = (JsonObject)Call(d, "gloom_solve", """{"expireAll":true}""", AgentAccess.ReadWrite)["structuredContent"]!;
        Assert.True(ran["ran"]!.GetValue<bool>());
        Assert.False(ran["solverLocked"]!.GetValue<bool>());
        Assert.Equal(42.5, ran["durationMs"]!.GetValue<double>());
        Assert.Equal("PostProcess", ran["solutionState"]!.GetValue<string>());
        Assert.Equal(1, ran["counts"]!["errors"]!.GetValue<int>());
        Assert.Equal(1, ran["counts"]!["warnings"]!.GetValue<int>());
        Assert.Equal("Radius must be positive", ran["errors"]![0]!["messages"]![0]!.GetValue<string>());
        Assert.Equal("Loft", ran["warnings"]![0]!["name"]!.GetValue<string>());
        Assert.Contains("onlyProblems", ran["note"]!.GetValue<string>());

        var args = _host.ArgsOf("Solve");
        Assert.Null(args[0]);
        Assert.Equal(true, args[1]);
        Assert.Equal(TimeSpan.FromSeconds(120), args[2]);
    }

    [Fact]
    public void Solve_clamps_the_timeout_and_reports_a_locked_solver()
    {
        LiveTools.Solve(_host, null, false, 1);
        Assert.Equal(TimeSpan.FromSeconds(5), _host.ArgsOf("Solve")[2]);
        LiveTools.Solve(_host, null, false, 9999);
        Assert.Equal(TimeSpan.FromSeconds(600), _host.ArgsOf("Solve")[2]);

        _host.Report = new SolveReport(false, true, false, 0, "PostProcess", 5, Array.Empty<ObjectProblem>(), Array.Empty<ObjectProblem>());
        var r = S(LiveTools.Solve(_host, null, false, 120));
        Assert.False(r["ran"]!.GetValue<bool>());
        Assert.True(r["solverLocked"]!.GetValue<bool>());
        Assert.Contains("locked", r["note"]!.GetValue<string>());
        Assert.Contains("unlock", r["note"]!.GetValue<string>());

        _host.Report = _host.Report with { Ran = true, SolverLocked = false };
        Assert.Contains("without errors", S(LiveTools.Solve(_host, null, false, 120))["note"]!.GetValue<string>());
    }

    // --- timeouts ---

    [Fact]
    public void Every_tool_maps_a_UI_thread_timeout_to_an_error_the_agent_can_act_on()
    {
        var tools = new (string Method, Func<ToolResult> Run)[]
        {
            ("Documents", () => LiveTools.Documents(_host)),
            ("ReadDocument", () => LiveTools.ReadDocument(_host, null, 0, 50, null, null, false, 3)),
            ("ReadOutputs", () => LiveTools.ReadOutputs(_host, null, "Circle", null, 100, 200)),
            ("Solve", () => LiveTools.Solve(_host, null, false, 120)),
            ("Categories", () => CatalogueTools.Catalogue(_host, null, null, false, 25, null)),
            ("Search", () => CatalogueTools.Catalogue(_host, "circle", null, false, 25, null)),
            ("Describe", () => CatalogueTools.Catalogue(_host, null, null, false, 25, Guid(101))),
            ("CanvasImage", () => CatalogueTools.CanvasImage(_host, null, null, null, null, 1600, 1200)),
            ("Context", () => LiveTools.Context(_host)),
        };
        foreach (var (method, run) in tools)
        {
            Assert.False(run().IsError);
            _host.Timeouts.Add(method);
            var r = run();
            Assert.True(r.IsError, method);
            var text = r.Content[0].Text!;
            Assert.Contains("UI thread did not respond", text);
            Assert.Contains("modal dialog", text);
            if (method == "Solve") Assert.Contains("gloom_documents", text);
            else Assert.DoesNotContain("gloom_documents", text);
            _host.Timeouts.Remove(method);
        }
    }

    // --- gloom_catalogue ---

    [Fact]
    public void Catalogue_lists_categories_without_arguments()
    {
        var r = S(CatalogueTools.Catalogue(_host, null, "  ", false, 25, null));
        var categories = (JsonArray)r["categories"]!;
        Assert.Equal(2, categories.Count);
        Assert.Equal("Params", categories[0]!["category"]!.GetValue<string>());
        Assert.Equal(new[] { "Input", "Primitive" }, ((JsonArray)categories[0]!["subCategories"]!).Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(40, categories[0]!["count"]!.GetValue<int>());
        Assert.Contains("\"query\"", r["note"]!.GetValue<string>());
        Assert.Equal(0, _host.CallsTo("Search"));
    }

    [Fact]
    public void Catalogue_search_sorts_by_score_and_flags_truncation()
    {
        var r = S(CatalogueTools.Catalogue(_host, "circ", null, false, 2, null));
        Assert.Equal("circ", r["query"]!.GetValue<string>());
        Assert.Null(r["category"]);
        Assert.Equal(2, r["returned"]!.GetValue<int>());
        Assert.True(r["truncated"]!.GetValue<bool>());
        Assert.Equal(new[] { "Arc", "Number Slider" }, Names(r["components"]));
        Assert.Equal(0.9, r["components"]![0]!["score"]!.GetValue<double>());
        Assert.Equal(Guid(102), r["components"]![0]!["componentGuid"]!.GetValue<string>());
        Assert.True(r["components"]![0]!["library"]!["isCore"]!.GetValue<bool>());
        Assert.Contains("raise limit", r["note"]!.GetValue<string>());
        var args = _host.ArgsOf("Search");
        Assert.Equal("circ", args[0]);
        Assert.Null(args[1]);
        Assert.Equal(false, args[2]);
        Assert.Equal(3, args[3]);

        var all = S(CatalogueTools.Catalogue(_host, "circ", null, true, 25, null));
        Assert.False(all["truncated"]!.GetValue<bool>());
        Assert.Equal(new[] { "Arc", "Number Slider", "Circle" }, Names(all["components"]));
        Assert.Equal(true, _host.ArgsOf("Search")[2]);
        Assert.Equal(26, _host.ArgsOf("Search")[3]);
    }

    [Fact]
    public void Catalogue_category_listing_sorts_by_category_subcategory_and_name_and_pages()
    {
        var r = S(CatalogueTools.Catalogue(_host, null, "curve", false, 25, null));
        Assert.Equal("curve", r["category"]!.GetValue<string>());
        Assert.Equal(new[] { "Arc", "Circle", "Number Slider" }, Names(r["components"]));
        Assert.Equal(3, r["total"]!.GetValue<int>());
        Assert.False(r["page"]!["hasMore"]!.GetValue<bool>());
        Assert.Null(r["page"]!["nextOffset"]);
        Assert.Null(_host.ArgsOf("Search")[0]);
        Assert.Equal("curve", _host.ArgsOf("Search")[1]);
        Assert.Equal(int.MaxValue, _host.ArgsOf("Search")[3]);

        var first = S(CatalogueTools.Catalogue(_host, null, "curve", false, 2, null));
        Assert.Equal(new[] { "Arc", "Circle" }, Names(first["components"]));
        Assert.True(first["page"]!["hasMore"]!.GetValue<bool>());
        Assert.Equal(2, first["page"]!["nextOffset"]!.GetValue<int>());
        Assert.Contains("offset=2", first["note"]!.GetValue<string>());

        var second = S(CatalogueTools.Catalogue(_host, null, "curve", false, 2, null, offset: 2));
        Assert.Equal(new[] { "Number Slider" }, Names(second["components"]));
        Assert.False(second["page"]!["hasMore"]!.GetValue<bool>());
        Assert.Equal(2, second["page"]!["offset"]!.GetValue<int>());

        _host.SearchResults.Clear();
        Assert.Contains("Nothing matched", S(CatalogueTools.Catalogue(_host, null, "curve", false, 25, null))["note"]!.GetValue<string>());
    }

    [Fact]
    public void Catalogue_describe_returns_one_component_and_refuses_a_bad_guid()
    {
        var r = S(CatalogueTools.Catalogue(_host, "ignored", null, false, 25, Guid(101)));
        Assert.Equal("Circle", r["component"]!["name"]!.GetValue<string>());
        Assert.Equal(Guid(101), r["component"]!["componentGuid"]!.GetValue<string>());
        Assert.Equal(new[] { "Plane", "Radius" }, Names(r["inputs"]));
        Assert.Equal("Number", r["inputs"]![1]!["typeName"]!.GetValue<string>());
        Assert.Equal(new[] { "Circle" }, Names(r["outputs"]));
        Assert.Null(r["paramTypeName"]);
        Assert.Equal("round", r["keywords"]![1]!.GetValue<string>());
        Assert.Contains("defaults", r["note"]!.GetValue<string>());
        Assert.Equal(System.Guid.Parse(Guid(101)), _host.ArgsOf("Describe")[0]);
        Assert.Equal(0, _host.CallsTo("Search"));

        _host.Description = _host.Description! with { ParamTypeName = "Curve" };
        Assert.Contains("parameter, not a component", S(CatalogueTools.Catalogue(_host, null, null, false, 25, Guid(101)))["note"]!.GetValue<string>());

        _host.Description = _host.Description with { Inputs = Array.Empty<ParamDescription>(), InstantiationError = "NullReferenceException: boom" };
        var broken = S(CatalogueTools.Catalogue(_host, null, null, false, 25, Guid(101)));
        Assert.Equal("NullReferenceException: boom", broken["instantiationError"]!.GetValue<string>());
        Assert.Contains("could not be instantiated", broken["note"]!.GetValue<string>());

        var bad = Assert.Throws<ToolArgumentException>(() => CatalogueTools.Catalogue(_host, null, null, false, 25, "not-a-guid"));
        Assert.Contains("componentGuid", bad.Message);

        _host.Rejections["Describe"] = "No installed component has guid " + Guid(9);
        var r2 = Call(Dispatcher(), "gloom_catalogue", $$"""{"describe":"{{Guid(9)}}"}""");
        Assert.True(r2["isError"]!.GetValue<bool>());
        Assert.StartsWith("Invalid arguments: No installed component", FirstText(r2));
    }

    // --- gloom_canvas_image ---

    [Fact]
    public void CanvasImage_returns_a_summary_then_the_png()
    {
        var r = CatalogueTools.CanvasImage(_host, null, null, null, null, 1600, 1200);

        Assert.False(r.IsError);
        Assert.Equal(2, r.Content.Count);
        Assert.Equal("text", r.Content[0].Type);
        var summary = (JsonObject)JsonNode.Parse(r.Content[0].Text!)!;
        Assert.Equal(800, summary["pixelWidth"]!.GetValue<int>());
        Assert.Equal(600, summary["pixelHeight"]!.GetValue<int>());
        Assert.Equal(1.5, summary["zoom"]!.GetValue<double>());
        Assert.Equal(10.0, summary["region"]!["x"]!.GetValue<double>());
        Assert.Equal(200.0, summary["region"]!["height"]!.GetValue<double>());
        Assert.Equal(5, summary["objectsInFrame"]!.GetValue<int>());
        Assert.Equal(7, summary["bytes"]!.GetValue<int>());
        Assert.Equal("visible", summary["mode"]!.GetValue<string>());
        Assert.DoesNotContain("\n", r.Content[0].Text!);

        Assert.Equal("image", r.Content[1].Type);
        Assert.Equal("image/png", r.Content[1].MimeType);
        Assert.Equal(_host.Image!.Png, Convert.FromBase64String(r.Content[1].Data!));

        var wire = r.ToJson();
        Assert.Equal("text", wire["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("image", wire["content"]![1]!["type"]!.GetValue<string>());

        var args = _host.ArgsOf("CanvasImage");
        Assert.Equal(ImageRegion.Visible, args[1]);
        Assert.Null(args[2]);
        Assert.Null(args[3]);
        Assert.Equal(1600, args[4]);
        Assert.Equal(1200, args[5]);
    }

    [Fact]
    public void CanvasImage_validates_the_region_splits_objects_and_clamps_sizes()
    {
        CatalogueTools.CanvasImage(_host, TrackedPath, "Objects", $" {Guid(1)}, {Guid(2)} ,", null, 10, 99999);
        var args = _host.ArgsOf("CanvasImage");
        Assert.Equal(TrackedPath, args[0]);
        Assert.Equal(ImageRegion.Objects, args[1]);
        Assert.Equal(new[] { Guid(1), Guid(2) }, (IReadOnlyList<string>)args[2]!);
        Assert.Equal(200, args[4]);
        Assert.Equal(4000, args[5]);

        CatalogueTools.CanvasImage(_host, null, null, null, "loft", 1600, 1200);
        Assert.Equal(ImageRegion.Objects, _host.ArgsOf("CanvasImage")[1]);
        Assert.Equal("loft", _host.ArgsOf("CanvasImage")[3]);

        Assert.Contains("ignores", Assert.Throws<ToolArgumentException>(
            () => CatalogueTools.CanvasImage(_host, null, "all", null, "loft", 1600, 1200)).Message);
        Assert.Contains("\"objects\"", Assert.Throws<ToolArgumentException>(
            () => CatalogueTools.CanvasImage(_host, null, "objects", " , ", null, 1600, 1200)).Message);
        Assert.Contains("bogus", Assert.Throws<ToolArgumentException>(
            () => CatalogueTools.CanvasImage(_host, null, "bogus", null, null, 1600, 1200)).Message);
        Assert.Equal(2, _host.CallsTo("CanvasImage"));
    }

    // --- gloom_rhino_context ---

    [Fact]
    public void Context_passes_the_host_through()
    {
        var r = S(LiveTools.Context(_host));
        Assert.Equal("8.15.25", r["rhinoVersion"]!.GetValue<string>());
        Assert.Equal("Rhino", r["hostProcess"]!.GetValue<string>());
        Assert.False(r["insideRevit"]!.GetValue<bool>());
        Assert.Null(r["rhinoInsideRevitVersion"]);
        Assert.Equal("0.3.0-mcp.3", r["gloomVersion"]!.GetValue<string>());
        Assert.Equal("Millimeters", r["activeModel"]!["units"]!.GetValue<string>());
        Assert.Equal(0.001, r["activeModel"]!["absoluteTolerance"]!.GetValue<double>());
        Assert.Equal(2, r["openDefinitions"]!.GetValue<int>());
        Assert.Equal(TrackedPath, r["activeDefinition"]!.GetValue<string>());
        Assert.Contains("gloom_documents", r["note"]!.GetValue<string>());

        _host.ContextValue = _host.ContextValue! with { InsideRevit = true, HostProcess = "Revit", RhinoInsideRevitVersion = "1.20" };
        var revit = S(LiveTools.Context(_host));
        Assert.Equal("1.20", revit["rhinoInsideRevitVersion"]!.GetValue<string>());
        Assert.Contains("Revit", revit["note"]!.GetValue<string>());
    }

    // --- argument validation through the dispatcher ---

    [Theory]
    [InlineData("gloom_read_document", """{"kind":"slider"}""")]
    [InlineData("gloom_canvas_image", """{"region":"bogus"}""")]
    [InlineData("gloom_canvas_image", """{"region":"objects"}""")]
    [InlineData("gloom_canvas_image", """{"region":"all","query":"loft"}""")]
    [InlineData("gloom_read_outputs", "{}")]
    [InlineData("gloom_catalogue", """{"describe":"nope"}""")]
    [InlineData("gloom_read_document", """{"limit":"ten"}""")]
    public void Invalid_arguments_are_reported_through_the_dispatcher(string tool, string args)
    {
        var r = Call(Dispatcher(), tool, args);
        Assert.True(r["isError"]!.GetValue<bool>());
        Assert.StartsWith("Invalid arguments: ", FirstText(r));
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public void Read_tools_run_through_the_dispatcher_in_read_only_mode()
    {
        var d = Dispatcher();
        var docs = Call(d, "gloom_documents", "{}");
        Assert.Null(docs["isError"]);
        Assert.Equal(2, ((JsonArray)docs["structuredContent"]!["documents"]!).Count);

        var image = Call(d, "gloom_canvas_image", """{"region":"all","maxWidth":300}""");
        Assert.Equal("image/png", image["content"]![1]!["mimeType"]!.GetValue<string>());
        Assert.Equal(300, _host.ArgsOf("CanvasImage")[4]);

        var doc = Call(d, "gloom_read_document", """{"onlyProblems":true,"previewItems":99}""");
        Assert.Equal(2, ((JsonArray)doc["structuredContent"]!["objects"]!).Count);
        Assert.Equal(20, _host.ArgsOf("ReadDocument")[1]);

        var off = d.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"gloom_documents","arguments":{}}}""",
            new DispatchContext(AgentAccess.Off, null, CancellationToken.None));
        Assert.Contains("switched off", off.Body);
    }
}
