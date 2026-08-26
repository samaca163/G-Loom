using GLoom.Vcs;
using Xunit;

namespace GLoom.Core.Tests;

public class CommitTrailersTests
{
    [Fact]
    public void The_last_paragraph_of_key_value_lines_is_the_trailer_block()
    {
        var split = CommitTrailers.Parse("Refined the facade.\n\nSecond paragraph.\n\nGloom-Version: tower_V012\nGloom-Agent: claude-code/2.1");
        Assert.Equal("Refined the facade.\n\nSecond paragraph.", split.Text);
        Assert.Equal("tower_V012", split.Trailers["Gloom-Version"]);
        Assert.Equal("claude-code/2.1", split.Trailers["gloom-agent"]);
    }

    [Fact]
    public void A_body_that_is_only_trailers_has_empty_text()
    {
        var split = CommitTrailers.Parse("Gloom-Version: tower_V001");
        Assert.Equal("", split.Text);
        Assert.Single(split.Trailers);
    }

    [Fact]
    public void A_last_paragraph_with_a_prose_line_is_not_a_trailer_block()
    {
        var split = CommitTrailers.Parse("Notes.\n\nSee: the drawing\nand more prose");
        Assert.Empty(split.Trailers);
        Assert.Equal("Notes.\n\nSee: the drawing\nand more prose", split.Text);
    }

    [Fact]
    public void Crlf_and_empty_bodies_are_handled()
    {
        Assert.Equal("v", CommitTrailers.Parse("Text.\r\n\r\nK: v\r\n").Trailers["K"]);
        Assert.Empty(CommitTrailers.Parse(null).Trailers);
        Assert.Empty(CommitTrailers.Parse("   ").Trailers);
    }

    [Fact]
    public void Append_then_parse_round_trips()
    {
        var body = CommitTrailers.Append("Why this change.", new Dictionary<string, string>
        {
            ["Gloom-Version"] = "tower_V003",
            ["Gloom-Intent"] = "widen the\natrium",
        });
        Assert.Equal("Why this change.\n\nGloom-Version: tower_V003\nGloom-Intent: widen the atrium", body);
        var split = CommitTrailers.Parse(body);
        Assert.Equal("Why this change.", split.Text);
        Assert.Equal(2, split.Trailers.Count);
        Assert.Equal("Gloom-Version: x", CommitTrailers.Append(null, new[] { KeyValuePair.Create("Gloom-Version", "x") }));
    }
}
