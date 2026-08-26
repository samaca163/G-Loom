using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace GLoom.Mcp.Protocol;

public enum AgentAccess { Off, ReadOnly, ReadWrite }

/// <summary>Everything the transport knows per request that the protocol layer needs.</summary>
public sealed record DispatchContext(
    AgentAccess Access,
    string? ProtocolVersionHeader,
    CancellationToken Cancellation);

/// <summary>The HTTP-shaped outcome of one JSON-RPC message: a status, an optional body.</summary>
public sealed record DispatchResult(int HttpStatus, string? Body)
{
    public static DispatchResult Accepted => new(202, null);
    public static DispatchResult Json(int status, JsonNode node) => new(status, JsonRpc.Serialize(node));
}

/// <summary>
/// The MCP method surface for the 2025-06-18 and 2025-11-25 revisions - the initialize
/// handshake every current client still speaks. Pure: no HTTP, no Rhino. The transport hands in one message
/// and gets back one status + body; tool handlers are the only thing that touches a host.
/// </summary>
public sealed class McpDispatcher
{
    public const string ServerName = "gloom";
    public static readonly string[] SupportedProtocolVersions = { "2025-11-25", "2025-06-18" };

    private readonly SortedDictionary<string, McpTool> _tools = new(StringComparer.Ordinal);
    private readonly string _serverVersion;
    private readonly string _instructions;
    private readonly SemaphoreSlim _oneCallAtATime = new(1, 1);

    public event Action<string, string>? ClientInitialized;

    public McpDispatcher(string serverVersion, string instructions)
    {
        _serverVersion = serverVersion;
        _instructions = instructions;
    }

    public void Register(McpTool tool) => _tools[tool.Name] = tool;
    public IReadOnlyCollection<McpTool> Tools => _tools.Values;

    public DispatchResult Handle(string body, DispatchContext ctx)
    {
        if (ctx.ProtocolVersionHeader is { } v && !SupportedProtocolVersions.Contains(v))
            return DispatchResult.Json(400, JsonRpc.Error(null, JsonRpcErrors.InvalidRequest,
                $"Unsupported MCP-Protocol-Version \"{v}\"; this server speaks {string.Join(", ", SupportedProtocolVersions)}."));

        var msg = JsonRpcMessage.Parse(body);
        switch (msg.Kind)
        {
            case JsonRpcKind.Invalid:
                var code = msg.ParseError!.StartsWith("Parse error", StringComparison.Ordinal)
                    ? JsonRpcErrors.ParseError : JsonRpcErrors.InvalidRequest;
                return DispatchResult.Json(400, JsonRpc.Error(null, code, msg.ParseError!));
            case JsonRpcKind.Response:
            case JsonRpcKind.Notification:
                // The only server-initiated requests would be sampling/elicitation, which this
                // server never sends, so responses have nothing to correlate with. Notifications
                // (initialized, cancelled, progress) need no work either.
                return DispatchResult.Accepted;
        }

        try
        {
            var result = msg.Method switch
            {
                "initialize" => Initialize(msg.Params),
                "ping" => new JsonObject(),
                "tools/list" => ToolsList(),
                "tools/call" => ToolsCall(msg.Params, ctx),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                "resources/read" => throw new McpError(JsonRpcErrors.InvalidParams, "Resource not found."),
                "prompts/get" => throw new McpError(JsonRpcErrors.InvalidParams, "Prompt not found."),
                _ => throw new McpError(JsonRpcErrors.MethodNotFound, $"Method not found: {msg.Method}"),
            };
            return DispatchResult.Json(200, JsonRpc.Result(msg.Id, result));
        }
        catch (McpError e)
        {
            return DispatchResult.Json(200, JsonRpc.Error(msg.Id, e.Code, e.Message));
        }
        catch (Exception e)
        {
            return DispatchResult.Json(200, JsonRpc.Error(msg.Id, JsonRpcErrors.Internal, e.Message));
        }
    }

    private JsonObject Initialize(JsonObject? p)
    {
        var requested = OptionalString(p?["protocolVersion"], "protocolVersion");
        var negotiated = requested is not null && SupportedProtocolVersions.Contains(requested)
            ? requested : SupportedProtocolVersions[0];

        // Validated before the event: a null-conditional invoke would skip the arguments.
        var client = p?["clientInfo"] as JsonObject;
        var clientName = OptionalString(client?["name"], "clientInfo.name") ?? "unknown client";
        var clientVersion = OptionalString(client?["version"], "clientInfo.version") ?? "";
        ClientInitialized?.Invoke(clientName, clientVersion);

        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "G-Loom", ["version"] = _serverVersion },
            ["instructions"] = _instructions,
        };
    }

    private JsonObject ToolsList()
    {
        var arr = new JsonArray();
        foreach (var t in _tools.Values) arr.Add(t.Describe());
        return new JsonObject { ["tools"] = arr };
    }

    private JsonObject ToolsCall(JsonObject? p, DispatchContext ctx)
    {
        var name = OptionalString(p?["name"], "name")
            ?? throw new McpError(JsonRpcErrors.InvalidParams, "\"name\" is required.");
        if (!_tools.TryGetValue(name, out var tool))
            throw new McpError(JsonRpcErrors.InvalidParams, $"Unknown tool: {name}");

        var args = p!["arguments"] as JsonObject ?? new JsonObject();

        var denied = AccessDenied(tool.Access, ctx.Access);
        if (denied is not null) return ToolResult.Error(denied).ToJson();

        // Tool failures are results, not protocol errors: the model is meant to read them.
        ToolResult result;
        _oneCallAtATime.Wait(ctx.Cancellation);
        try
        {
            result = tool.Handler(args, ctx.Cancellation);
        }
        catch (ToolArgumentException e) { result = ToolResult.Error("Invalid arguments: " + e.Message); }
        catch (OperationCanceledException) { result = ToolResult.Error("Cancelled."); }
        catch (Exception e) { result = ToolResult.Error($"{tool.Name} failed: {e.Message}"); }
        finally { _oneCallAtATime.Release(); }
        return result.ToJson();
    }

    private static string? OptionalString(JsonNode? node, string field) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw new McpError(JsonRpcErrors.InvalidParams, $"\"{field}\" must be a string."),
    };

    public static string? AccessDenied(ToolAccess needs, AgentAccess granted) => (needs, granted) switch
    {
        (_, AgentAccess.Off) => "Agent access is switched off in the G-Loom panel.",
        (ToolAccess.Read, _) => null,
        (_, AgentAccess.ReadOnly) => "Agent access is read-only; switch it to read-write in the G-Loom panel to use this tool.",
        _ => null,
    };
}

public sealed class McpError : Exception
{
    public int Code { get; }
    public McpError(int code, string message) : base(message) => Code = code;
}
