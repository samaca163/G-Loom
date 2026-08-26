using GLoom.Vcs;
using Xunit;

namespace GLoom.Survey.Tests;

public class RepoDiscoveryTests
{
    [Fact]
    public void Finds_the_root_from_the_root_itself()
    {
        using var repo = TempRepo.WithGitDirectory();
        Assert.Equal(repo.Root, RepoDiscovery.FindRepoRoot(repo.Root));
    }

    [Fact]
    public void Walks_up_from_a_nested_directory()
    {
        using var repo = TempRepo.WithGitDirectory();
        Assert.Equal(repo.Root, RepoDiscovery.FindRepoRoot(repo.Dir("Coding", "Definitions")));
    }

    [Fact]
    public void Walks_up_from_a_file_rather_than_treating_it_as_a_directory()
    {
        using var repo = TempRepo.WithGitDirectory();
        var file = repo.File_("Coding/tower.gh", "not really a definition");

        Assert.Equal(repo.Root, RepoDiscovery.FindRepoRoot(file));
    }

    [Fact]
    public void A_git_file_is_a_root_too_because_that_is_how_worktrees_and_submodules_point_home()
    {
        using var repo = TempRepo.WithGitLinkFile();
        Assert.Equal(repo.Root, RepoDiscovery.FindRepoRoot(repo.Dir("Coding")));
    }

    [Fact]
    public void A_path_outside_any_repository_finds_nothing()
    {
        using var repo = TempRepo.WithoutGit();
        Assert.Null(RepoDiscovery.FindRepoRoot(repo.Dir("Coding")));
    }

    [Fact]
    public void A_path_not_yet_on_disk_finds_nothing_by_default()
    {
        using var repo = TempRepo.WithGitDirectory();
        var unsaved = Path.Combine(repo.Root, "Coding", "never-saved.gh");

        Assert.Null(RepoDiscovery.FindRepoRoot(unsaved));
    }

    [Fact]
    public void An_unsaved_definition_resolves_through_its_nearest_existing_ancestor_when_asked()
    {
        using var repo = TempRepo.WithGitDirectory();
        repo.Dir("Coding");
        var unsaved = Path.Combine(repo.Root, "Coding", "never-saved.gh");

        Assert.Equal(repo.Root, RepoDiscovery.FindRepoRoot(unsaved, allowMissingStart: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_in_is_nothing_out(string? start)
    {
        Assert.Null(RepoDiscovery.FindRepoRoot(start));
        Assert.Null(RepoDiscovery.FindRepoRoot(start, allowMissingStart: true));
    }
}
