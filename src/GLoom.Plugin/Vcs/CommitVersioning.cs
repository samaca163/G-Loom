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
    /// Resolves the version label from a commit, searching both the subject
    /// and the body. Dialog-based commits carry the version in a
    /// <c>Gloom-Version:</c> body trailer (keeping subjects human-readable);
    /// auto-versioned commits carry it in the subject. Scanning both keeps
    /// every era of history readable.
    /// </summary>
    public static string? ExtractVersionLabel(GLoomRepository.CommitInfo c) =>
        ExtractVersionLabel($"{c.Message}\n{c.Body}");
}
