using GLoom.Survey;
using Xunit;
using static GLoom.Survey.Tests.SchemaBuilder;

namespace GLoom.Survey.Tests;

public class RuleMatcherTests
{
    [Fact]
    public void Exact_rules_compare_normalized_paths_so_the_map_absorbs_separator_mangling()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Exact, "arq/muros", "wall") }));

        Assert.NotNull(matcher.Match(@"Arq\Muros"));
        Assert.NotNull(matcher.Match("ARQ::MUROS"));
        Assert.Null(matcher.Match("Arq::Muros::Exterior"));
    }

    [Fact]
    public void Glob_stars_and_question_marks_translate_to_a_whole_path_match()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Glob, "*MURO*", "wall") }));

        Assert.NotNull(matcher.Match("Muros exteriores"));
        Assert.NotNull(matcher.Match("Arq::Muros"));
        Assert.Null(matcher.Match("Puertas"));
    }

    [Fact]
    public void A_glob_without_stars_does_not_match_a_longer_path()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Glob, "MURO", "wall") }));

        Assert.NotNull(matcher.Match("Muro"));
        Assert.Null(matcher.Match("Muros"));
    }

    [Fact]
    public void Glob_metacharacters_that_are_regex_syntax_are_escaped_not_interpreted()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Glob, "A.B", "wall") }));

        Assert.NotNull(matcher.Match("A.B"));
        Assert.Null(matcher.Match("AXB"));
    }

    [Fact]
    public void Regex_rules_match_case_insensitively_against_the_normalized_path()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Regex, "^muro[s]?$", "wall") }));

        Assert.NotNull(matcher.Match("Muro"));
        Assert.NotNull(matcher.Match("MUROS"));
        Assert.Null(matcher.Match("Muros exteriores"));
    }

    [Fact]
    public void A_malformed_regex_degrades_to_matching_nothing_rather_than_taking_the_solve_down()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Regex, "([unclosed", "wall") }));

        Assert.Null(matcher.Match("anything"));
        Assert.Null(matcher.Match("[unclosed"));
    }

    [Fact]
    public void Ncs_rules_match_the_stem_of_the_leaf_not_the_whole_path()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Ncs, "A-WALL", "wall") }));

        Assert.NotNull(matcher.Match("Existing::A-WALL-E"));
        Assert.Null(matcher.Match("A-WALL::Something"));
    }

    [Fact]
    public void An_ncs_pattern_can_require_minor_group_tokens()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Ncs, "A-WALL+EXTR", "wall") }));

        Assert.NotNull(matcher.Match("A-WALL-EXTR"));
        Assert.NotNull(matcher.Match("A-WALL-FULL-EXTR-E"));
        Assert.Null(matcher.Match("A-WALL-INTR"));
        Assert.Null(matcher.Match("A-WALL"));
    }

    [Fact]
    public void Rules_are_ordered_and_the_first_match_wins()
    {
        var matcher = new RuleMatcher(Schema(new[]
        {
            Rule("specific", RuleKind.Exact, "MUROS", "door"),
            Rule("broad", RuleKind.Glob, "*MURO*", "wall"),
        }));

        Assert.Equal("specific", matcher.Match("Muros")!.Rule.Id);
        Assert.Equal("broad", matcher.Match("Muros exteriores")!.Rule.Id);
    }

    [Fact]
    public void A_rule_targeting_an_undeclared_category_is_skipped_so_a_later_rule_still_gets_its_turn()
    {
        var matcher = new RuleMatcher(Schema(new[]
        {
            Rule("ghost", RuleKind.Glob, "*MURO*", "nonexistent"),
            Rule("real", RuleKind.Glob, "*MURO*", "wall"),
        }));

        Assert.Equal("real", matcher.Match("Muros")!.Rule.Id);
    }

    [Fact]
    public void Category_lookup_is_case_insensitive()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Glob, "*MURO*", "WALL") }));

        Assert.Equal("wall", matcher.Match("Muros")!.Category.Id);
    }

    [Fact]
    public void An_unmatched_layer_is_null_never_defaulted_to_a_generic_category()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Glob, "*MURO*", "wall") }));

        Assert.Null(matcher.Match("Something nobody mapped"));
        Assert.Null(matcher.Match(null));
        Assert.Null(matcher.Match("   "));
    }

    [Fact]
    public void The_rules_own_phase_wins_over_the_ncs_status_code()
    {
        var matcher = new RuleMatcher(Schema(new[]
        {
            Rule("r", RuleKind.Ncs, "A-WALL", "wall", phase: "DEMOLISH"),
        }));

        Assert.Equal("DEMOLISH", matcher.Match("A-WALL-E")!.Phase);
    }

    [Fact]
    public void An_ncs_status_code_supplies_the_phase_when_the_rule_declares_none()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Ncs, "A-WALL", "wall") }));

        Assert.Equal("EXISTING", matcher.Match("A-WALL-E")!.Phase);
        Assert.Equal("DEMOLISH", matcher.Match("A-WALL-D")!.Phase);
        Assert.Null(matcher.Match("A-WALL")!.Phase);
    }

    [Fact]
    public void A_non_ncs_rule_still_picks_up_a_phase_when_the_leaf_happens_to_parse()
    {
        // The NCS parse runs once per resolve regardless of rule kind, which is what lets a
        // glob-mapped project still get existing-versus-demolish for free.
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Glob, "*WALL*", "wall") }));

        Assert.Equal("DEMOLISH", matcher.Match("A-WALL-D")!.Phase);
    }

    [Fact]
    public void The_match_carries_the_rules_role_and_type_through()
    {
        var matcher = new RuleMatcher(Schema(new[]
        {
            Rule("r", RuleKind.Glob, "*MURO*", "wall", role: "EXTERIOR", type: "Brick 350"),
        }));

        var match = matcher.Match("Muros")!;
        Assert.Equal("EXTERIOR", match.Role);
        Assert.Equal("Brick 350", match.Type);
        Assert.Equal("MUROS", match.Layer);
    }

    [Fact]
    public void Results_are_memoized_per_normalized_path()
    {
        var matcher = new RuleMatcher(Schema(new[] { Rule("r", RuleKind.Glob, "*MURO*", "wall") }));

        var first = matcher.Match("Arq::Muros");
        var second = matcher.Match(@"arq\muros");

        Assert.Same(first, second);
    }

    [Fact]
    public void A_miss_is_memoized_too()
    {
        var matcher = new RuleMatcher(Schema(Array.Empty<SurveyRule>()));

        Assert.Null(matcher.Match("Muros"));
        Assert.Null(matcher.Match("Muros"));
    }
}
