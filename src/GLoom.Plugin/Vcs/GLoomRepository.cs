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

    public static bool IsRepo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        var result = Run(path, "rev-parse", "--is-inside-work-tree");
        return result.ExitCode == 0 && result.StdOut.Trim() == "true";
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

        // 1. Hash every working-tree file as Git would.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in repoRelativeFiles)
        {
            var rel = f.Replace('\\', '/');
            var fullPath = Path.Combine(repoRoot, rel);
            if (!File.Exists(fullPath)) return null;

            var hash = Run(repoRoot, "hash-object", "--", rel);
            if (hash.ExitCode != 0) return null;
            var blob = hash.StdOut.Trim();
            if (string.IsNullOrEmpty(blob)) return null;
            expected[rel] = blob;
        }

        // 2. Walk recent commits that touched any of these files, looking for
        //    one whose tree-blobs match every working-tree blob.
        var logArgs = new List<string> { "log", "-n200", "--format=%H", "--" };
        foreach (var rel in expected.Keys) logArgs.Add(rel);
        var commits = Run(repoRoot, logArgs.ToArray());
        if (commits.ExitCode != 0) return null;

        foreach (var line in commits.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var sha = line.Trim();
            if (string.IsNullOrEmpty(sha)) continue;

            var allMatch = true;
            foreach (var kv in expected)
            {
                // git ls-tree <commit> -- <path> -> "<mode> <type> <blobSha>\t<path>"
                var ls = Run(repoRoot, "ls-tree", sha, "--", kv.Key);
                if (ls.ExitCode != 0 || string.IsNullOrWhiteSpace(ls.StdOut)) { allMatch = false; break; }
                var firstLine = ls.StdOut.Split('\n')[0];
                var parts = firstLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || parts[2].Trim() != kv.Value) { allMatch = false; break; }
            }
            if (allMatch) return sha;
        }
        return null;
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
        if (!IsRepo(workingDir))
            Run(workingDir, "init");
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

    private static ProcResult Run(string workingDir, params string[] args)
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

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return new ProcResult(proc.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not launch git at '{psi.FileName}'. Is git installed? ({ex.Message})", ex);
        }
    }
}
