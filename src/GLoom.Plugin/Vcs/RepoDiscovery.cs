using System.IO;

namespace GLoom.Vcs;

public static class RepoDiscovery
{
    /// <summary>
    /// Walks up from <paramref name="startPath"/> looking for a .git marker.
    /// Mirrors how `git` itself locates the repo root. Returns null if not found.
    /// </summary>
    /// <param name="allowMissingStart">
    /// When true, a path that does not exist resolves to its nearest existing
    /// ancestor instead of failing outright. Off by default so tracking state
    /// stays tied to files that are actually on disk.
    /// </param>
    public static string? FindRepoRoot(string? startPath, bool allowMissingStart = false)
    {
        if (string.IsNullOrWhiteSpace(startPath)) return null;

        var current = allowMissingStart
            ? NearestExistingDirectory(startPath)
            : File.Exists(startPath) ? Path.GetDirectoryName(startPath)
            : Directory.Exists(startPath) ? startPath
            : null;

        while (!string.IsNullOrEmpty(current))
        {
            // A .git *file* is a gitlink - linked worktrees and submodules point
            // at their real git directory that way and are still repo roots.
            var marker = Path.Combine(current, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    /// <summary>
    /// Closest directory that exists for a given path: the folder itself, a
    /// file's parent, or - for something not yet on disk - the nearest ancestor
    /// that is. Returns null when nothing resolves.
    /// </summary>
    private static string? NearestExistingDirectory(string path)
    {
        string candidate;
        try
        {
            candidate = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (System.Exception)
        {
            return null;
        }

        while (!string.IsNullOrEmpty(candidate))
        {
            if (Directory.Exists(candidate)) return candidate;
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
            candidate = Path.GetDirectoryName(candidate) ?? string.Empty;
        }
        return null;
    }

    /// <summary>
    /// Sibling JSON filename for a given .gh path: "MyDef.gh" → "MyDef.gloom.json".
    /// </summary>
    public static string CanonicalJsonFilenameFor(string ghFilePath)
    {
        var name = Path.GetFileNameWithoutExtension(ghFilePath);
        return $"{name}.gloom.json";
    }

    public static string CanonicalJsonFullPathFor(string ghFilePath)
    {
        var dir = Path.GetDirectoryName(ghFilePath) ?? string.Empty;
        return Path.Combine(dir, CanonicalJsonFilenameFor(ghFilePath));
    }
}
