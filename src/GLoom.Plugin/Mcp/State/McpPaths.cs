using System;
using System.IO;

namespace GLoom.Mcp.State;

/// <summary>
/// Where the MCP side keeps its per-user state: the access mode, the bearer token and the
/// endpoint files other processes discover a running Rhino through. Per user, outside any
/// project, so nothing here can end up committed.
/// </summary>
public static class McpPaths
{
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "G-Loom", "mcp");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string TokenFile => Path.Combine(Root, "token");
    public static string EndpointsDir => Path.Combine(Root, "endpoints");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(EndpointsDir);
    }
}
