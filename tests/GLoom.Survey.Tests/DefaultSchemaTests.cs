using GLoom.Survey;
using Xunit;

namespace GLoom.Survey.Tests;

/// <summary>
/// The built-in vocabulary is what every project gets before it writes its own, so the
/// claims made about it in the docs are asserted here rather than remembered.
/// </summary>
public class DefaultSchemaTests
{
    private static readonly SurveySchema Schema =
        SurveySchemaJson.TryParse(DefaultSchema.Json) ?? throw new InvalidOperationException("built-in schema failed to parse");

    private static readonly RuleMatcher Matcher = new(Schema);

    [Fact]
    public void The_built_in_schema_parses()
    {
        Assert.Equal("gloom-survey/1.0", Schema.Id);
        Assert.Equal(1, Schema.SchemaVersion);
        Assert.Equal(MaterialisePolicy.Full, Schema.Materialise);
    }

    [Fact]
    public void The_built_in_schema_is_clean_against_its_own_validator()
    {
        var issues = SurveySchemaJson.Validate(Schema);
        Assert.True(issues.Count == 0, string.Join("\n", issues));
    }

    [Fact]
    public void Every_category_names_a_revit_category_because_that_is_what_routes_it_downstream()
    {
        Assert.All(Schema.Categories, c => Assert.False(string.IsNullOrWhiteSpace(c.Revit)));
    }

    [Fact]
    public void Only_the_four_documented_fields_are_machine_owned()
    {
        var owned = Schema.Core
            .SelectMany(g => g.Fields.Select(f => SurveyKeys.For(g.Id, f.Id)))
            .Concat(Schema.Categories.SelectMany(c => c.Fields.Select(f => SurveyKeys.For(c.Id, f.Id))))
            .Where(k => Machine(k))
            .OrderBy(k => k, StringComparer.Ordinal);

        Assert.Equal(
            new[] { SurveyKeys.Category, SurveyKeys.Role, SurveyKeys.Type, SurveyKeys.Phase }.OrderBy(k => k, StringComparer.Ordinal),
            owned);

        static bool Machine(string key) =>
            key == SurveyKeys.Category || key == SurveyKeys.Role || key == SurveyKeys.Type || key == SurveyKeys.Phase;
    }

    [Fact]
    public void The_four_rule_owned_keys_are_the_ones_the_builder_knows_how_to_fill()
    {
        var declared = Schema.Core
            .SelectMany(g => g.Fields.Where(f => f.Source == FieldSource.Rule).Select(f => SurveyKeys.For(g.Id, f.Id)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(new[] { SurveyKeys.Category, SurveyKeys.Role, SurveyKeys.Type, SurveyKeys.Phase }, StringComparer.Ordinal),
            declared);
    }

    [Theory]
    [InlineData("A-WALL-EXTR-E", "wall", "EXTERIOR")]
    [InlineData("A-WALL-E", "wall", "INTERIOR")]
    [InlineData("A-COLS", "column", null)]
    [InlineData("A-FLOR", "floor", null)]
    [InlineData("A-CLNG", "ceiling", null)]
    [InlineData("A-ROOF", "roof", null)]
    [InlineData("A-DOOR", "door", null)]
    [InlineData("A-GLAZ", "window", null)]
    [InlineData("A-AREA", "room", null)]
    [InlineData("A-FURN", "furniture", null)]
    public void An_ncs_compliant_project_classifies_with_no_map_entries_of_its_own(
        string layer, string category, string? role)
    {
        var match = Matcher.Match(layer);
        Assert.NotNull(match);
        Assert.Equal(category, match!.Category.Id);
        Assert.Equal(role, match.Role);
    }

    [Theory]
    [InlineData("Exterior Walls", "wall", "EXTERIOR")]
    [InlineData("Walls", "wall", "INTERIOR")]
    [InlineData("Columns", "column", null)]
    [InlineData("Ground Floor Slab", "floor", null)]
    [InlineData("Ceilings", "ceiling", null)]
    [InlineData("Roof", "roof", null)]
    [InlineData("Doors", "door", null)]
    [InlineData("Windows", "window", null)]
    [InlineData("Rooms", "room", null)]
    [InlineData("Level 01", "level", null)]
    [InlineData("Furniture", "furniture", null)]
    public void A_plain_english_layer_name_classifies(string layer, string category, string? role)
    {
        var match = Matcher.Match(layer);
        Assert.NotNull(match);
        Assert.Equal(category, match!.Category.Id);
        Assert.Equal(role, match.Role);
    }

    [Theory]
    [InlineData("Muros", "wall", "INTERIOR")]
    [InlineData("Muro Exterior", "wall", "EXTERIOR")]
    [InlineData("Columnas", "column", null)]
    [InlineData("Losa de Entrepiso", "floor", null)]
    [InlineData("Cielo Raso", "ceiling", null)]
    [InlineData("Techo", "ceiling", null)]
    [InlineData("Cubierta", "roof", null)]
    [InlineData("Puertas", "door", null)]
    [InlineData("Ventanas", "window", null)]
    [InlineData("Espacios", "room", null)]
    [InlineData("Ambientes", "room", null)]
    [InlineData("Nivel 1", "level", null)]
    [InlineData("Mobiliario", "furniture", null)]
    [InlineData("Muebles Fijos", "furniture", null)]
    public void A_plain_spanish_layer_name_classifies(string layer, string category, string? role)
    {
        var match = Matcher.Match(layer);
        Assert.NotNull(match);
        Assert.Equal(category, match!.Category.Id);
        Assert.Equal(role, match.Role);
    }

    [Fact]
    public void Losa_de_entrepiso_matches_on_piso_before_losa_gets_its_turn()
    {
        // ENTREPISO contains PISO, and es-piso is ordered ahead of es-losa. Both land on
        // floor so nothing is wrong today - pinned because it stops being harmless the
        // moment either rule carries a role the other does not.
        Assert.Equal("es-piso", Matcher.Match("Losa de Entrepiso")!.Rule.Id);
    }

    [Fact]
    public void A_nested_layer_classifies_on_its_leaf()
    {
        var match = Matcher.Match("Arquitectura::Existente::Muros");
        Assert.Equal("wall", match!.Category.Id);
    }

    [Fact]
    public void An_unmapped_layer_is_reported_rather_than_routed_to_generic_models()
    {
        Assert.Null(Matcher.Match("Cotas"));
        Assert.Null(Matcher.Match("Ejes"));
        Assert.Null(Matcher.Match("Notas del levantamiento"));
    }

    [Fact]
    public void The_generic_category_exists_but_no_rule_reaches_it()
    {
        Assert.Contains(Schema.Categories, c => c.Id == "generic");
        Assert.DoesNotContain(Schema.Rules, r => r.Category == "generic");
    }

    [Fact]
    public void A_wall_materialises_forty_two_keys_under_full()
    {
        var match = Matcher.Match("Muros")!;
        var record = SurveyRecordBuilder.Build(Schema, match, null, "abc123def456", "MILLIMETERS");

        Assert.Equal(42, record.Pairs.Count);
    }

    [Fact]
    public void The_same_wall_materialises_fifteen_keys_under_present()
    {
        var present = Schema with { Materialise = MaterialisePolicy.Present };
        var match = new RuleMatcher(present).Match("Muros")!;
        var record = SurveyRecordBuilder.Build(present, match, null, "abc123def456", "MILLIMETERS");

        Assert.Equal(15, record.Pairs.Count);
    }

    [Fact]
    public void Every_key_the_built_in_schema_can_produce_fits_the_properties_page()
    {
        foreach (var category in Schema.Categories)
        {
            var match = new LayerMatch(Schema.Rules[0], category, "X", null, null, null);
            var record = SurveyRecordBuilder.Build(Schema, match, null, "abc123def456", "MILLIMETERS");

            Assert.All(record.Pairs, p => Assert.True(p.Key.Length <= 40, $"{p.Key} is {p.Key.Length} characters"));
        }
    }

    [Fact]
    public void Phase_status_defaults_to_existing_which_is_the_right_guess_for_a_survey()
    {
        var match = Matcher.Match("Muros")!;
        var record = SurveyRecordBuilder.Build(Schema, match, null, "abc123def456", "MILLIMETERS");

        Assert.Contains(record.Pairs, p => p.Key == SurveyKeys.Phase && p.Value == "EXISTING");
    }

    [Fact]
    public void An_ncs_status_code_overrides_that_default()
    {
        var match = Matcher.Match("A-WALL-D")!;
        var record = SurveyRecordBuilder.Build(Schema, match, null, "abc123def456", "MILLIMETERS");

        Assert.Contains(record.Pairs, p => p.Key == SurveyKeys.Phase && p.Value == "DEMOLISH");
    }
}
