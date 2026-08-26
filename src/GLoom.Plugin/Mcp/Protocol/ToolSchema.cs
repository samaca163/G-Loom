using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace GLoom.Mcp.Protocol;

/// <summary>What a tool needs from the host before it may run. Gated at call time by the
/// panel's agent-access mode; every tool is listed regardless so an agent can plan.</summary>
public enum ToolAccess { Read, Write, Destructive }

public sealed record ToolContent(string Type, string? Text = null, string? Data = null, string? MimeType = null)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["type"] = Type };
        if (Text is not null) o["text"] = Text;
        if (Data is not null) o["data"] = Data;
        if (MimeType is not null) o["mimeType"] = MimeType;
        return o;
    }
}

public sealed class ToolResult
{
    public List<ToolContent> Content { get; } = new();
    public bool IsError { get; init; }
    public JsonNode? StructuredContent { get; init; }

    public static ToolResult Text(string text) { var r = new ToolResult(); r.Content.Add(new ToolContent("text", text)); return r; }

    public static ToolResult Error(string text) { var r = new ToolResult { IsError = true }; r.Content.Add(new ToolContent("text", text)); return r; }

    /// <summary>The JSON rides in a text block (every client renders that) and as
    /// structuredContent (clients that understand it get the object).</summary>
    public static ToolResult Json(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, ToolJson.Options)!;
        var r = new ToolResult { StructuredContent = node };
        r.Content.Add(new ToolContent("text", node.ToJsonString(ToolJson.Options)));
        return r;
    }

    public static ToolResult Image(byte[] png)
    {
        var r = new ToolResult();
        r.Content.Add(new ToolContent("image", Data: Convert.ToBase64String(png), MimeType: "image/png"));
        return r;
    }

    public JsonObject ToJson()
    {
        var content = new JsonArray();
        foreach (var c in Content) content.Add(c.ToJson());
        var o = new JsonObject { ["content"] = content };
        if (IsError) o["isError"] = true;
        if (StructuredContent is not null) o["structuredContent"] = JsonRpcMessage.CloneNode(StructuredContent);
        return o;
    }
}

public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public delegate ToolResult ToolHandler(JsonObject arguments, CancellationToken cancellation);

public sealed record McpTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    ToolAccess Access,
    ToolHandler Handler,
    string? Title = null)
{
    public JsonObject Describe()
    {
        var o = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["inputSchema"] = JsonRpcMessage.CloneNode(InputSchema),
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = Access == ToolAccess.Read,
                ["destructiveHint"] = Access == ToolAccess.Destructive,
                ["openWorldHint"] = false,
            },
        };
        if (Title is not null) o["title"] = Title;
        return o;
    }
}

/// <summary>A tiny JSON-Schema builder so tool files read as contracts, not as JSON.</summary>
public sealed class Schema
{
    private readonly JsonObject _props = new();
    private readonly JsonArray _required = new();

    public static Schema Object() => new();

    public Schema String(string name, string description, bool required = false) =>
        Add(name, "string", description, required);

    public Schema Integer(string name, string description, bool required = false, int? min = null, int? max = null)
    {
        var p = Add(name, "integer", description, required);
        var prop = (JsonObject)_props[name]!;
        if (min is not null) prop["minimum"] = min;
        if (max is not null) prop["maximum"] = max;
        return p;
    }

    public Schema Boolean(string name, string description, bool required = false) =>
        Add(name, "boolean", description, required);

    public Schema Enum(string name, string description, IEnumerable<string> values, bool required = false)
    {
        Add(name, "string", description, required);
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        ((JsonObject)_props[name]!)["enum"] = arr;
        return this;
    }

    private Schema Add(string name, string type, string description, bool required)
    {
        _props[name] = new JsonObject { ["type"] = type, ["description"] = description };
        if (required) _required.Add(name);
        return this;
    }

    public JsonObject Build()
    {
        var o = new JsonObject { ["type"] = "object", ["properties"] = _props, ["additionalProperties"] = false };
        if (_required.Count > 0) o["required"] = _required;
        return o;
    }
}

public static class Args
{
    public static string? String(JsonObject a, string name)
    {
        var v = a[name];
        if (v is null) return null;
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        throw new ToolArgumentException($"\"{name}\" must be a string.");
    }

    public static int Int(JsonObject a, string name, int fallback)
    {
        var v = a[name];
        if (v is null) return fallback;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var i)) return i;
            if (jv.TryGetValue<double>(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9) return (int)Math.Round(d);
        }
        throw new ToolArgumentException($"\"{name}\" must be an integer.");
    }

    public static bool Bool(JsonObject a, string name, bool fallback)
    {
        var v = a[name];
        if (v is null) return fallback;
        if (v is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        throw new ToolArgumentException($"\"{name}\" must be a boolean.");
    }
}

public sealed class ToolArgumentException : Exception
{
    public ToolArgumentException(string message) : base(message) { }
}
