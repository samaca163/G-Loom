using GLoom.Survey;

namespace GLoom.Survey.Tests;

/// <summary>
/// Minimal schemas built in code, so a matcher or record test states exactly the rules it
/// depends on. Tests that must hold against the shipped vocabulary use
/// <see cref="DefaultSchemaTests"/> instead.
/// </summary>
internal static class SchemaBuilder
{
    public static SurveyCategory Category(string id, params SurveyField[] fields) =>
        new(id, id, id + "s", fields);

    public static SurveyField Field(
        string id,
        FieldType type = FieldType.Text,
        string? @default = null,
        FieldSource source = FieldSource.Human,
        IReadOnlyList<string>? values = null) =>
        new(id, id, type, Required: false, Unit: null, Values: values, Default: @default, Revit: null, Source: source);

    public static SurveySchema Schema(
        IReadOnlyList<SurveyRule> rules,
        IReadOnlyList<SurveyCategory>? categories = null,
        IReadOnlyList<FieldGroup>? core = null,
        MaterialisePolicy materialise = MaterialisePolicy.Full) =>
        new(1, "test/1.0",
            core ?? Array.Empty<FieldGroup>(),
            categories ?? new[] { Category("wall"), Category("door") },
            rules,
            materialise);

    public static SurveyRule Rule(
        string id,
        RuleKind kind,
        string pattern,
        string category,
        string? role = null,
        string? phase = null,
        string? type = null) =>
        new(id, kind, pattern, category, role, phase, type);
}
