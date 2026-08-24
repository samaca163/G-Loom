using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GLoom.Survey;

public static class SurveySchemaJson
{
    /// <summary>
    /// Mirrors the canonical-document policy (indented, camelCase, omit nulls, relaxed
    /// escaping) so a schema diffs the same way a recipe does, with three additions the
    /// canonical writer does not need: this file is hand-authored, so enums read as
    /// names, comments and trailing commas are tolerated, and property matching is
    /// case-insensitive.
    /// </summary>
    public static readonly JsonSerializerOptions Options = Build(indented: true);

    private static readonly JsonSerializerOptions Compact = Build(indented: false);

    private static JsonSerializerOptions Build(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Write(SurveySchema schema) => JsonSerializer.Serialize(schema, Options);

    /// <summary>Compact - this one is a single value inside a user-text field.</summary>
    public static string WriteState(SurveyState state) => JsonSerializer.Serialize(state, Compact);

    public static SurveyState? TryReadState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SurveyState>(json!, Compact); }
        catch (JsonException) { return null; }
    }

    /// <summary>Null for anything that is not valid JSON of the expected shape.</summary>
    public static SurveySchema? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SurveySchema>(json!, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Semantic checks the deserializer cannot make. Returns findings rather than
    /// throwing: a schema with one bad rule should still classify everything else, and
    /// the findings are what tell the architect which line to fix.
    /// </summary>
    public static IReadOnlyList<SchemaIssue> Validate(SurveySchema? schema)
    {
        var issues = new List<SchemaIssue>();
        if (schema is null)
        {
            issues.Add(new SchemaIssue("unreadable", "schema", "Not valid JSON of the expected shape."));
            return issues;
        }

        if (string.IsNullOrWhiteSpace(schema.Id))
            issues.Add(new SchemaIssue("missing", "id", "Schema id is required, e.g. gloom-survey/1.0."));

        if (schema.Categories.Count == 0)
            issues.Add(new SchemaIssue("empty", "categories", "No categories declared - nothing can be classified."));

        var categoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in schema.Categories)
        {
            var where = $"categories/{category.Id}";
            if (string.IsNullOrWhiteSpace(category.Id))
                issues.Add(new SchemaIssue("missing", "categories", "A category has no id."));
            else if (!categoryIds.Add(category.Id))
                issues.Add(new SchemaIssue("duplicate", where, "Two categories share this id."));

            if (string.IsNullOrWhiteSpace(category.Revit))
                issues.Add(new SchemaIssue("missing", where, "No Revit category name - elements cannot be routed."));

            ValidateFields(issues, where, category.Id, category.Fields);
        }

        foreach (var group in schema.Core)
            ValidateFields(issues, $"core/{group.Id}", group.Id, group.Fields);

        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in schema.Rules)
        {
            var where = $"rules/{rule.Id}";
            if (string.IsNullOrWhiteSpace(rule.Id))
                issues.Add(new SchemaIssue("missing", "rules", "A rule has no id - ids make a reclassification legible in a diff."));
            else if (!ruleIds.Add(rule.Id))
                issues.Add(new SchemaIssue("duplicate", where, "Two rules share this id."));

            if (string.IsNullOrWhiteSpace(rule.Pattern))
                issues.Add(new SchemaIssue("missing", where, "Rule has no pattern."));

            if (!categoryIds.Contains(rule.Category))
                issues.Add(new SchemaIssue("unknown-category", where, $"Rule targets category '{rule.Category}', which is not declared."));

            if (rule.Kind == RuleKind.Regex && !string.IsNullOrWhiteSpace(rule.Pattern))
            {
                try { _ = new Regex(rule.Pattern); }
                catch (ArgumentException ex)
                {
                    issues.Add(new SchemaIssue("bad-pattern", where, $"Not a valid regular expression: {ex.Message}"));
                }
            }
        }

        return issues;
    }

    private static void ValidateFields(
        ICollection<SchemaIssue> issues, string where, string group, IReadOnlyList<SurveyField> fields)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Id))
            {
                issues.Add(new SchemaIssue("missing", where, "A field has no id."));
                continue;
            }
            if (!seen.Add(field.Id))
                issues.Add(new SchemaIssue("duplicate", $"{where}/{field.Id}", "Two fields share this id."));

            if (field.Type == FieldType.Enum && (field.Values is null || field.Values.Count == 0))
                issues.Add(new SchemaIssue("empty-enum", $"{where}/{field.Id}", "Enum field declares no values."));

            // The Properties page is a narrow two-column table; a key past this length
            // stops being readable at the width an architect actually docks it.
            var key = SurveyKeys.For(group, field.Id);
            if (key.Length > 40)
                issues.Add(new SchemaIssue("long-key", $"{where}/{field.Id}", $"Key '{key}' is {key.Length} characters; keep keys at 40 or fewer."));
        }
    }
}
