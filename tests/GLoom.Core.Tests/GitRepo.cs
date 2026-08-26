using System.Diagnostics;

namespace GLoom.Core.Tests;

/// <summary>
/// A real, throwaway git repository under the system temp path - anywhere inside the
/// working tree would find G-Loom's own .git. Commits go through GLoomRepository so the
/// tests exercise the same code the panel and the MCP tools use.
/// </summary>
internal sealed class GitRepo : IDisposable
{
    public string Root { get; }

    private GitRepo(string root) => Root = root;

    public static GitRepo Init()
    {
        var root = Path.Combine(Path.GetTempPath(), "gloom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Resolved because macOS hands out /var/folders paths that are really /private/var.
        root = new DirectoryInfo(root).FullName;
        Git(root, "init", "-q", "-b", "main");
        return new GitRepo(root);
    }

    public static GitRepo Empty()
    {
        var root = Path.Combine(Path.GetTempPath(), "gloom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new GitRepo(new DirectoryInfo(root).FullName);
    }

    public string Full(string relative) =>
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Write(string relative, string contents)
    {
        var path = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public string Read(string relative) => File.ReadAllText(Full(relative));

    public static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
        return stdout;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
