using GLoom.Survey;
using Xunit;

namespace GLoom.Survey.Tests;

public class LayerPathTests
{
    [Theory]
    [InlineData("Muros", "MUROS")]
    [InlineData("Arquitectura::Muros", "ARQUITECTURA::MUROS")]
    [InlineData("Arquitectura$Muros", "ARQUITECTURA::MUROS")]
    [InlineData("Arquitectura/Muros", "ARQUITECTURA::MUROS")]
    [InlineData(@"Arquitectura\Muros", "ARQUITECTURA::MUROS")]
    [InlineData("Arquitectura|Muros", "ARQUITECTURA::MUROS")]
    public void Normalize_reconciles_every_separator_a_round_trip_can_produce(string input, string expected)
    {
        Assert.Equal(expected, LayerPath.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::")]
    [InlineData("///")]
    public void Normalize_yields_empty_for_anything_that_names_no_layer(string? input)
    {
        Assert.Equal(string.Empty, LayerPath.Normalize(input));
    }

    [Fact]
    public void Normalize_drops_empty_segments_so_a_doubled_separator_makes_no_phantom_level()
    {
        Assert.Equal("A::B", LayerPath.Normalize("A::::B"));
        Assert.Equal("A::B", LayerPath.Normalize("A//B"));
    }

    [Fact]
    public void Normalize_trims_each_segment_rather_than_only_the_whole_path()
    {
        Assert.Equal("ARQ::MUROS", LayerPath.Normalize("  Arq  ::  Muros  "));
    }

    [Fact]
    public void Normalize_strips_brackets_because_they_carry_meaning_in_the_matchers()
    {
        Assert.Equal("MUROS", LayerPath.Normalize("[Muros]"));
    }

    [Fact]
    public void Split_returns_segments_outermost_first()
    {
        Assert.Equal(new[] { "A", "B", "C" }, LayerPath.Split("a::b::c"));
    }

    [Theory]
    [InlineData("Arquitectura::Muros::Exterior", "EXTERIOR")]
    [InlineData("Muros", "MUROS")]
    [InlineData(null, "")]
    public void Leaf_is_the_innermost_segment_because_parents_group_rather_than_identify(string? input, string expected)
    {
        Assert.Equal(expected, LayerPath.Leaf(input));
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var once = LayerPath.Normalize(@"Arq\Muros/Exterior");
        Assert.Equal(once, LayerPath.Normalize(once));
    }
}
