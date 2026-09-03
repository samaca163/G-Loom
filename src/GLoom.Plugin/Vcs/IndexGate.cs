using System.Threading;

namespace GLoom.Vcs;

/// <summary>
/// Guards the span between staging and committing. Git's index is repo-wide, and the panel
/// holds a staged pair across a modal dialog, so a commit arriving from the MCP endpoint in
/// that window would commit the human's staged files under the agent's message - and the
/// panel's rollback would then unstage paths that had already been committed, reporting
/// "nothing to commit" for a commit that happened.
///
/// Interlocked and readable from any thread on purpose: a tool must never have to take the
/// UI thread to discover the index is busy, or it would wait out the full UI timeout during
/// exactly the long solve this is meant to stay clear of.
/// </summary>
public static class IndexGate
{
    private static int _held;

    /// <summary>Who holds it, for a message the other side can act on. Null when free.</summary>
    public static string? Holder { get; private set; }

    public static bool TryEnter(string holder)
    {
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0) return false;
        Holder = holder;
        return true;
    }

    public static void Exit()
    {
        Holder = null;
        Volatile.Write(ref _held, 0);
    }
}
