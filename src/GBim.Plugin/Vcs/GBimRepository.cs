using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GBim.Vcs;

/// <summary>
/// Git operations for G-BIM, implemented by shelling out to the system `git`
/// CLI. We tried LibGit2Sharp first; on Rhino 8 macOS its native dylib's type
/// initializer fails in ways that resist all the standard fixes (custom
/// DllImportResolver, pre-loading via NativeLibrary, GlobalSettings.NativeLibraryPath,
/// install-name symlinks). Shelling out is ~50-100ms per call but rock solid.
/// </summary>
public static class GBimRepository
{
    public sealed record CommitInfo(string Sha, string Author, DateTimeOffset When, string Message);
    public sealed record RepoStatus(string Branch, CommitInfo? LastCommit);

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
    /// auto-versioning logic; pass both the .gh and the .gbim.json so that
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

    /// <summary>
    /// Returns true if the working tree matches the index for the given files
    /// (i.e. no UNSAVED canvas edits since the last snapshot). Deliberately
    /// ignores staged-vs-HEAD differences so that consecutive Restores - which
    /// leave staged content - don't disable themselves; the user can chain
    /// "restore V003, then V001, then V005" without hitting a false dirty
    /// signal between hops.
    /// </summary>
    public static bool AreFilesClean(string repoRoot, IEnumerable<string> repoRelativeFiles)
    {
        if (!IsRepo(repoRoot)) return true;
        var args = new List<string> { "diff", "--quiet", "--" };
        foreach (var f in repoRelativeFiles) args.Add(f.Replace('\\', '/'));
        var result = Run(repoRoot, args.ToArray());
        // git diff --quiet: 0 = no differences, 1 = differences.
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Finds the commit whose copy of <paramref name="repoRelativeFile"/>
    /// matches the file currently in the working tree (compared by Git blob
    /// SHA, so it's content-exact, not path/timestamp/mtime). Returns null if
    /// the working tree's content doesn't match any commit's history of that
    /// file (e.g. user manually edited it without committing).
    ///
    /// This lets us re-derive "current version" across Grasshopper restarts
    /// without persisting any side state - the filesystem is the source of
    /// truth.
    /// </summary>
    public static string? FindCommitMatchingWorkingTree(string repoRoot, string repoRelativeFile)
    {
        if (!IsRepo(repoRoot)) return null;
        var rel = repoRelativeFile.Replace('\\', '/');
        var fullPath = Path.Combine(repoRoot, rel);
        if (!File.Exists(fullPath)) return null;

        // 1. Hash the working-tree file as Git would.
        var workingHash = Run(repoRoot, "hash-object", "--", rel);
        if (workingHash.ExitCode != 0) return null;
        var blobSha = workingHash.StdOut.Trim();
        if (string.IsNullOrEmpty(blobSha)) return null;

        // 2. Walk recent commits that touched this file, comparing tree blobs.
        var commits = Run(repoRoot, "log", "-n200", "--format=%H", "--", rel);
        if (commits.ExitCode != 0) return null;

        foreach (var line in commits.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var sha = line.Trim();
            if (string.IsNullOrEmpty(sha)) continue;

            // git ls-tree <commit> -- <path> -> "<mode> <type> <blobSha>\t<path>"
            var ls = Run(repoRoot, "ls-tree", sha, "--", rel);
            if (ls.ExitCode != 0 || string.IsNullOrWhiteSpace(ls.StdOut)) continue;
            var firstLine = ls.StdOut.Split('\n')[0];
            var parts = firstLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (parts[2].Trim() == blobSha) return sha;
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
