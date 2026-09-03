using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Memory;
using GLoom.Serialization;
using GLoom.Vcs;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class IndexGateTests
{
    [Fact]
    public void Only_one_holder_at_a_time()
    {
        Assert.True(IndexGate.TryEnter("first"));
        try
        {
            Assert.False(IndexGate.TryEnter("second"));
            Assert.Equal("first", IndexGate.Holder);
        }
        finally
        {
            IndexGate.Exit();
        }

        Assert.Null(IndexGate.Holder);
        Assert.True(IndexGate.TryEnter("third"));
        IndexGate.Exit();
    }

    [Fact]
    public void Commit_refuses_while_the_panel_holds_the_index()
    {
        using var repo = GitRepo.Init();
        GitRepo.Git(repo.Root, "config", "user.name", "Tester");
        GitRepo.Git(repo.Root, "config", "user.email", "t@example.com");

        var gh = repo.Write("Coding/tower.gh", "gh bytes");
        var json = CanonicalJson.Write(Doc("tower", Slider(Guid(1), 5)));
        GLoomRepository.StageForCommit(repo.Root, json, repo.Full("Coding/tower.gloom.json"), gh);
        var sha = GLoomRepository.CommitStaged(repo.Root, "First", "Gloom-Version: tower_V001", "Tester", "t@example.com")!;

        var live = new LiveSnapshot(repo.Full("Coding/tower.gh"), repo.Root, true, sha, true);

        Assert.True(IndexGate.TryEnter("the G-Loom panel"));
        try
        {
            var r = WriteTools.Commit(
                null, "Agent edit", null, "opencode", "adjust massing",
                live, () => CanonicalJson.Write(Doc("tower", Slider(Guid(1), 9))), () => { });

            Assert.True(r.IsError);
            Assert.Contains("the G-Loom panel", r.Content[0].Text);
        }
        finally
        {
            IndexGate.Exit();
        }
    }
}
