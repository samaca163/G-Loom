using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using Xunit;

namespace GLoom.Core.Tests;

public class DispatcherTests
{
    private static McpDispatcher NewDispatcher()
    {
        var d = new McpDispatcher("0.3.0", "hello agent");
        d.Register(new McpTool("gloom_echo", "Echoes.",
            Schema.Object().String("text", "what to echo", required: true).Integer("times", "repeat", min: 1).Build(),
            ToolAccess.Read,
            (a, _) => ToolResult.Text(string.Concat(Enumerable.Repeat(Args.String(a, "text")!, Args.Int(a, "times", 1))))));
        d.Register(new McpTool("gloom_write", "Writes.", Schema.Object().Build(), ToolAccess.Write,
            (_, _) => ToolResult.Json(new { ok = true })));
        d.Register(new McpTool("gloom_boom", "Throws.", Schema.Object().Build(), ToolAccess.Read,
            (_, _) => throw new InvalidOperationException("kaboom")));
        return d;
    }

    private static DispatchContext Ctx(AgentAccess access = AgentAccess.ReadOnly, string? version = null) =>
        new(access, version, CancellationToken.None);

    private static JsonObject Call(McpDispatcher d, string json, AgentAccess access = AgentAccess.ReadOnly, int expectStatus = 200)
    {
        var r = d.Handle(json, Ctx(access));
        Assert.Equal(expectStatus, r.HttpStatus);
        return (JsonObject)JsonNode.Parse(r.Body!)!;
    }

    [Fact]
    public void Initialize_negotiates_a_supported_version_and_names_the_server()
    {
        var d = NewDispatcher();
        string? seen = null;
        d.ClientInitialized += (n, v) => seen = $"{n}/{v}";

        var r = Call(d, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1"}}}""");

        var result = (JsonObject)r["result"]!;
        Assert.Equal(1, r["id"]!.GetValue<int>());
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("gloom", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("hello agent", result["instructions"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Equal("claude-code/2.1", seen);
    }

    [Fact]
    public void An_unknown_client_version_falls_back_to_the_newest_supported_one()
    {
        var r = Call(NewDispatcher(), """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"2099-01-01"}}""");
        Assert.Equal("2025-11-25", r["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("a", r["id"]!.GetValue<string>());
    }

    [Fact]
    public void An_unsupported_protocol_header_is_a_400()
    {
        var r = NewDispatcher().Handle("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Ctx(version: "2026-07-28"));
        Assert.Equal(400, r.HttpStatus);
        Assert.Contains("Unsupported MCP-Protocol-Version", r.Body);
    }

    [Fact]
    public void Notifications_and_responses_are_accepted_without_a_body()
    {
        var d = NewDispatcher();
        Assert.Equal(202, d.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", Ctx()).HttpStatus);
        Assert.Equal(202, d.Handle("""{"jsonrpc":"2.0","id":5,"result":{}}""", Ctx()).HttpStatus);
    }

    [Fact]
    public void Ping_and_the_empty_list_endpoints_answer()
    {
        var d = NewDispatcher();
        Assert.Empty((JsonObject)Call(d, """{"jsonrpc":"2.0","id":1,"method":"ping"}""")["result"]!);
        Assert.Empty((JsonArray)Call(d, """{"jsonrpc":"2.0","id":2,"method":"resources/list"}""")["result"]!["resources"]!);
        Assert.Empty((JsonArray)Call(d, """{"jsonrpc":"2.0","id":3,"method":"prompts/list"}""")["result"]!["prompts"]!);
    }

    [Fact]
    public void Tools_list_is_sorted_and_carries_schemas_and_annotations()
    {
        var r = Call(NewDispatcher(), """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = (JsonArray)r["result"]!["tools"]!;
        Assert.Equal(new[] { "gloom_boom", "gloom_echo", "gloom_write" }, tools.Select(t => t!["name"]!.GetValue<string>()));
        var echo = tools[1]!;
        Assert.Equal("object", echo["inputSchema"]!["type"]!.GetValue<string>());
        Assert.Equal("text", ((JsonArray)echo["inputSchema"]!["required"]!)[0]!.GetValue<string>());
        Assert.Equal(1, echo["inputSchema"]!["properties"]!["times"]!["minimum"]!.GetValue<int>());
        Assert.True(echo["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.False(tools[2]!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
    }

    [Fact]
    public void Tools_call_runs_the_handler_with_its_arguments()
    {
        var r = Call(NewDispatcher(), """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"gloom_echo","arguments":{"text":"ab","times":2}}}""");
        var result = r["result"]!;
        Assert.Equal("abab", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Null(result["isError"]);
    }

    [Fact]
    public void Structured_results_ride_in_both_text_and_structuredContent()
    {
        var r = Call(NewDispatcher(), """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"gloom_write"}}""", AgentAccess.ReadWrite);
        Assert.True(r["result"]!["structuredContent"]!["ok"]!.GetValue<bool>());
        Assert.Contains("\"ok\": true", r["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("gloom_echo", AgentAccess.Off, "switched off")]
    [InlineData("gloom_write", AgentAccess.ReadOnly, "read-only")]
    public void Access_is_gated_at_call_time_as_a_tool_error(string tool, AgentAccess access, string expected)
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"TOOL","arguments":{"text":"x"}}}""".Replace("TOOL", tool);
        var r = Call(NewDispatcher(), request, access);
        Assert.True(r["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains(expected, r["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Handler_failures_and_bad_arguments_are_tool_errors_not_protocol_errors()
    {
        var d = NewDispatcher();
        var boom = Call(d, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"gloom_boom"}}""");
        Assert.True(boom["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("kaboom", boom["result"]!["content"]![0]!["text"]!.GetValue<string>());

        var bad = Call(d, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"gloom_echo","arguments":{"text":"x","times":"lots"}}}""");
        Assert.Contains("must be an integer", bad["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Non_string_params_are_invalid_params_not_internal_errors()
    {
        var d = NewDispatcher();
        foreach (var request in new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""",
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"clientInfo":{"name":{}}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":5}}""",
        })
        {
            var r = Call(d, request);
            Assert.True(r["error"] is not null, $"expected an error for {request} but got {r.ToJsonString()}");
            Assert.Equal(JsonRpcErrors.InvalidParams, r["error"]!["code"]!.GetValue<int>());
        }
    }

    [Fact]
    public void Protocol_level_mistakes_are_json_rpc_errors()
    {
        var d = NewDispatcher();
        Assert.Equal(JsonRpcErrors.InvalidParams, Call(d, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"nope"}}""")["error"]!["code"]!.GetValue<int>());
        Assert.Equal(JsonRpcErrors.MethodNotFound, Call(d, """{"jsonrpc":"2.0","id":2,"method":"server/discover"}""")["error"]!["code"]!.GetValue<int>());
        Assert.Equal(JsonRpcErrors.ParseError, Call(d, "{not json", expectStatus: 400)["error"]!["code"]!.GetValue<int>());
        Assert.Equal(JsonRpcErrors.InvalidRequest, Call(d, """[{"jsonrpc":"2.0","id":1,"method":"ping"}]""", expectStatus: 400)["error"]!["code"]!.GetValue<int>());
        Assert.Equal(JsonRpcErrors.InvalidRequest, Call(d, """{"id":1,"method":"ping"}""", expectStatus: 400)["error"]!["code"]!.GetValue<int>());
    }
}
