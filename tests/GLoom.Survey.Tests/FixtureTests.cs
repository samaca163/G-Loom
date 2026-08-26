using GLoom.Survey;
using Xunit;

namespace GLoom.Survey.Tests;

/// <summary>
/// The same table as tests/fixtures/make-survey-fixture.py, asserted against the built-in
/// schema. The fixture tells the architect what to expect on screen; this keeps that
/// promise honest when a rule moves.
/// </summary>
public class FixtureTests
{
    private static readonly SurveySchema Schema = SurveySchemaLoader.BuiltIn().Schema;
    private static readonly RuleMatcher Matcher = SurveySchemaLoader.BuiltIn().Matcher;

    public static TheoryData<string, string?, string?, string?> Cases => new()
    {
        // layer                             category     role        rule
        { "Muros",                           "wall",      "INTERIOR", "es-muro" },
        { "Muro Exterior",                   "wall",      "EXTERIOR", "es-muro-ext" },
        { "Losa de Entrepiso",               "floor",     null,       "es-piso" },
        { "Cielo Raso",                      "ceiling",   null,       "es-cielo" },
        { "Cubierta",                        "roof",      null,       "es-cubierta" },
        { "Puertas",                         "door",      null,       "es-puerta" },
        { "Ventanas",                        "window",    null,       "es-ventana" },
        { "Ambientes",                       "room",      null,       "es-ambiente" },
        { "Nivel 1",                         "level",     null,       "es-nivel" },
        { "Mobiliario",                      "furniture", null,       "es-mobiliario" },
        { "Exterior Walls",                  "wall",      "EXTERIOR", "en-wall-ext" },
        { "Ground Floor Slab",               "floor",     null,       "en-floor" },
        { "A-WALL-EXTR-E",                   "wall",      "EXTERIOR", "ncs-wall-ext" },
        { "A-WALL-D",                        "wall",      "INTERIOR", "ncs-wall" },
        { "A-COLS",                          "column",    null,       "ncs-column" },
        { "Arquitectura::Existente::Muros",  "wall",      "INTERIOR", "es-muro" },
        { "Cotas",                           null,        null,       null },
        { "Ejes",                            null,        null,       null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_fixture_classifies_the_way_its_table_says(string layer, string? category, string? role, string? rule)
    {
        var match = Matcher.Match(layer);

        if (category is null)
        {
            Assert.Null(match);
            return;
        }

        Assert.NotNull(match);
        Assert.Equal(category, match!.Category.Id);
        Assert.Equal(role, match.Role);
        Assert.Equal(rule, match.Rule.Id);
    }

    [Fact]
    public void The_fixture_leaves_exactly_two_layers_unmapped()
    {
        var layers = Cases.Select(row => (string)row[0]!).ToList();
        var unmapped = layers.Count(l => Matcher.Match(l) is null);

        Assert.Equal(18, layers.Count);
        Assert.Equal(2, unmapped);
    }

    [Fact]
    public void The_ncs_layers_carry_the_phase_their_status_code_declares()
    {
        Assert.Equal("EXISTING", Matcher.Match("A-WALL-EXTR-E")!.Phase);
        Assert.Equal("DEMOLISH", Matcher.Match("A-WALL-D")!.Phase);
    }

    [Fact]
    public void Every_classified_fixture_layer_produces_a_readable_state_key()
    {
        foreach (var row in Cases)
        {
            var match = Matcher.Match((string)row[0]!);
            if (match is null) continue;

            var record = SurveyRecordBuilder.Build(Schema, match, null, "abc123def456", "MILLIMETERS");
            var state = SurveySchemaJson.TryReadState(record.Pairs[^1].Value);

            Assert.NotNull(state);
            Assert.Equal(match.Rule.Id, state!.Rule);
        }
    }
}
