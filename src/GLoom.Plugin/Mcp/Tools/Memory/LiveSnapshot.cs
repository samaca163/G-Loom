namespace GLoom.Mcp.Tools.Memory;

/// <summary>
/// What the host knows about the active Grasshopper document, handed to the memory tools
/// as plain data so they stay host-free. Null when no document is active.
/// </summary>
public sealed record LiveSnapshot(
    string? ActiveFilePath,
    string? RepoRoot,
    bool IsDirty,
    string? CurrentSha,
    bool IsTracked);
