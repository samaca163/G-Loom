namespace GBim.Vcs;

public static class CommitVersioning
{
    /// <summary>
    /// Counts commits in <paramref name="repoPath"/> that touched
    /// <paramref name="repoRelativeFile"/> and returns the next version number.
    /// Returns 1 for an empty / missing repo or a never-committed file.
    /// </summary>
    public static int NextVersion(string repoPath, string repoRelativeFile) =>
        GBimRepository.CountCommitsTouching(repoPath, repoRelativeFile) + 1;

    /// <summary>
    /// Commit-message format: "&lt;ghBaseName&gt;_V###" with 3-digit zero padding.
    /// </summary>
    public static string FormatMessage(string ghBaseName, int version) =>
        $"{ghBaseName}_V{version:D3}";
}
