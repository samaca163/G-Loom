using System.IO;

namespace GLoom.Vcs;

public static class RepoDiscovery
{
    /// <summary>
    /// Walks up from <paramref name="startPath"/> looking for a .git directory.
    /// Mirrors how `git` itself locates the repo root. Returns null if not found.
    /// </summary>
    public static string? FindRepoRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath)) return null;

        var current = File.Exists(startPath)
            ? Path.GetDirectoryName(startPath)
            : Directory.Exists(startPath) ? startPath : null;

        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
                return current;
            current = Path.GetDirectoryName(current);
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
