using GLoom.Serialization;
using GLoom.Vcs;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class RepositoryTests
{
    private const string Gh = "Coding/tower.gh";
    private const string Json = "Coding/tower.gloom.json";

    private static string Commit(GitRepo repo, CanonicalDocument doc, string subject, string? body = null)
    {
        var ghPath = repo.Write(Gh, $"binary stand-in for {subject}");
        var staged = GLoomRepository.StageForCommit(repo.Root, CanonicalJson.Write(doc), repo.Full(Json), ghPath);
        Assert.Equal(2, staged.Count);
        var sha = GLoomRepository.CommitStaged(repo.Root, subject, body, "Tester", "tester@example.com");
        Assert.NotNull(sha);
        return sha!;
    }

    [Fact]
    public void Staging_and_committing_round_trips_through_the_log_with_its_trailer()
    {
        using var repo = GitRepo.Init();
        var sha = Commit(repo, Doc("tower", Slider(Guid(1), 5)), "First massing",
            "Notes.\n\nGloom-Version: tower_V001");

        var log = GLoomRepository.Log(repo.Root, 10, new[] { Gh, Json });
        var only = Assert.Single(log);
        Assert.Equal(sha, only.Sha);
        Assert.Equal("First massing", only.Message);
        Assert.Equal("Tester", only.Author);
        Assert.Contains("Gloom-Version: tower_V001", only.Body);
        Assert.Equal("V001", CommitVersioning.ExtractVersionLabel(only));
    }

    [Fact]
    public void Nothing_staged_means_no_commit_and_the_stage_reports_it()
    {
        using var repo = GitRepo.Init();
        Commit(repo, Doc("tower", Slider(Guid(1), 5)), "First");

        var staged = GLoomRepository.StageForCommit(
            repo.Root, CanonicalJson.Write(Doc("tower", Slider(Guid(1), 5))), repo.Full(Json), repo.Full(Gh));
        Assert.Empty(staged);
    }

    [Fact]
    public void A_previous_version_is_readable_by_sha_and_by_reference()
    {
        using var repo = GitRepo.Init();
        var first = Doc("tower", Slider(Guid(1), 5));
        var sha1 = Commit(repo, first, "First");
        Commit(repo, Doc("tower", Slider(Guid(1), 9)), "Second");

        // git's clean filter (autocrlf on Windows checkouts) stores LF; the writer emits the platform newline.
        Assert.Equal(CanonicalJson.Write(first).Replace("\r\n", "\n"),
            GLoomRepository.ReadFileAtCommit(repo.Root, sha1, Json)!.Replace("\r\n", "\n"));
        Assert.Equal(sha1, GLoomRepository.ResolveCommit(repo.Root, "HEAD~1"));
        Assert.Null(GLoomRepository.ResolveCommit(repo.Root, "no-such-ref"));
        Assert.Null(GLoomRepository.ReadFileAtCommit(repo.Root, sha1, "Coding/missing.gloom.json"));

        var parsed = CanonicalJson.TryParse(GLoomRepository.ReadFileAtCommit(repo.Root, sha1, Json));
        Assert.NotNull(parsed);
        Assert.Equal(5m, parsed!.Objects[0].Persistent!.Slider!.Value);
    }

    [Fact]
    public void Restore_rewrites_the_working_tree_without_moving_head()
    {
        using var repo = GitRepo.Init();
        var first = Doc("tower", Slider(Guid(1), 5));
        var sha1 = Commit(repo, first, "First");
        var sha2 = Commit(repo, Doc("tower", Slider(Guid(1), 9)), "Second");

        GLoomRepository.Restore(repo.Root, sha1, new[] { Gh, Json });

        Assert.Equal(CanonicalJson.Write(first), repo.Read(Json));
        Assert.Equal(sha2, GLoomRepository.ResolveCommit(repo.Root, "HEAD"));
    }

    [Fact]
    public void The_working_tree_is_matched_to_the_commit_whose_blobs_it_equals()
    {
        using var repo = GitRepo.Init();
        var sha1 = Commit(repo, Doc("tower", Slider(Guid(1), 5)), "First");
        var sha2 = Commit(repo, Doc("tower", Slider(Guid(1), 9)), "Second");

        Assert.Equal(sha2, GLoomRepository.FindCommitMatchingWorkingTree(repo.Root, Gh, Json));

        GLoomRepository.Restore(repo.Root, sha1, new[] { Gh, Json });
        Assert.Equal(sha1, GLoomRepository.FindCommitMatchingWorkingTree(repo.Root, Gh, Json));

        repo.Write(Json, "{ \"edited\": true }");
        Assert.Null(GLoomRepository.FindCommitMatchingWorkingTree(repo.Root, Gh, Json));
    }

    [Fact]
    public void Status_and_version_counting_are_file_scoped()
    {
        using var repo = GitRepo.Init();
        Commit(repo, Doc("tower", Slider(Guid(1), 5)), "First");
        repo.Write("README.md", "unrelated");
        GitRepo.Git(repo.Root, "add", "README.md");
        GitRepo.Git(repo.Root, "-c", "user.name=T", "-c", "user.email=t@x", "commit", "-q", "-m", "docs");

        var status = GLoomRepository.GetStatus(repo.Root, new[] { Gh, Json });
        Assert.Equal("main", status.Branch);
        Assert.Equal("First", status.LastCommit?.Message);
        Assert.Equal(2, CommitVersioning.NextVersion(repo.Root, Gh, Json));
    }

    [Fact]
    public void A_directory_without_git_is_not_a_repository_and_reads_come_back_empty()
    {
        using var repo = GitRepo.Empty();
        Assert.False(GLoomRepository.IsRepo(repo.Root));
        Assert.Empty(GLoomRepository.Log(repo.Root, 10));
        Assert.Null(GLoomRepository.ReadFileAtCommit(repo.Root, "HEAD", Json));
    }
}
