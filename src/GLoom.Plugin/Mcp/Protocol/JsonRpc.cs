using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GLoom.Mcp.Protocol;

public enum JsonRpcKind { Request, Notification, Response, Invalid }

/// <summary>
/// One parsed JSON-RPC 2.0 message. Batches are rejected up front: the 2025-06-18 revision
/// removed them, which is why the server advertises no revision older than that - the
/// 2025-03-26 base protocol made batch support a MUST.
/// </summary>
public sealed class JsonRpcMessage
{
    public JsonRpcKind Kind { get; init; }
    public JsonNode? Id { get; init; }
    public string? Method { get; init; }
    public JsonObject? Params { get; init; }
    public string? ParseError { get; init; }

    public static JsonRpcMessage Parse(string body)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch (JsonException ex) { return Invalid("Parse error: " + ex.Message); }

        if (node is JsonArray) return Invalid("Batches are not supported.");
        if (node is not JsonObject obj) return Invalid("A JSON-RPC message must be an object.");
        if (!(obj["jsonrpc"] is JsonValue ver && ver.TryGetValue<string>(out var version) && version == "2.0"))
            return Invalid("Missing or wrong \"jsonrpc\" version.");

        var hasId = obj.ContainsKey("id");
        var id = hasId ? CloneNode(obj["id"]) : null;
        var method = obj["method"] as JsonValue;

        if (method is not null)
        {
            var name = method.TryGetValue<string>(out var s) ? s : null;
            if (string.IsNullOrEmpty(name)) return Invalid("\"method\" must be a string.");
            var parameters = obj["params"] as JsonObject;
            return new JsonRpcMessage
            {
                Kind = hasId ? JsonRpcKind.Request : JsonRpcKind.Notification,
                Id = id,
                Method = name,
                Params = parameters is null ? null : (JsonObject?)CloneNode(parameters),
            };
        }

        if (obj.ContainsKey("result") || obj.ContainsKey("error"))
            return new JsonRpcMessage { Kind = JsonRpcKind.Response, Id = id };

        return Invalid("Neither a request, a notification nor a response.");
    }

    private static JsonRpcMessage Invalid(string why) =>
        new() { Kind = JsonRpcKind.Invalid, ParseError = why };

    // JsonNode.DeepClone arrived in .NET 8; the plugin compiles against .NET 7.
    public static JsonNode? CloneNode(JsonNode? node) =>
        node is null ? null : JsonNode.Parse(node.ToJsonString());
}

public static class JsonRpcErrors
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int Internal = -32603;
}

public static class JsonRpc
{
    public static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = JsonRpcMessage.CloneNode(id),
        ["result"] = result,
    };

    public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonRpcMessage.CloneNode(id),
            ["error"] = error,
        };
    }

    public static readonly JsonSerializerOptions WireOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonNode node) => node.ToJsonString(WireOptions);
}
