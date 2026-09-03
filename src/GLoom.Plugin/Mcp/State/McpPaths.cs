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

    /// <summary>The open edit envelope for this Rhino, if any. Outlives a request (the server
    /// is stateless) and outlives a crash, which is how the next session learns one was
    /// abandoned rather than closed.</summary>
    public static string EnvelopeFile => Path.Combine(Root, $"envelope-{Environment.ProcessId}.json");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(EndpointsDir);
    }
}
