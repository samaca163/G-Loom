using System;
using System.Threading;
using GLoom.Mcp.Host;
using GLoom.Mcp.Host.Live;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.State;
using GLoom.Mcp.Tools.Live;
using GLoom.Mcp.Tools.Memory;
using GLoom.Vcs;
using Rhino;

namespace GLoom.Mcp;

/// <summary>
/// Owns the MCP endpoint for this Rhino process: the persisted access mode, the token, the
/// listener and the endpoint file other processes discover it through. Off by default -
/// the panel's "Agent access" row is the only switch, because a .gha cannot register a
/// Rhino command.
/// </summary>
public static class McpService
{
    // Everything that touches _host or the endpoint file holds this: the heartbeat timer,
    // the panel's mode switch and Rhino's shutdown all arrive on different threads.
    private static readonly object Gate = new();
    private static McpDispatcher? _dispatcher;
    private static McpHttpHost? _host;
    private static System.Threading.Timer? _heartbeat;
    private static DateTimeOffset _started;
    private static bool _endpointWriteFailed;

    public static AgentAccess Access { get; private set; } = AgentAccess.Off;
    public static string? Token { get; private set; }
    public static string? Url => _host?.Url;
    public static bool IsRunning => _host is not null;
    public static string? LastError { get; private set; }

    public static event Action? Changed;

    public static void Initialize()
    {
        UiThread.Initialize();
        try
        {
            Token = McpToken.LoadOrCreate();
            Access = McpSettings.LoadAccess();
            _dispatcher = BuildDispatcher();
            if (Access != AgentAccess.Off) StartHost();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            RhinoApp.WriteLine($"[G-Loom] MCP could not initialise: {ex.Message}");
        }

        RhinoApp.Closing += (_, _) => Shutdown();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        Changed?.Invoke();
    }

    public static void SetAccess(AgentAccess access)
    {
        Access = access;
        try { McpSettings.SaveAccess(access); }
        catch (Exception ex) { RhinoApp.WriteLine($"[G-Loom] Could not save MCP settings: {ex.Message}"); }

        if (access == AgentAccess.Off) StopHost();
        else if (_host is null) StartHost();
        else WriteEndpoint();

        Changed?.Invoke();
    }

    /// <summary>Null until the endpoint is actually listening - a guessed port could point a
    /// client at another Rhino's endpoint.</summary>
    public static string? ConnectCommand =>
        Url is null || Token is null ? null
            : $"claude mcp add --transport http gloom {Url} --header \"Authorization: Bearer {Token}\"";

    private static McpDispatcher BuildDispatcher()
    {
        var version = typeof(McpService).Assembly.GetName().Version?.ToString(3) ?? "0";
        var d = new McpDispatcher(version,
            "G-Loom is the project's memory for Grasshopper definitions: every definition (.gh) in a project " +
            "has a version history of commits, each with a version label like tower_V012, a subject and a " +
            "description of the design decision. Call gloom_status first to learn which definition is active, " +
            "its project root and where it stands in history. Then: gloom_history or gloom_decision_record for " +
            "the record of decisions; gloom_diff and gloom_explain_changes for what changed between two versions " +
            "(by default the last committed version and the file on disk); gloom_read_version for the recipe at " +
            "any version; gloom_branches for the project's system options; gloom_tags and gloom_toolchain for " +
            "milestones and the Rhino / Grasshopper / Rhino.Inside.Revit / G-Loom versions they were pinned on. " +
            "The same facts are readable as gloom:// resources, and the prompts review-changes and design-history " +
            "open a review or a history conversation. The live canvas is read through gloom_documents (what is " +
            "open), gloom_read_document (objects with runtime errors, warnings and output previews, unsaved edits " +
            "included), gloom_read_outputs (the data on one object), gloom_solve (recompute; needs read-write), " +
            "gloom_catalogue (installed components), gloom_canvas_image (a screenshot) and gloom_rhino_context. " +
            "File arguments are absolute paths or paths relative to the project root; version arguments accept a " +
            "label (V012), a sha, a tag, a branch, HEAD or \"working\".");
        d.ClientInitialized += (name, ver) =>
            RhinoApp.WriteLine($"[G-Loom] MCP client connected: {name} {ver}".TrimEnd());
        MemoryTools.Register(d, LiveSnapshot);
        VersionTools.Register(d, LiveSnapshot);
        RecordTools.Register(d, LiveSnapshot, ToolchainSnapshot.Capture);
        d.Register(new GloomResources(LiveSnapshot));
        GloomPrompts.Register(d, LiveSnapshot);
        LiveTools.Register(d, new LiveHost());
        return d;
    }

    private static LiveSnapshot? LiveSnapshot()
    {
        // TrackedState is an immutable record replaced wholesale by the tracker; reading the
        // reference off-thread sees either the previous or the current state, never a torn one.
        var s = DocumentTracker.Instance.State;
        if (s.FilePath is null) return null;
        return new LiveSnapshot(s.FilePath, s.RepoPath, s.HasUnsavedChanges, s.CurrentRestoredSha, s.IsTracked);
    }

    private static void StartHost()
    {
        if (_dispatcher is null || Token is null) return;
        lock (Gate)
        {
            if (_host is not null) return;
            try
            {
                _host = McpHttpHost.Start(_dispatcher, Token, () => Access, McpSettings.DefaultPort, 10);
                _started = DateTimeOffset.Now;
                LastError = null;
                WriteEndpointLocked();
                _heartbeat = new System.Threading.Timer(_ => WriteEndpoint(), null, EndpointFile.RefreshEvery, EndpointFile.RefreshEvery);
                RhinoApp.WriteLine($"[G-Loom] MCP listening on {_host.Url} ({McpSettings.Label(Access)}).");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _host = null;
                RhinoApp.WriteLine($"[G-Loom] MCP could not start: {ex.Message}");
            }
        }
    }

    private static void StopHost()
    {
        lock (Gate)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
            EndpointFile.Remove(Environment.ProcessId);
            if (_host is null) return;
            _host.Dispose();
            _host = null;
            RhinoApp.WriteLine("[G-Loom] MCP stopped.");
        }
    }

    private static void WriteEndpoint()
    {
        lock (Gate) WriteEndpointLocked();
    }

    private static void WriteEndpointLocked()
    {
        var host = _host;
        if (host is null) return;
        try
        {
            var toolchain = ToolchainSnapshot.Capture();
            EndpointFile.Write(new EndpointInfo(
                Environment.ProcessId, host.Port, host.Url,
                toolchain.RhinoInsideRevit is null ? "rhino" : "revit",
                McpSettings.Label(Access), toolchain.Rhino, toolchain.Gloom,
                _started, DateTimeOffset.Now));
            _endpointWriteFailed = false;
        }
        catch (Exception ex)
        {
            // The heartbeat retries every 15 s; one line per failure streak is enough.
            if (!_endpointWriteFailed)
                RhinoApp.WriteLine($"[G-Loom] Could not write the MCP endpoint file: {ex.Message}");
            _endpointWriteFailed = true;
        }
    }

    private static void Shutdown()
    {
        try { StopHost(); } catch { }
    }
}
