using GLoom.Survey;
using Xunit;
using static GLoom.Survey.Tests.SchemaBuilder;

namespace GLoom.Survey.Tests;

public class SurveySchemaJsonTests
{
    [Fact]
    public void A_schema_survives_a_write_and_read_round_trip()
    {
        var original = Schema(new[] { Rule("r", RuleKind.Ncs, "A-WALL", "wall", role: "EXTERIOR") });
        var parsed = SurveySchemaJson.TryParse(SurveySchemaJson.Write(original));

        Assert.NotNull(parsed);
        Assert.Equal(original.Id, parsed!.Id);
        Assert.Equal(RuleKind.Ncs, parsed.Rules[0].Kind);
        Assert.Equal("EXTERIOR", parsed.Rules[0].Role);
        Assert.Equal(original.Categories.Count, parsed.Categories.Count);
    }

    [Fact]
    public void Enums_are_written_as_camel_case_names_because_the_file_is_hand_authored()
    {
        var json = SurveySchemaJson.Write(Schema(new[] { Rule("r", RuleKind.Ncs, "A-WALL", "wall") }));

        Assert.Contains("\"kind\": \"ncs\"", json);
        Assert.DoesNotContain("\"kind\": 3", json);
    }

    [Fact]
    public void Nulls_are_omitted_so_the_file_diffs_the_way_a_recipe_does()
    {
        var json = SurveySchemaJson.Write(Schema(new[] { Rule("r", RuleKind.Glob, "*", "wall") }));

        Assert.DoesNotContain("\"role\": null", json);
        Assert.DoesNotContain("\"uniformat\": null", json);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated_because_a_person_maintains_this_file()
    {
        const string json = """
        {
          // the map for this project
          "schemaVersion": 1,
          "id": "hand/1.0",
          "core": [],
          "categories": [ { "id": "wall", "label": "Wall", "revit": "Walls", "fields": [] } ],
          "rules": [ { "id": "r", "kind": "glob", "pattern": "*MURO*", "category": "wall" }, ],
        }
        """;

        var parsed = SurveySchemaJson.TryParse(json);
        Assert.NotNull(parsed);
        Assert.Equal("hand/1.0", parsed!.Id);
    }

    [Fact]
    public void Property_names_match_case_insensitively()
    {
        const string json = """
        { "SchemaVersion": 1, "ID": "x/1.0", "Core": [], "Categories": [], "Rules": [] }
        """;

        Assert.Equal("x/1.0", SurveySchemaJson.TryParse(json)!.Id);
    }

    [Fact]
    public void An_absent_materialise_policy_defaults_to_full()
    {
        const string json = """
        { "schemaVersion": 1, "id": "x/1.0", "core": [], "categories": [], "rules": [] }
        """;

        Assert.Equal(MaterialisePolicy.Full, SurveySchemaJson.TryParse(json)!.Materialise);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"schemaVersion\": ")]
    [InlineData("[]")]
    public void Anything_that_is_not_a_schema_parses_to_null_rather_than_throwing(string? json)
    {
        Assert.Null(SurveySchemaJson.TryParse(json));
    }

    [Fact]
    public void State_round_trips_through_a_single_user_text_value()
    {
        var state = new SurveyState("gloom-survey/1.0@abc123", "es-muro", "ARQ::MUROS", "MILLIMETERS");
        var json = SurveySchemaJson.WriteState(state);

        Assert.DoesNotContain("\n", json);
        Assert.Equal(state, SurveySchemaJson.TryReadState(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ broken")]
    public void Unreadable_state_reads_back_as_null_rather_than_throwing(string? json)
    {
        Assert.Null(SurveySchemaJson.TryReadState(json));
    }

    [Fact]
    public void Validate_reports_an_unreadable_schema_rather_than_being_handed_one()
    {
        var issues = SurveySchemaJson.Validate(null);
        Assert.Single(issues);
        Assert.Equal("unreadable", issues[0].Kind);
    }

    [Fact]
    public void Validate_requires_an_id_and_at_least_one_category()
    {
        var issues = SurveySchemaJson.Validate(
            new SurveySchema(1, "  ", Array.Empty<FieldGroup>(), Array.Empty<SurveyCategory>(), Array.Empty<SurveyRule>()));

        Assert.Contains(issues, i => i.Kind == "missing" && i.Where == "id");
        Assert.Contains(issues, i => i.Kind == "empty" && i.Where == "categories");
    }

    [Fact]
    public void Validate_catches_duplicate_category_and_rule_ids()
    {
        var schema = Schema(
            new[] { Rule("dup", RuleKind.Glob, "*", "wall"), Rule("dup", RuleKind.Glob, "*", "wall") },
            new[] { Category("wall"), Category("wall") });

        var issues = SurveySchemaJson.Validate(schema);
        Assert.Contains(issues, i => i.Kind == "duplicate" && i.Where == "categories/wall");
        Assert.Contains(issues, i => i.Kind == "duplicate" && i.Where == "rules/dup");
    }

    [Fact]
    public void Validate_catches_a_rule_pointing_at_a_category_nobody_declared()
    {
        var issues = SurveySchemaJson.Validate(Schema(new[] { Rule("r", RuleKind.Glob, "*", "ceiling") }));

        Assert.Contains(issues, i => i.Kind == "unknown-category" && i.Where == "rules/r");
    }

    [Fact]
    public void Validate_catches_a_rule_with_no_pattern_and_no_id()
    {
        var issues = SurveySchemaJson.Validate(Schema(new[] { Rule("", RuleKind.Glob, "", "wall") }));

        Assert.Contains(issues, i => i.Kind == "missing" && i.Where == "rules");
        Assert.Contains(issues, i => i.Message.Contains("no pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_catches_a_regex_pattern_that_will_never_compile()
    {
        var issues = SurveySchemaJson.Validate(Schema(new[] { Rule("r", RuleKind.Regex, "([unclosed", "wall") }));

        Assert.Contains(issues, i => i.Kind == "bad-pattern" && i.Where == "rules/r");
    }

    [Fact]
    public void Validate_only_pattern_checks_rules_that_are_regexes()
    {
        var issues = SurveySchemaJson.Validate(Schema(new[] { Rule("r", RuleKind.Glob, "([unclosed", "wall") }));

        Assert.DoesNotContain(issues, i => i.Kind == "bad-pattern");
    }

    [Fact]
    public void Validate_catches_an_enum_field_that_declares_no_values()
    {
        var schema = Schema(
            new[] { Rule("r", RuleKind.Glob, "*", "wall") },
            new[] { Category("wall", Field("bearing", FieldType.Enum)) });

        Assert.Contains(SurveySchemaJson.Validate(schema), i => i.Kind == "empty-enum");
    }

    [Fact]
    public void Validate_catches_a_key_too_long_for_the_properties_page()
    {
        var schema = Schema(
            new[] { Rule("r", RuleKind.Glob, "*", "wall") },
            new[] { Category("wall", Field("aFieldNameFarTooLongToReadInThatNarrowTable")) });

        Assert.Contains(SurveySchemaJson.Validate(schema), i => i.Kind == "long-key");
    }

    [Fact]
    public void Validate_catches_duplicate_field_ids_within_one_group()
    {
        var schema = Schema(
            new[] { Rule("r", RuleKind.Glob, "*", "wall") },
            new[] { Category("wall", Field("height"), Field("height")) });

        Assert.Contains(SurveySchemaJson.Validate(schema), i => i.Kind == "duplicate" && i.Where.EndsWith("/height", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_checks_core_groups_as_well_as_categories()
    {
        var schema = Schema(
            new[] { Rule("r", RuleKind.Glob, "*", "wall") },
            null,
            new[] { new FieldGroup("phase", "Phase", new[] { Field("status", FieldType.Enum) }) });

        Assert.Contains(SurveySchemaJson.Validate(schema), i => i.Kind == "empty-enum" && i.Where.StartsWith("core/phase", StringComparison.Ordinal));
    }

    [Fact]
    public void A_findings_line_reads_as_one_string()
    {
        Assert.Equal("bad-pattern · rules/r · nope", new SchemaIssue("bad-pattern", "rules/r", "nope").ToString());
    }
}
