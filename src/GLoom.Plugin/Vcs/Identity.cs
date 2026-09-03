namespace GLoom.Vcs;

/// <summary>
/// Who a version is attributed to. Always the human at the keyboard, read from git's own
/// config, so a project's history reads the same whether a commit came from the panel or
/// through the agent endpoint; an agent is named in trailers, never in the author field.
/// </summary>
public static class Identity
{
    public sealed record Author(string Name, string Email);

    public const string NotSetMessage =
        "This project has no commit identity set. Run `git config user.name \"...\"` and " +
        "`git config user.email \"...\"` so the version is attributed to you.";

    /// <summary>Null when git has no identity configured; the caller shows <see cref="NotSetMessage"/>.</summary>
    public static Author? Resolve(string repoRoot)
    {
        var (name, email) = GLoomRepository.ConfiguredIdentity(repoRoot);
        return string.IsNullOrWhiteSpace(name) ? null : new Author(name, email ?? "unknown");
    }
}
