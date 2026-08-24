using System.Collections.Generic;

namespace GLoom.Survey;

/// <summary>
/// Schema v1 - the contract between what an architect tags on a survey and what
/// Rhino.Inside.Revit can build from it. Names the categories, the fields each one
/// carries, and the rules that resolve a Rhino layer onto a category.
///
/// Backward compatibility follows the house rule: new fields are appended as trailing
/// optional parameters with `= null` defaults, so an older file parses into a newer
/// record and an older build ignores additions.
///
/// Schema identity is the content hash computed at load, never record equality - the
/// collection members here compare by reference, which is fine because nothing diffs
/// two schemas.
/// </summary>
public sealed record SurveySchema(
    int SchemaVersion,
    string Id,
    IReadOnlyList<FieldGroup> Core,
    IReadOnlyList<SurveyCategory> Categories,
    IReadOnlyList<SurveyRule> Rules,
    MaterialisePolicy Materialise = MaterialisePolicy.Full);

/// <summary>
/// A block of fields every surveyed element carries. <see cref="Id"/> is the middle
/// segment of the user-text key, so the nine groups sort into legible blocks in Rhino's
/// Attribute User Text page - which is the whole reason the keys have three segments.
/// </summary>
public sealed record FieldGroup(
    string Id,
    string Label,
    IReadOnlyList<SurveyField> Fields);

/// <summary>
/// One element category. <see cref="Id"/> is the middle key segment for this category's
/// own fields; <see cref="Revit"/> is the Revit category display name the collector
/// emits, which is what routes an element to the right RiR component downstream.
/// </summary>
public sealed record SurveyCategory(
    string Id,
    string Label,
    string Revit,
    IReadOnlyList<SurveyField> Fields,
    string? Uniformat = null,
    string? Omniclass = null);

public sealed record SurveyField(
    string Id,
    string Label,
    FieldType Type,
    bool Required = false,
    string? Unit = null,
    IReadOnlyList<string>? Values = null,
    string? Default = null,
    string? Revit = null,
    FieldSource Source = FieldSource.Human);

/// <summary>
/// One layer-to-category rule. Rules are ordered and the first match wins, so a specific
/// exact path can override a broad glob - and the order is visible in a git diff, which
/// matters when two people edit the map on different branches.
/// </summary>
public sealed record SurveyRule(
    string Id,
    RuleKind Kind,
    string Pattern,
    string Category,
    string? Role = null,
    string? Phase = null,
    string? Type = null);

public sealed record SchemaIssue(string Kind, string Where, string Message)
{
    public override string ToString() => $"{Kind} · {Where} · {Message}";
}

public enum FieldType { Text, Number, Integer, Bool, Enum, Date }

/// <summary>
/// Where a value comes from, and therefore who owns it. <see cref="Rule"/> fields are
/// machine-owned - G-Loom rewrites them whenever classification changes.
/// <see cref="Human"/> fields are created once with a placeholder and never modified
/// again; the architect owns them. This split is the entire conflict model.
/// </summary>
public enum FieldSource { Human, Rule }

public enum RuleKind { Exact, Glob, Regex, Ncs }

/// <summary>
/// <see cref="Full"/> writes every declared key so the Properties page reads as a form
/// to fill in - which is what a "metadata container" means. <see cref="Present"/> writes
/// only fields that resolved to a real value.
///
/// Under Full, absence is scoped to the object: an object with no keys was never
/// surveyed, while `unknown` on a field means surveyed-but-indeterminate and `n/a` means
/// surveyed-but-not-applicable.
/// </summary>
public enum MaterialisePolicy { Full, Present }

public enum WriteMode { Merge, Ensure, Replace }
