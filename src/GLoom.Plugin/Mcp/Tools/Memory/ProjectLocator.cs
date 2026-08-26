using System;
using System.IO;
using GLoom.Mcp.Protocol;
using GLoom.Vcs;

namespace GLoom.Mcp.Tools.Memory;

public sealed record LocatedFile(
    string GhFullPath,
    bool Exists,
    string RepoRoot,
    string GhRel,
    string JsonRel,
    bool IsActiveDocument);

/// <summary>
/// Turns a tool's optional "file" argument into repo-relative paths. Absolute paths stand
/// on their own; relative ones resolve against the active document's project; no argument
/// means the active document. A file missing from disk is still located when its project
/// exists - its history may be exactly what the caller wants.
/// </summary>
public static class ProjectLocator
{
    public const string FileArgDescription =
        "Path to a .gh definition: absolute, or relative to the active document's project root. " +
        "Omit to use the active Grasshopper document.";

    public static LocatedFile Locate(string? file, LiveSnapshot? live)
    {
        string full;
        if (string.IsNullOrWhiteSpace(file))
        {
            full = live?.ActiveFilePath
                ?? throw new ToolArgumentException("No active Grasshopper document. Pass \"file\" (absolute, or relative to the project root).");
        }
        else if (Path.IsPathRooted(file))
        {
            full = Path.GetFullPath(file);
        }
        else
        {
            var root = live?.RepoRoot
                ?? throw new ToolArgumentException($"\"{file}\" is relative but no active document defines a project root; pass an absolute path.");
            full = Path.GetFullPath(Path.Combine(root, file));
        }

        var repoRoot = RepoDiscovery.FindRepoRoot(full, allowMissingStart: true)
            ?? throw new ToolArgumentException($"\"{full}\" is not inside a git repository, so G-Loom has no history for it.");

        var jsonFull = RepoDiscovery.CanonicalJsonFullPathFor(full);
        var isActive = live?.ActiveFilePath is { } active
            && string.Equals(Path.GetFullPath(active), full, StringComparison.OrdinalIgnoreCase);

        return new LocatedFile(
            full,
            File.Exists(full),
            repoRoot,
            Rel(repoRoot, full),
            Rel(repoRoot, jsonFull),
            isActive);
    }

    private static string Rel(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');
}
