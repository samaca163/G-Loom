using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GLoom.Mcp.State;

/// <summary>
/// One file per running Rhino that serves G-Loom's MCP endpoint. Refreshed while alive,
/// removed on shutdown; a reader treats a stale timestamp or a dead pid as gone, because a
/// crashed Rhino leaves its file behind.
/// </summary>
public sealed record EndpointInfo(
    int Pid,
    int Port,
    string Url,
    string Host,
    string Access,
    string RhinoVersion,
    string GloomVersion,
    DateTimeOffset Started,
    DateTimeOffset Refreshed);

public static class EndpointFile
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(int pid) => Path.Combine(McpPaths.EndpointsDir, $"{pid}.json");

    public static void Write(EndpointInfo info)
    {
        McpPaths.Ensure();
        var tmp = PathFor(info.Pid) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(info, Options));
        File.Move(tmp, PathFor(info.Pid), overwrite: true);
    }

    public static void Remove(int pid)
    {
        try { File.Delete(PathFor(pid)); } catch { }
    }

    public static IReadOnlyList<EndpointInfo> ReadAll(DateTimeOffset now)
    {
        var list = new List<EndpointInfo>();
        if (!Directory.Exists(McpPaths.EndpointsDir)) return list;
        foreach (var file in Directory.GetFiles(McpPaths.EndpointsDir, "*.json"))
        {
            try
            {
                var info = JsonSerializer.Deserialize<EndpointInfo>(File.ReadAllText(file), Options);
                if (info is null) continue;
                if (now - info.Refreshed > StaleAfter) { try { File.Delete(file); } catch { } continue; }
                list.Add(info);
            }
            catch { /* half-written or foreign file; skip */ }
        }
        return list;
    }
}
