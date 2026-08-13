using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GLoom.Vcs;

/// <summary>
/// Git operations for G-Loom, implemented by shelling out to the system `git`
/// CLI. We tried LibGit2Sharp first; on Rhino 8 macOS its native dylib's type
/// initializer fails in ways that resist all the standard fixes (custom
/// DllImportResolver, pre-loading via NativeLibrary, GlobalSettings.NativeLibraryPath,
/// install-name symlinks). Shelling out is ~50-100ms per call but rock solid.
/// </summary>
public static class GLoomRepository
{
    public sealed record CommitInfo(string Sha, string Author, DateTimeOffset When, string Message);
    public sealed record RepoStatus(string Branch, CommitInfo? LastCommit);
    public sealed record BranchInfo(string Name, bool IsCurrent);
    public sealed record ForkPoint(string ParentBranch, string ForkSha);
    public sealed record TagInfo(string Name, string Sha, string? Message);
    public sealed record RemoteInfo(string Name, string FetchUrl);
    public sealed record UpstreamInfo(string Remote, string RemoteBranch);
    public sealed record NetworkResult(bool Success, string Message);
    public readonly record struct AheadBehind(int Ahead, int Behind);

    // ASCII control codes - extremely unlikely to appear in commit messages or
    // author fields - used as field/record delimiters in `git log` output so we
    // can split safely without quoting headaches.
    private const char FieldSep = '';
    private const char RecordSep = '';

    public static string? Commit(
        string repoRoot,
        string canonicalJson,
        string canonicalJsonFullPath,
        string message,
        string authorName,
        string authorEmail,
        string? alsoStageFullPath = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("Repo root is required.", nameof(repoRoot));

        EnsureDirectoryAndRepo(repoRoot);

        // Write the canonical JSON next to the .gh.
        var jsonDir = Path.GetDirectoryName(canonicalJsonFullPath);
        if (!string.IsNullOrEmpty(jsonDir)) Directory.CreateDirectory(jsonDir);
        File.WriteAllText(canonicalJsonFullPath, canonicalJson);

        var jsonRel = ToRepoRelative(repoRoot, canonicalJsonFullPath);
        Run(repoRoot, "add", "--", jsonRel);

        if (!string.IsNullOrEmpty(alsoStageFullPath) && File.Exists(alsoStageFullPath))
        {
            var alsoRel = ToRepoRelative(repoRoot, alsoStageFullPath);
            Run(repoRoot, "add", "--", alsoRel);
        }

        // Anything actually staged?
        var staged = Run(repoRoot, "diff", "--cached", "--name-only");
        if (string.IsNullOrWhiteSpace(staged.StdOut))
            return null;

        var commit = Run(repoRoot,
            "-c", $"user.name={authorName}",
            "-c", $"user.email={authorEmail}",
            "commit", "--message", message);

        if (commit.ExitCode != 0)
            throw new InvalidOperationException(
                $"git commit failed (exit {commit.ExitCode}): {commit.StdErr.Trim()}");

        var sha = Run(repoRoot, "rev-parse", "HEAD");
        return sha.ExitCode == 0 ? sha.StdOut.Trim() : null;
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> recent commits. If
    /// <paramref name="repoRelativeFiles"/> is non-null, only commits that
    /// touched any of those files are returned (prevents cross-file
    /// pollution when several .gh definitions share one repo).
    /// </summary>
    public static IReadOnlyList<CommitInfo> Log(
        string repoRoot,
        int limit,
        IEnumerable<string>? repoRelativeFiles = null)
    {
        if (!IsRepo(repoRoot)) return Array.Empty<CommitInfo>();
        if (limit <= 0) limit = 10;

        var fmt = $"%H{FieldSep}%an{FieldSep}%aI{FieldSep}%s{RecordSep}";
        var args = new List<string> { "log", $"-n{limit}", $"--pretty=format:{fmt}" };
        if (repoRelativeFiles is not null)
        {
            args.Add("--");
            foreach (var f in repoRelativeFiles) args.Add(f.Replace('\\', '/'));
        }
        var result = Run(repoRoot, args.ToArray());
        if (result.ExitCode != 0) return Array.Empty<CommitInfo>();

        var list = new List<CommitInfo>();
        foreach (var rec in result.StdOut.Split(RecordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rec.Trim('\n', '\r').Split(FieldSep);
            if (parts.Length < 4) continue;
            DateTimeOffset.TryParse(parts[2], out var when);
            list.Add(new CommitInfo(parts[0], parts[1], when, parts[3]));
        }
        return list;
    }

    /// <summary>
    /// Branch is always the repo-wide current branch. <c>LastCommit</c> is the
    /// most recent commit touching <paramref name="repoRelativeFiles"/> when
    /// supplied (so multi-file repos don't show another file's commit as
    /// "last"); otherwise the repo's HEAD commit.
    /// </summary>
    public static RepoStatus GetStatus(
        string repoRoot,
        IEnumerable<string>? repoRelativeFiles = null)
    {
        if (!IsRepo(repoRoot))
            return new RepoStatus("(no repo)", null);

        var branch = Run(repoRoot, "rev-parse", "--abbrev-ref", "HEAD");
        var branchName = branch.ExitCode == 0 ? branch.StdOut.Trim() : "(detached)";
        if (branchName == "HEAD") branchName = "(detached)";

        var args = new List<string>
        {
            "log", "-1", $"--pretty=format:%H{FieldSep}%an{FieldSep}%aI{FieldSep}%s",
        };
        if (repoRelativeFiles is not null)
        {
            args.Add("--");
            foreach (var f in repoRelativeFiles) args.Add(f.Replace('\\', '/'));
        }
        var head = Run(repoRoot, args.ToArray());

        CommitInfo? last = null;
        if (head.ExitCode == 0 && !string.IsNullOrWhiteSpace(head.StdOut))
        {
            var parts = head.StdOut.Trim().Split(FieldSep);
            if (parts.Length >= 4)
            {
                DateTimeOffset.TryParse(parts[2], out var when);
                last = new CommitInfo(parts[0], parts[1], when, parts[3]);
            }
        }
        return new RepoStatus(branchName, last);
    }

    /// <summary>
    /// Counts commits in the repo that touched any of the given files.
    /// Returns 0 for a repo with no history of those files. Used by the
    /// auto-versioning logic; pass both the .gh and the .gloom.json so that
    /// commits which only changed one are still counted as a version bump.
    /// </summary>
    public static int CountCommitsTouching(string repoRoot, IEnumerable<string> repoRelativeFiles)
    {
        if (!IsRepo(repoRoot)) return 0;
        var args = new List<string> { "rev-list", "--count", "HEAD", "--" };
        foreach (var f in repoRelativeFiles) args.Add(f.Replace('\\', '/'));
        var result = Run(repoRoot, args.ToArray());
        if (result.ExitCode != 0) return 0;
        return int.TryParse(result.StdOut.Trim(), out var n) ? n : 0;
    }

    // Every public method guards with IsRepo, which used to spawn a git process
    // each time - a hidden ~40% multiplier on all git traffic. Memoized per
    // path for the session; the cheap .git-marker probe detects a repo being
    // deleted or `git init`-ed underneath a cached answer and forces a re-probe.
    // ConcurrentDictionary because panel reads run on a background thread.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        _isRepoCache = new(StringComparer.Ordinal);

    public static bool IsRepo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

        // .git is a directory in normal repos, a file in worktrees/submodules.
        var marker = Path.Combine(path, ".git");
        var markerExists = Directory.Exists(marker) || File.Exists(marker);
        if (_isRepoCache.TryGetValue(path, out var cached) && cached == markerExists)
            return cached;
        _isRepoCache.TryRemove(path, out _);

        var result = Run(path, "rev-parse", "--is-inside-work-tree");
        var isRepo = result.ExitCode == 0 && result.StdOut.Trim() == "true";
        if (markerExists || !isRepo) _isRepoCache[path] = isRepo;
        return isRepo;
    }

    // ------------------------------------------------------------------
    // Branch operations
    // ------------------------------------------------------------------

    /// <summary>
    /// Lists all local branches, with the current one flagged. Empty for a
    /// brand-new repo with no commits (no branches exist until first commit).
    /// </summary>
    public static IReadOnlyList<BranchInfo> GetBranches(string repoRoot)
    {
        if (!IsRepo(repoRoot)) return Array.Empty<BranchInfo>();

        // %(HEAD) prints "*" for the current branch, " " otherwise.
        var fmt = $"%(HEAD){FieldSep}%(refname:short)";
        var result = Run(repoRoot, "for-each-ref", "refs/heads/", $"--format={fmt}");
        if (result.ExitCode != 0) return Array.Empty<BranchInfo>();

        var list = new List<BranchInfo>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(FieldSep, 2);
            if (parts.Length < 2) continue;
            var isCurrent = parts[0].Trim() == "*";
            var name = parts[1].Trim();
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new BranchInfo(name, isCurrent));
        }
        return list;
    }

    public static void CreateBranch(string repoRoot, string name, bool checkout)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var args = checkout
            ? new[] { "checkout", "-b", name }
            : new[] { "branch", name };
        var result = Run(repoRoot, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    /// <summary>
    /// Switches HEAD to the named branch via plain `git checkout &lt;branch&gt;`.
    /// This swaps every file in the working tree, not just G-Loom-managed
    /// files. Throws on conflict or unknown branch.
    /// </summary>
    public static void SwitchBranch(string repoRoot, string name)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var result = Run(repoRoot, "checkout", name);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git checkout {name} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    public static void DeleteBranch(string repoRoot, string name, bool force)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var flag = force ? "-D" : "-d";
        var result = Run(repoRoot, "branch", flag, name);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git branch {flag} {name} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    /// <summary>
    /// Renames a branch via `git branch -m &lt;old&gt; &lt;new&gt;`. Works whether
    /// the branch is currently checked out or not. Throws on conflict (a
    /// branch with the new name already exists) or invalid name.
    /// </summary>
    public static void RenameBranch(string repoRoot, string oldName, string newName)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var result = Run(repoRoot, "branch", "-m", oldName, newName);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git branch -m {oldName} {newName} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    /// <summary>
    /// Returns the repo's default branch name, or null if it can't be
    /// determined. Tries `origin/HEAD` first (set by `git clone`), then
    /// falls back to a local `main` or `master` if either exists.
    /// </summary>
    public static string? GetDefaultBranch(string repoRoot)
    {
        if (!IsRepo(repoRoot)) return null;

        var sym = Run(repoRoot, "symbolic-ref", "--short", "refs/remotes/origin/HEAD");
        if (sym.ExitCode == 0)
        {
            var name = sym.StdOut.Trim();
            if (name.StartsWith("origin/")) name = name["origin/".Length..];
            if (!string.IsNullOrEmpty(name)) return name;
        }

        foreach (var candidate in new[] { "main", "master" })
        {
            var verify = Run(repoRoot, "show-ref", "--verify", "--quiet", $"refs/heads/{candidate}");
            if (verify.ExitCode == 0) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Computes fork-point markers for <paramref name="currentBranchName"/>:
    /// up to two markers, one anchored to the default branch (main/master)
    /// and one to the closest other branch (most recent merge-base). The two
    /// are deduplicated when they resolve to the same commit.
    /// </summary>
    public static IReadOnlyList<ForkPoint> GetForkPoints(
        string repoRoot,
        string currentBranchName,
        IReadOnlyList<BranchInfo>? branches = null)
    {
        if (!IsRepo(repoRoot)) return Array.Empty<ForkPoint>();
        if (string.IsNullOrEmpty(currentBranchName) || currentBranchName == "(detached)")
            return Array.Empty<ForkPoint>();

        branches ??= GetBranches(repoRoot);
        var others = new List<string>();
        foreach (var b in branches)
            if (b.Name != currentBranchName) others.Add(b.Name);
        if (others.Count == 0) return Array.Empty<ForkPoint>();

        var defaultBranch = GetDefaultBranch(repoRoot);

        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in others)
        {
            var mb = Run(repoRoot, "merge-base", currentBranchName, name);
            if (mb.ExitCode != 0) continue;
            var sha = mb.StdOut.Trim();
            if (!string.IsNullOrEmpty(sha)) bases[name] = sha;
        }
        if (bases.Count == 0) return Array.Empty<ForkPoint>();

        var result = new List<ForkPoint>();
        if (defaultBranch != null
            && defaultBranch != currentBranchName
            && bases.TryGetValue(defaultBranch, out var defSha))
        {
            result.Add(new ForkPoint(defaultBranch, defSha));
        }

        // Closest non-default branch by commit timestamp of its merge-base.
        (string Name, string Sha, long Ts)? closest = null;
        foreach (var kv in bases)
        {
            if (kv.Key == defaultBranch) continue;
            var ts = Run(repoRoot, "log", "-1", "--format=%ct", kv.Value);
            long t = 0;
            if (ts.ExitCode == 0) long.TryParse(ts.StdOut.Trim(), out t);
            if (closest is null || t > closest.Value.Ts)
                closest = (kv.Key, kv.Value, t);
        }

        if (closest is { } c && !result.Exists(fp => fp.ForkSha == c.Sha))
            result.Add(new ForkPoint(c.Name, c.Sha));

        return result;
    }

    // ------------------------------------------------------------------
    // Tag operations
    // ------------------------------------------------------------------

    /// <summary>
    /// Lists every tag in the repo with the commit SHA it points at and the
    /// raw message body for annotated tags. Resolves annotated tags through
    /// the tag object via %(*objectname); for lightweight tags
    /// %(objectname) already is the commit SHA, so we fall back to it when
    /// %(*objectname) is empty. %(contents) carries the full annotated tag
    /// body (multi-line, possibly JSON) and is empty for lightweight tags.
    /// We split records on RecordSep instead of newline because the
    /// contents field may contain its own newlines.
    /// </summary>
    public static IReadOnlyList<TagInfo> GetTags(string repoRoot)
    {
        if (!IsRepo(repoRoot)) return Array.Empty<TagInfo>();

        var fmt = $"%(refname:short){FieldSep}%(objectname){FieldSep}%(*objectname){FieldSep}%(contents){RecordSep}";
        var result = Run(repoRoot, "for-each-ref", "refs/tags/", $"--format={fmt}");
        if (result.ExitCode != 0) return Array.Empty<TagInfo>();

        var list = new List<TagInfo>();
        foreach (var rec in result.StdOut.Split(RecordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rec.TrimStart('\n', '\r').Split(FieldSep);
            if (parts.Length < 2) continue;
            var name = parts[0].Trim();
            var sha = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2])
                ? parts[2].Trim()
                : parts[1].Trim();
            var msg = parts.Length >= 4 ? parts[3].Trim('\n', '\r', ' ') : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(sha)) continue;
            list.Add(new TagInfo(name, sha, string.IsNullOrEmpty(msg) ? null : msg));
        }
        return list;
    }

    /// <summary>
    /// Creates a lightweight tag pointing at <paramref name="commitSha"/>.
    /// Throws if the tag name is invalid or already exists.
    /// </summary>
    public static void CreateTag(string repoRoot, string tagName, string commitSha)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var result = Run(repoRoot, "tag", tagName, commitSha);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git tag {tagName} {commitSha} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    /// <summary>
    /// Creates an annotated tag with the given message and tagger info.
    /// We use this for G-Loom tags so the toolchain metadata JSON travels
    /// inside the tag object itself - no working-tree pollution, survives
    /// `git push --tags` cleanly.
    /// </summary>
    public static void CreateAnnotatedTag(
        string repoRoot,
        string tagName,
        string commitSha,
        string message,
        string taggerName,
        string taggerEmail)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var result = Run(repoRoot,
            "-c", $"user.name={taggerName}",
            "-c", $"user.email={taggerEmail}",
            "tag", "-a", tagName, commitSha, "-m", message);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git tag -a {tagName} {commitSha} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    public static void DeleteTag(string repoRoot, string tagName)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var result = Run(repoRoot, "tag", "-d", tagName);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git tag -d {tagName} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    // ------------------------------------------------------------------
    // Remote / network operations
    // ------------------------------------------------------------------

    public static IReadOnlyList<RemoteInfo> GetRemotes(string repoRoot)
    {
        if (!IsRepo(repoRoot)) return Array.Empty<RemoteInfo>();

        var result = Run(repoRoot, "remote", "-v");
        if (result.ExitCode != 0) return Array.Empty<RemoteInfo>();

        // Each remote prints two lines (fetch + push); we only keep fetch URLs
        // and dedupe by remote name to preserve declaration order.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<RemoteInfo>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (!trimmed.EndsWith("(fetch)", StringComparison.Ordinal)) continue;

            var firstTab = trimmed.IndexOf('\t');
            var firstSpace = trimmed.IndexOf(' ');
            if (firstTab < 0 || firstSpace < firstTab) continue;
            var name = trimmed[..firstTab].Trim();
            var url = trimmed[(firstTab + 1)..firstSpace].Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
            list.Add(new RemoteInfo(name, url));
        }
        return list;
    }

    public static void AddRemote(string repoRoot, string name, string url)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var result = Run(repoRoot, "remote", "add", name, url);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git remote add {name} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    public static void RemoveRemote(string repoRoot, string name)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var result = Run(repoRoot, "remote", "remove", name);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git remote remove {name} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    public static void SetRemoteUrl(string repoRoot, string name, string url)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var result = Run(repoRoot, "remote", "set-url", name, url);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git remote set-url {name} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    /// <summary>
    /// Returns the configured upstream for <paramref name="branch"/> as a
    /// (remote, remoteBranch) pair, or null when the branch has no upstream.
    /// Resolved via rev-parse @{upstream}; an exit-1 means "no upstream", not
    /// an error condition.
    /// </summary>
    public static UpstreamInfo? GetUpstream(string repoRoot, string branch)
    {
        if (!IsRepo(repoRoot) || string.IsNullOrEmpty(branch)) return null;
        if (branch == "(detached)") return null;

        var result = Run(repoRoot,
            "rev-parse", "--abbrev-ref", "--symbolic-full-name", $"{branch}@{{u}}");
        if (result.ExitCode != 0) return null;

        var full = result.StdOut.Trim();
        if (string.IsNullOrEmpty(full)) return null;

        var slash = full.IndexOf('/');
        if (slash <= 0 || slash >= full.Length - 1) return null;
        return new UpstreamInfo(full[..slash], full[(slash + 1)..]);
    }

    public static void SetUpstream(string repoRoot, string branch, string remote, string remoteBranch)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var result = Run(repoRoot, "branch", $"--set-upstream-to={remote}/{remoteBranch}", branch);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git branch --set-upstream-to failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    /// <summary>
    /// Ahead/behind counts of <paramref name="branch"/> relative to its
    /// configured upstream. Zero/zero when no upstream is configured (caller
    /// should check GetUpstream first if it needs to distinguish "in sync"
    /// from "no upstream").
    /// </summary>
    public static AheadBehind GetAheadBehind(string repoRoot, string branch)
    {
        if (!IsRepo(repoRoot) || string.IsNullOrEmpty(branch)) return default;
        if (branch == "(detached)") return default;
        return GetAheadBehind(repoRoot, branch, GetUpstream(repoRoot, branch));
    }

    /// <summary>
    /// Overload for callers that already resolved the upstream - avoids the
    /// duplicate GetUpstream spawn the panel used to pay on every refresh.
    /// </summary>
    public static AheadBehind GetAheadBehind(string repoRoot, string branch, UpstreamInfo? upstream)
    {
        if (upstream is null) return default;
        if (!IsRepo(repoRoot) || string.IsNullOrEmpty(branch)) return default;
        if (branch == "(detached)") return default;

        var spec = $"{upstream.Remote}/{upstream.RemoteBranch}...{branch}";
        var result = Run(repoRoot, "rev-list", "--left-right", "--count", spec);
        if (result.ExitCode != 0) return default;

        var parts = result.StdOut.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return default;
        int.TryParse(parts[0], out var behind);
        int.TryParse(parts[1], out var ahead);
        return new AheadBehind(ahead, behind);
    }

    public static NetworkResult Fetch(string repoRoot, string remote)
    {
        if (!IsRepo(repoRoot)) return new NetworkResult(false, "Not a Git repo.");
        var result = RunNetwork(repoRoot, "fetch", "--prune", remote);
        return new NetworkResult(
            result.ExitCode == 0,
            result.ExitCode == 0
                ? FirstNonEmptyLine(result.StdErr, result.StdOut) ?? $"Fetched {remote}."
                : ExtractFailureMessage(result));
    }

    /// <summary>
    /// Fast-forward-only pull. Refuses to pull when local has diverged from
    /// the remote; the caller surfaces the rejection and the user falls back
    /// to a real merge in Phase 5.
    /// </summary>
    public static NetworkResult Pull(string repoRoot, string remote, string remoteBranch)
    {
        if (!IsRepo(repoRoot)) return new NetworkResult(false, "Not a Git repo.");
        var result = RunNetwork(repoRoot, "pull", "--ff-only", remote, remoteBranch);
        return new NetworkResult(
            result.ExitCode == 0,
            result.ExitCode == 0
                ? "Pulled from " + remote + "/" + remoteBranch + "."
                : ExtractFailureMessage(result));
    }

    public static NetworkResult Push(string repoRoot, string remote, string branch, bool setUpstream)
    {
        if (!IsRepo(repoRoot)) return new NetworkResult(false, "Not a Git repo.");
        var args = setUpstream
            ? new[] { "push", "-u", remote, branch }
            : new[] { "push", remote, branch };
        var result = RunNetwork(repoRoot, args);
        return new NetworkResult(
            result.ExitCode == 0,
            result.ExitCode == 0
                ? "Pushed to " + remote + "/" + branch + "."
                : ExtractFailureMessage(result));
    }

    private static string ExtractFailureMessage(ProcResult result)
    {
        var msg = FirstNonEmptyLine(result.StdErr, result.StdOut);
        if (string.IsNullOrEmpty(msg)) msg = $"git exited {result.ExitCode}.";
        return msg!;
    }

    private static string? FirstNonEmptyLine(params string[] sources)
    {
        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            foreach (var line in s.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r').Trim();
                if (!string.IsNullOrEmpty(trimmed)) return trimmed;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the union of .gh files tracked on <paramref name="targetBranch"/>
    /// and currently in the working tree - i.e. every .gh that may be
    /// added, modified, or removed by `git checkout &lt;targetBranch&gt;`.
    /// Repo-relative paths, sorted lexicographically.
    /// </summary>
    public static IReadOnlyList<string> ListAffectedGhFiles(string repoRoot, string targetBranch)
    {
        if (!IsRepo(repoRoot)) return Array.Empty<string>();

        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ListGhFilesAtRef(repoRoot, targetBranch)) union.Add(f);
        foreach (var f in ListGhFilesInWorkingTree(repoRoot)) union.Add(f);

        var sorted = new List<string>(union);
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    private static IReadOnlyList<string> ListGhFilesAtRef(string repoRoot, string @ref)
    {
        var result = Run(repoRoot, "ls-tree", "-r", "--name-only", @ref);
        if (result.ExitCode != 0) return Array.Empty<string>();
        return FilterGh(result.StdOut);
    }

    private static IReadOnlyList<string> ListGhFilesInWorkingTree(string repoRoot)
    {
        var result = Run(repoRoot, "ls-files");
        if (result.ExitCode != 0) return Array.Empty<string>();
        return FilterGh(result.StdOut);
    }

    private static List<string> FilterGh(string output)
    {
        var list = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.EndsWith(".gh", StringComparison.OrdinalIgnoreCase))
                list.Add(trimmed);
        }
        return list;
    }

    /// <summary>
    /// Finds the commit whose copy of ALL <paramref name="repoRelativeFiles"/>
    /// matches the working tree (compared by Git blob SHA, so it's content-
    /// exact, not path/timestamp/mtime). Returns null when no single commit's
    /// pair matches the working tree (e.g. user hand-edited a file).
    ///
    /// Why multi-file: the canonical .gloom.json is structural-only (Phase 1a),
    /// so a slider tweak commits a new .gh with a byte-identical JSON. Hashing
    /// only the JSON would resolve "current" to the previous JSON-bumping
    /// commit, leaving the panel arrow stuck. Hashing both files together
    /// ties "current" to a unique commit.
    ///
    /// This also lets us re-derive "current version" across Grasshopper
    /// restarts without any persisted side state - the filesystem is the
    /// source of truth.
    /// </summary>
    public static string? FindCommitMatchingWorkingTree(string repoRoot, params string[] repoRelativeFiles)
    {
        if (!IsRepo(repoRoot)) return null;
        if (repoRelativeFiles.Length == 0) return null;

        // 1. Hash every working-tree file as Git would, in ONE invocation
        //    (one hash per line, argument order). Must be `git hash-object`,
        //    not in-process SHA-1: git applies clean filters (autocrlf /
        //    .gitattributes CRLF->LF) before hashing, so raw disk bytes diverge
        //    from committed blob IDs on Windows checkouts.
        var rels = new string[repoRelativeFiles.Length];
        for (var i = 0; i < repoRelativeFiles.Length; i++)
        {
            rels[i] = repoRelativeFiles[i].Replace('\\', '/');
            if (!File.Exists(Path.Combine(repoRoot, rels[i]))) return null;
        }

        var hashArgs = new List<string> { "hash-object", "--" };
        hashArgs.AddRange(rels);
        var hash = Run(repoRoot, hashArgs.ToArray());
        if (hash.ExitCode != 0) return null;
        var blobs = hash.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (blobs.Length != rels.Length) return null;

        var expected = new (string Rel, string Blob)[rels.Length];
        for (var i = 0; i < rels.Length; i++)
            expected[i] = (rels[i], blobs[i].Trim());

        // 2. Candidate commits that touched any of these files.
        var logArgs = new List<string> { "log", "-n200", "--format=%H", "--" };
        logArgs.AddRange(rels);
        var commits = Run(repoRoot, logArgs.ToArray());
        if (commits.ExitCode != 0) return null;
        var candidates = commits.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (candidates.Length == 0) return null;

        // 3. One cat-file process answers every <sha>:<path> blob query,
        //    replacing the previous ls-tree spawn per commit (~200 process
        //    launches on a miss - the multi-second post-save freeze).
        return MatchViaCatFile(repoRoot, candidates, expected);
    }

    private static string? MatchViaCatFile(
        string repoRoot, string[] candidates, (string Rel, string Blob)[] expected)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GitBinary(),
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        psi.ArgumentList.Add("cat-file");
        psi.ArgumentList.Add("--batch-check");

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return null;
            _ = proc.StandardError.ReadToEndAsync();

            var budget = Stopwatch.StartNew();
            foreach (var line in candidates)
            {
                var sha = line.Trim();
                if (string.IsNullOrEmpty(sha)) continue;

                var allMatch = true;
                foreach (var (rel, blob) in expected)
                {
                    if (budget.ElapsedMilliseconds > 15_000)
                    {
                        Rhino.RhinoApp.WriteLine("[G-Loom] cat-file match walk exceeded 15s; giving up.");
                        return null;
                    }

                    // Strict ping-pong: one request, one response. Writing all
                    // requests up front deadlocks once responses fill the pipe
                    // buffer while we are still blocked writing stdin.
                    // On macOS git precomposes argv (core.precomposeunicode)
                    // but NOT stdin, so NFD on-disk filenames must be
                    // NFC-normalized here or every tree lookup answers
                    // "missing" - argv-based ls-tree used to get this free.
                    var requestPath = OperatingSystem.IsMacOS()
                        ? rel.Normalize(System.Text.NormalizationForm.FormC)
                        : rel;
                    proc.StandardInput.Write(sha + ":" + requestPath + "\n");
                    proc.StandardInput.Flush();
                    var response = proc.StandardOutput.ReadLine();
                    if (response is null) return null;

                    // Hit: "<oid> blob <size>". Path absent at that commit:
                    // "<input> missing". Anything else: treat as non-matching.
                    var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3 || parts[1] != "blob" || parts[0] != blob)
                    {
                        allMatch = false;
                        break;
                    }
                }
                if (allMatch) return sha;
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (proc is not null)
            {
                try { proc.StandardInput.Close(); } catch { }
                if (!proc.WaitForExit(2000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
                proc.Dispose();
            }
        }
    }


    /// <summary>
    /// Resolves any commit-ish reference (HEAD, a branch, a tag, an
    /// abbreviated SHA) to its full commit SHA, or null when the reference
    /// doesn't resolve. Lets callers cache content by immutable SHA instead
    /// of re-reading a moving reference.
    /// </summary>
    public static string? ResolveCommit(string repoRoot, string reference)
    {
        if (!IsRepo(repoRoot) || string.IsNullOrWhiteSpace(reference)) return null;
        var result = Run(repoRoot, "rev-parse", "--verify", "--quiet", reference + "^{commit}");
        if (result.ExitCode != 0) return null;
        var sha = result.StdOut.Trim();
        return string.IsNullOrEmpty(sha) ? null : sha;
    }

    /// <summary>
    /// Reads the contents of a file as it existed at <paramref name="commitSha"/>
    /// via `git show &lt;sha&gt;:&lt;path&gt;`. Returns null when git fails (file
    /// didn't exist at that commit, repo not initialised, bad path, etc.) -
    /// the caller decides how to fall back rather than throwing for a miss.
    /// </summary>
    public static string? ReadFileAtCommit(string repoRoot, string commitSha, string repoRelativePath)
    {
        if (!IsRepo(repoRoot)) return null;
        var path = repoRelativePath.Replace('\\', '/');
        var result = Run(repoRoot, "show", $"{commitSha}:{path}");
        return result.ExitCode == 0 ? result.StdOut : null;
    }

    /// <summary>
    /// Restores the given files at the given commit by running
    /// `git checkout &lt;sha&gt; -- &lt;files...&gt;`. Throws if git fails.
    /// </summary>
    public static void Restore(string repoRoot, string commitSha, IEnumerable<string> repoRelativeFiles)
    {
        if (!IsRepo(repoRoot))
            throw new InvalidOperationException($"{repoRoot} is not a Git repo.");

        var args = new List<string> { "checkout", commitSha, "--" };
        foreach (var f in repoRelativeFiles) args.Add(f.Replace('\\', '/'));

        var result = Run(repoRoot, args.ToArray());
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git checkout failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
    }

    private static void EnsureDirectoryAndRepo(string workingDir)
    {
        if (!Directory.Exists(workingDir))
            Directory.CreateDirectory(workingDir);
        if (!IsRepo(workingDir) && Run(workingDir, "init").ExitCode == 0)
            _isRepoCache[workingDir] = true;
    }

    private static string ToRepoRelative(string repoRoot, string fullPath) =>
        Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');

    // ------------------------------------------------------------------
    // git binary discovery + invocation
    // ------------------------------------------------------------------

    private static string? _gitBinaryCache;

    private static string GitBinary()
    {
        if (_gitBinaryCache is not null) return _gitBinaryCache;

        // macOS apps inherit a sanitized PATH that often excludes /opt/homebrew/bin
        // and /usr/local/bin. Try common locations explicitly before falling back
        // to PATH-based resolution.
        IEnumerable<string> candidates;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            candidates = new[] { "/usr/bin/git", "/opt/homebrew/bin/git", "/usr/local/bin/git" };
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            candidates = new[] { @"C:\Program Files\Git\cmd\git.exe", @"C:\Program Files\Git\bin\git.exe" };
        else
            candidates = new[] { "/usr/bin/git", "/usr/local/bin/git" };

        foreach (var c in candidates)
            if (File.Exists(c)) { _gitBinaryCache = c; return c; }

        // Fall back to unqualified - hope it's on PATH.
        _gitBinaryCache = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "git.exe" : "git";
        return _gitBinaryCache;
    }

    private readonly record struct ProcResult(int ExitCode, string StdOut, string StdErr);

    // Local git ops finish in tens of milliseconds; only a truly wedged process
    // trips this. Network ops get longer: GUI credential-helper popups and slow
    // transfers are legitimate (GIT_TERMINAL_PROMPT=0 only stops TTY prompts).
    private const int LocalTimeoutMs = 30_000;
    private const int NetworkTimeoutMs = 120_000;

    private static ProcResult Run(string workingDir, params string[] args)
    {
        return RunInternal(workingDir, args, suppressTerminalPrompt: false, LocalTimeoutMs);
    }

    /// <summary>
    /// Run a git command that may need credentials. Sets GIT_TERMINAL_PROMPT=0
    /// so git exits with a clear error if no credential helper resolves auth -
    /// otherwise git would block forever waiting on a TTY that doesn't exist
    /// inside Rhino's process.
    /// </summary>
    private static ProcResult RunNetwork(string workingDir, params string[] args)
    {
        return RunInternal(workingDir, args, suppressTerminalPrompt: true, NetworkTimeoutMs);
    }

    private static ProcResult RunInternal(
        string workingDir, string[] args, bool suppressTerminalPrompt, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GitBinary(),
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        if (suppressTerminalPrompt)
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            // Drain both pipes concurrently: reading stdout to completion before
            // touching stderr deadlocks when git fills the stderr pipe buffer
            // first (checkout/commit/tag all write progress there).
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stdout, stderr }, 2000);
                Rhino.RhinoApp.WriteLine(
                    $"[G-Loom] git {(args.Length > 0 ? args[0] : "?")} timed out after {timeoutMs / 1000}s and was killed.");
                return new ProcResult(-1, SafeResult(stdout), SafeResult(stderr));
            }
            System.Threading.Tasks.Task.WaitAll(stdout, stderr);
            return new ProcResult(proc.ExitCode, stdout.Result, stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not launch git at '{psi.FileName}'. Is git installed? ({ex.Message})", ex);
        }
    }

    private static string SafeResult(System.Threading.Tasks.Task<string> t) =>
        t.IsCompletedSuccessfully ? t.Result : string.Empty;
}
