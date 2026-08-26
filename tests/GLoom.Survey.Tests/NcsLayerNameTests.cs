using GLoom.Survey;
using Xunit;

namespace GLoom.Survey.Tests;

public class NcsLayerNameTests
{
    [Fact]
    public void Parses_the_minimal_discipline_and_major_group()
    {
        Assert.True(NcsLayerName.TryParse("A-WALL", out var name));
        Assert.Equal("A", name!.Discipline);
        Assert.Null(name.Modifier);
        Assert.Equal("WALL", name.Major);
        Assert.Null(name.Minor1);
        Assert.Null(name.Minor2);
        Assert.Null(name.Status);
    }

    [Fact]
    public void Parses_a_discipline_modifier()
    {
        Assert.True(NcsLayerName.TryParse("AI-WALL", out var name));
        Assert.Equal("A", name!.Discipline);
        Assert.Equal("I", name.Modifier);
        Assert.Equal("AI-WALL", name.Stem);
    }

    [Fact]
    public void Parses_two_minor_groups_and_a_status()
    {
        Assert.True(NcsLayerName.TryParse("A-WALL-FULL-EXTR-E", out var name));
        Assert.Equal("FULL", name!.Minor1);
        Assert.Equal("EXTR", name.Minor2);
        Assert.Equal('E', name.Status);
    }

    [Fact]
    public void A_one_character_tail_reads_as_status_not_as_a_minor_group()
    {
        // The minor groups are four characters and the status is one, which is the whole
        // reason the trailing optionals cannot be confused however many are present.
        Assert.True(NcsLayerName.TryParse("A-WALL-E", out var name));
        Assert.Null(name!.Minor1);
        Assert.Equal('E', name.Status);
    }

    [Theory]
    [InlineData("MUROS")]
    [InlineData("A-WALLS")]
    [InlineData("A-WAL")]
    [InlineData("a-wall")]
    [InlineData("A_WALL")]
    [InlineData("A-WALL-E-E")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_anything_the_grammar_does_not_describe(string? leaf)
    {
        Assert.False(NcsLayerName.TryParse(leaf, out var name));
        Assert.Null(name);
    }

    [Fact]
    public void Trims_before_parsing()
    {
        Assert.True(NcsLayerName.TryParse("  A-WALL  ", out _));
    }

    [Theory]
    [InlineData('E', "EXISTING")]
    [InlineData('D', "DEMOLISH")]
    [InlineData('N', "NEW")]
    [InlineData('T', "TEMPORARY")]
    [InlineData('F', "NEW")]
    [InlineData('M', "OTHER")]
    [InlineData('X', "OTHER")]
    public void Status_codes_that_carry_phase_meaning_resolve_to_a_phase(char status, string phase)
    {
        Assert.Equal(phase, NcsLayerName.PhaseFor(status));
    }

    [Theory]
    [InlineData('1')]
    [InlineData('9')]
    [InlineData(null)]
    public void Construction_phase_digits_resolve_to_nothing_rather_than_being_guessed_at(char? status)
    {
        Assert.Null(NcsLayerName.PhaseFor(status));
    }

    [Fact]
    public void Stem_is_discipline_plus_major_because_minor_groups_refine_rather_than_choose()
    {
        Assert.True(NcsLayerName.TryParse("A-WALL-FULL-EXTR-E", out var name));
        Assert.Equal("A-WALL", name!.Stem);
    }

    [Fact]
    public void HasMinor_looks_in_both_slots()
    {
        Assert.True(NcsLayerName.TryParse("A-WALL-FULL-EXTR", out var name));
        Assert.True(name!.HasMinor("FULL"));
        Assert.True(name.HasMinor("EXTR"));
        Assert.False(name.HasMinor("INTR"));
    }
}
