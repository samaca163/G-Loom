namespace GLoom.Vcs;

public static class CommitVersioning
{
    /// <summary>
    /// Counts commits in <paramref name="repoPath"/> that touched any of
    /// <paramref name="repoRelativeFiles"/> and returns the next version number.
    /// Pass both the .gh and the .gloom.json - if either changed, the commit
    /// counts as a version bump. Returns 1 for an empty/missing repo or
    /// never-committed files.
    /// </summary>
    public static int NextVersion(string repoPath, params string[] repoRelativeFiles) =>
        GLoomRepository.CountCommitsTouching(repoPath, repoRelativeFiles) + 1;

    /// <summary>
    /// Commit-message format: "&lt;ghBaseName&gt;_V###" with 3-digit zero padding.
    /// </summary>
    public static string FormatMessage(string ghBaseName, int version) =>
        $"{ghBaseName}_V{version:D3}";

    /// <summary>
    /// Parses a commit message of shape "&lt;anything&gt;_V###" and returns ###
    /// formatted as "V###". Returns null if the message isn't in that shape -
    /// useful for displaying the version label of historic commits in the
    /// history list, including ones made outside our auto-versioning flow.
    /// </summary>
    public static string? ExtractVersionLabel(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            message, @"_V(\d{3,})\b");
        return match.Success ? "V" + match.Groups[1].Value : null;
    }

    /// <summary>
    /// Resolves the version label from a commit. The explicit
    /// <c>Gloom-Version:</c> body trailer is authoritative - a user-typed
    /// subject can legitimately contain "_V012"-shaped text and must not
    /// shadow the real version. The loose subject/body scan is the fallback
    /// for pre-trailer auto commits and hand-written history.
    /// </summary>
    public static string? ExtractVersionLabel(GLoomRepository.CommitInfo c)
    {
        if (!string.IsNullOrEmpty(c.Body))
        {
            foreach (var line in c.Body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Gloom-Version:", System.StringComparison.Ordinal))
                    return ExtractVersionLabel(trimmed);
            }
        }
        return ExtractVersionLabel($"{c.Message}\n{c.Body}");
    }
}
