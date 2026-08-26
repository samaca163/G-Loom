using GLoom.Vcs;
using Xunit;

namespace GLoom.Core.Tests;

public class CommitVersioningTests
{
    [Theory]
    [InlineData("tower_V007", "V007")]
    [InlineData("Refined facade\n\nGloom-Version: tower_V012", "V012")]
    [InlineData("no version here", null)]
    [InlineData("", null)]
    public void Labels_are_read_from_the_message_shape(string message, string? expected) =>
        Assert.Equal(expected, CommitVersioning.ExtractVersionLabel(message));

    [Fact]
    public void The_trailer_wins_over_a_subject_that_merely_looks_versioned()
    {
        var c = new GLoomRepository.CommitInfo("abc", "me", DateTimeOffset.UnixEpoch,
            "Ported tower_V099 logic", "Gloom-Version: tower_V003");
        Assert.Equal("V003", CommitVersioning.ExtractVersionLabel(c));
    }

    [Fact]
    public void Formatting_pads_to_three_digits() =>
        Assert.Equal("tower_V042", CommitVersioning.FormatMessage("tower", 42));
}
