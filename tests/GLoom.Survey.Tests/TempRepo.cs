namespace GLoom.Survey.Tests;

/// <summary>
/// A throwaway directory carrying a .git marker, so repo-root discovery and schema
/// resolution can be exercised without a real repository. Created under the system temp
/// path deliberately - anywhere inside the working tree would find G-Loom's own .git.
/// </summary>
internal sealed class TempRepo : IDisposable
{
    public string Root { get; }

    private TempRepo(string root) => Root = root;

    public static TempRepo WithGitDirectory()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return new TempRepo(root);
    }

    public static TempRepo WithGitLinkFile(string target = "gitdir: /elsewhere/.git/worktrees/x")
    {
        var root = NewRoot();
        File.WriteAllText(Path.Combine(root, ".git"), target);
        return new TempRepo(root);
    }

    public static TempRepo WithoutGit() => new(NewRoot());

    public string Dir(params string[] segments)
    {
        var path = Path.Combine(new[] { Root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    public string File_(string relative, string contents)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents);
        return path;
    }

    public string SchemaPath => Path.Combine(Root, SurveySchemaLoader.FolderName, SurveySchemaLoader.FileName);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "gloom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // Resolved because macOS hands out /var/folders paths that are really /private/var,
        // and a test comparing discovery's answer to its own path would fail on the symlink.
        return new DirectoryInfo(root).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
