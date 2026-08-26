using GLoom.Mcp.Protocol;
using GLoom.Mcp.State;
using Xunit;

namespace GLoom.Core.Tests;

public class McpStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gloom-tests", Guid.NewGuid().ToString("N"));

    public McpStateTests() => McpPaths.Root = _root;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Access_defaults_to_off_and_round_trips()
    {
        Assert.Equal(AgentAccess.Off, McpSettings.LoadAccess());
        McpSettings.SaveAccess(AgentAccess.ReadWrite);
        Assert.Equal(AgentAccess.ReadWrite, McpSettings.LoadAccess());
        Assert.Equal(AgentAccess.ReadOnly, McpSettings.ParseAccess("READ-ONLY"));
        Assert.Equal(AgentAccess.Off, McpSettings.ParseAccess("whatever"));
    }

    [Fact]
    public void The_token_persists_and_only_an_exact_bearer_matches()
    {
        var token = McpToken.LoadOrCreate();
        Assert.True(token.Length >= 40);
        Assert.Equal(token, McpToken.LoadOrCreate());
        Assert.True(McpToken.Matches("Bearer " + token, token));
        Assert.True(McpToken.Matches("bearer " + token, token));
        Assert.False(McpToken.Matches(token, token));
        Assert.False(McpToken.Matches("Bearer " + token + "x", token));
        Assert.False(McpToken.Matches(null, token));
    }

    [Fact]
    public void Endpoint_files_are_listed_while_fresh_and_dropped_when_stale()
    {
        var now = DateTimeOffset.Now;
        EndpointFile.Write(new EndpointInfo(111, 27180, "http://localhost:27180/mcp", "rhino", "read-only", "8.30", "0.3.0", now, now));
        EndpointFile.Write(new EndpointInfo(222, 27181, "http://localhost:27181/mcp", "revit", "read-write", "8.30", "0.3.0", now, now - TimeSpan.FromMinutes(5)));

        var live = EndpointFile.ReadAll(now);
        var only = Assert.Single(live);
        Assert.Equal(111, only.Pid);
        Assert.Equal("rhino", only.Host);
        Assert.False(File.Exists(EndpointFile.PathFor(222)));

        EndpointFile.Remove(111);
        Assert.Empty(EndpointFile.ReadAll(now));
    }
}
