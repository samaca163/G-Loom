using GLoom.Survey;
using Xunit;
using static GLoom.Survey.Tests.SchemaBuilder;

namespace GLoom.Survey.Tests;

public class SurveyRecordTests
{
    private static readonly FieldGroup Identity = new("identity", "Identity", new[]
    {
        Field("category", source: FieldSource.Rule),
        Field("role", source: FieldSource.Rule),
        Field("type", source: FieldSource.Rule),
        Field("mark"),
    });

    private static readonly FieldGroup Phase = new("phase", "Phase", new[]
    {
        Field("status", FieldType.Enum, @default: "EXISTING", source: FieldSource.Rule),
    });

    private static SurveySchema WithCore(MaterialisePolicy materialise = MaterialisePolicy.Full) =>
        Schema(
            new[] { Rule("r", RuleKind.Glob, "*MURO*", "wall", role: "EXTERIOR") },
            new[] { Category("wall", Field("thickness", FieldType.Number), Field("bearing", @default: "UNKNOWN")) },
            new[] { Identity, Phase },
            materialise);

    private static SurveyRecord BuildFor(
        SurveySchema schema,
        IReadOnlyDictionary<string, string>? existing = null,
        string layer = "Muros")
    {
        var match = new RuleMatcher(schema).Match(layer)!;
        return SurveyRecordBuilder.Build(schema, match, existing, "abc123def456", "MILLIMETERS");
    }

    private static Dictionary<string, string> AsMap(SurveyRecord record) =>
        record.Pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Keys_are_always_three_dot_separated_segments_under_one_prefix()
    {
        foreach (var pair in BuildFor(WithCore()).Pairs)
        {
            Assert.StartsWith(SurveyKeys.Prefix, pair.Key, StringComparison.Ordinal);
            Assert.Equal(3, pair.Key.Split('.').Length);
        }
    }

    [Fact]
    public void Machine_owned_fields_come_from_the_match()
    {
        var map = AsMap(BuildFor(WithCore()));

        Assert.Equal("WALL", map[SurveyKeys.Category]);
        Assert.Equal("EXTERIOR", map[SurveyKeys.Role]);
    }

    [Fact]
    public void A_machine_owned_field_is_rewritten_even_when_the_object_already_carries_a_value()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SurveyKeys.Role] = "INTERIOR",
        };

        Assert.Equal("EXTERIOR", AsMap(BuildFor(WithCore(), existing))[SurveyKeys.Role]);
    }

    [Fact]
    public void A_human_owned_value_the_architect_typed_is_never_overwritten()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SVY.wall.thickness"] = "340",
            ["SVY.identity.mark"] = "W-03",
        };

        var map = AsMap(BuildFor(WithCore(), existing));
        Assert.Equal("340", map["SVY.wall.thickness"]);
        Assert.Equal("W-03", map["SVY.identity.mark"]);
    }

    [Fact]
    public void A_human_field_falls_back_to_the_schema_default_before_the_placeholder()
    {
        var map = AsMap(BuildFor(WithCore()));

        Assert.Equal("UNKNOWN", map["SVY.wall.bearing"]);
        Assert.Equal(SurveyKeys.Unknown, map["SVY.wall.thickness"]);
    }

    [Fact]
    public void A_machine_field_the_match_leaves_empty_falls_through_to_its_default()
    {
        // The rule declares no phase and the layer is not NCS, so status has nothing to be
        // taken from - and lands on EXISTING, which is the right guess for a survey.
        Assert.Equal("EXISTING", AsMap(BuildFor(WithCore()))[SurveyKeys.Phase]);
    }

    [Fact]
    public void A_blank_existing_value_is_treated_as_absent()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SVY.identity.mark"] = "   ",
        };

        Assert.Equal(SurveyKeys.Unknown, AsMap(BuildFor(WithCore(), existing))["SVY.identity.mark"]);
    }

    [Fact]
    public void Values_are_trimmed_so_a_stray_space_never_becomes_a_second_distinct_value()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SVY.wall.thickness"] = "  340  ",
        };

        Assert.Equal("340", AsMap(BuildFor(WithCore(), existing))["SVY.wall.thickness"]);
    }

    [Fact]
    public void Full_writes_every_declared_key_so_the_properties_page_reads_as_a_form()
    {
        var map = AsMap(BuildFor(WithCore()));

        // Five core fields, two category fields, one state key.
        Assert.Equal(8, map.Count);
        Assert.Contains("SVY.wall.thickness", map.Keys);
    }

    [Fact]
    public void Present_writes_only_fields_that_resolved_to_a_real_value()
    {
        var map = AsMap(BuildFor(WithCore(MaterialisePolicy.Present)));

        Assert.DoesNotContain("SVY.wall.thickness", map.Keys);
        Assert.DoesNotContain("SVY.identity.mark", map.Keys);
        Assert.Contains("SVY.wall.bearing", map.Keys);
        Assert.Contains(SurveyKeys.Category, map.Keys);
    }

    [Fact]
    public void Present_still_keeps_a_value_the_object_already_carries()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SVY.wall.thickness"] = "340",
        };

        Assert.Equal("340", AsMap(BuildFor(WithCore(MaterialisePolicy.Present), existing))["SVY.wall.thickness"]);
    }

    [Fact]
    public void The_state_key_carries_schema_rule_layer_and_unit_and_reads_back()
    {
        var map = AsMap(BuildFor(WithCore()));
        var state = SurveySchemaJson.TryReadState(map[SurveyKeys.State]);

        Assert.NotNull(state);
        Assert.Equal("test/1.0@abc123def456", state!.Schema);
        Assert.Equal("r", state.Rule);
        Assert.Equal("MUROS", state.Layer);
        Assert.Equal("MILLIMETERS", state.Unit);
    }

    [Fact]
    public void The_state_key_is_written_last_so_the_form_reads_before_the_plumbing()
    {
        Assert.Equal(SurveyKeys.State, BuildFor(WithCore()).Pairs[^1].Key);
    }

    [Fact]
    public void Merge_carries_foreign_keys_through_untouched_and_in_their_original_order()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Speckle.id"] = "abc",
            ["ClientRef"] = "PLOT-12",
            ["SVY.identity.role"] = "stale",
        };

        var record = BuildFor(WithCore(), existing);
        var final = SurveyRecordBuilder.Merge(existing, record.Pairs);

        Assert.Equal("Speckle.id", final[0].Key);
        Assert.Equal("abc", final[0].Value);
        Assert.Equal("ClientRef", final[1].Key);
        Assert.Equal("PLOT-12", final[1].Value);
    }

    [Fact]
    public void Merge_replaces_the_survey_keys_rather_than_leaving_a_stale_one_behind()
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SVY.identity.role"] = "stale",
            ["SVY.gone.key"] = "a field the schema no longer declares",
        };

        var record = BuildFor(WithCore(), existing);
        var final = SurveyRecordBuilder.Merge(existing, record.Pairs);

        Assert.DoesNotContain(final, p => p.Value == "stale");
        Assert.DoesNotContain(final, p => p.Key == "SVY.gone.key");
        Assert.Single(final, p => p.Key == SurveyKeys.Role);
    }

    [Fact]
    public void Merge_of_nothing_existing_is_just_the_record()
    {
        var record = BuildFor(WithCore());
        Assert.Equal(record.Pairs.Count, SurveyRecordBuilder.Merge(null, record.Pairs).Count);
    }

    [Theory]
    [InlineData("SVY.identity.role", true)]
    [InlineData("svy.identity.role", true)]
    [InlineData("Svy.anything", true)]
    [InlineData("Speckle.id", false)]
    [InlineData("SURVEY.role", false)]
    [InlineData(null, false)]
    public void Survey_key_detection_matches_rhinos_own_case_insensitive_comparison(string? key, bool expected)
    {
        Assert.Equal(expected, SurveyKeys.IsSurveyKey(key));
    }

    [Fact]
    public void The_record_reports_the_category_it_resolved_to()
    {
        Assert.Equal("wall", BuildFor(WithCore()).Category.Id);
    }
}
