using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;

namespace GLoom.Mcp.State;

public static class McpSettings
{
    public const int DefaultPort = 27180;

    public static AgentAccess LoadAccess()
    {
        try
        {
            if (!File.Exists(McpPaths.SettingsFile)) return AgentAccess.Off;
            var node = JsonNode.Parse(File.ReadAllText(McpPaths.SettingsFile)) as JsonObject;
            return ParseAccess(node?["access"]?.GetValue<string>());
        }
        catch
        {
            return AgentAccess.Off;
        }
    }

    public static void SaveAccess(AgentAccess access)
    {
        McpPaths.Ensure();
        var node = new JsonObject { ["access"] = Label(access) };
        File.WriteAllText(McpPaths.SettingsFile, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Label(AgentAccess a) => a switch
    {
        AgentAccess.ReadOnly => "read-only",
        AgentAccess.ReadWrite => "read-write",
        _ => "off",
    };

    public static AgentAccess ParseAccess(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "read-only" or "readonly" or "read" => AgentAccess.ReadOnly,
        "read-write" or "readwrite" or "rw" or "write" => AgentAccess.ReadWrite,
        _ => AgentAccess.Off,
    };
}
