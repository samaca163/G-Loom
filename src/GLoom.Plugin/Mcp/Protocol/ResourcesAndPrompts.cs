using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;

namespace GLoom.Mcp.Protocol;

/// <summary>A readable thing with a stable URI - what a client offers as an @-mention.</summary>
public sealed record McpResource(
    string Uri,
    string Name,
    string? Title = null,
    string? Description = null,
    string? MimeType = null)
{
    public JsonObject Describe()
    {
        var o = new JsonObject { ["uri"] = Uri, ["name"] = Name };
        if (Title is not null) o["title"] = Title;
        if (Description is not null) o["description"] = Description;
        if (MimeType is not null) o["mimeType"] = MimeType;
        return o;
    }
}

/// <summary>An RFC 6570 URI template for resources that exist per file or per version.</summary>
public sealed record McpResourceTemplate(
    string UriTemplate,
    string Name,
    string? Title = null,
    string? Description = null,
    string? MimeType = null)
{
    public JsonObject Describe()
    {
        var o = new JsonObject { ["uriTemplate"] = UriTemplate, ["name"] = Name };
        if (Title is not null) o["title"] = Title;
        if (Description is not null) o["description"] = Description;
        if (MimeType is not null) o["mimeType"] = MimeType;
        return o;
    }
}

public sealed record ResourceContents(string Uri, string MimeType, string Text)
{
    public JsonObject ToJson() => new() { ["uri"] = Uri, ["mimeType"] = MimeType, ["text"] = Text };
}

/// <summary>
/// One family of resources. <see cref="Read"/> returns null for a URI that is not this
/// provider's, so several providers can share the dispatcher; a URI the provider owns but
/// cannot serve is a <see cref="ToolArgumentException"/> with the reason.
/// </summary>
public interface IMcpResourceProvider
{
    IReadOnlyList<McpResource> List();
    IReadOnlyList<McpResourceTemplate> Templates();
    ResourceContents? Read(string uri, CancellationToken cancellation);
}

public sealed record PromptArgument(string Name, string Description, bool Required = false)
{
    public JsonObject Describe() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["required"] = Required,
    };
}

public sealed record PromptMessage(string Role, string Text)
{
    public JsonObject ToJson() => new()
    {
        ["role"] = Role,
        ["content"] = new JsonObject { ["type"] = "text", ["text"] = Text },
    };

    public static PromptMessage User(string text) => new("user", text);
}

public sealed record PromptResult(string? Description, IReadOnlyList<PromptMessage> Messages)
{
    public JsonObject ToJson()
    {
        var messages = new JsonArray();
        foreach (var m in Messages) messages.Add(m.ToJson());
        var o = new JsonObject { ["messages"] = messages };
        if (Description is not null) o["description"] = Description;
        return o;
    }
}

public delegate PromptResult PromptHandler(IReadOnlyDictionary<string, string> arguments, CancellationToken cancellation);

/// <summary>A server-templated conversation opener: the server gathers the facts, the
/// client's model does the reasoning.</summary>
public sealed record McpPrompt(
    string Name,
    string Description,
    IReadOnlyList<PromptArgument> Arguments,
    PromptHandler Handler,
    string? Title = null)
{
    public JsonObject Describe()
    {
        var args = new JsonArray();
        foreach (var a in Arguments) args.Add(a.Describe());
        var o = new JsonObject { ["name"] = Name, ["description"] = Description, ["arguments"] = args };
        if (Title is not null) o["title"] = Title;
        return o;
    }
}
