using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace GLoom.Survey;

public sealed record LayerMatch(
    SurveyRule Rule,
    SurveyCategory Category,
    string Layer,
    string? Role,
    string? Phase,
    string? Type);

/// <summary>
/// Resolves a normalized layer path to a category by walking the schema's rules in
/// order, first match wins. One matcher per loaded schema, so results memoize against
/// the schema that produced them without a composite key.
/// </summary>
public sealed class RuleMatcher
{
    private readonly SurveySchema _schema;
    private readonly Dictionary<string, SurveyCategory> _categories;
    private readonly Dictionary<string, Regex> _compiled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LayerMatch?> _memo = new(StringComparer.Ordinal);

    public RuleMatcher(SurveySchema schema)
    {
        _schema = schema;
        _categories = new Dictionary<string, SurveyCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in schema.Categories)
            _categories[category.Id] = category;
    }

    /// <summary>
    /// Null when nothing matched. An unmatched layer is never defaulted to a generic
    /// category - a Revit model full of anonymous Generic Models is the worst outcome
    /// of a survey pipeline, because nobody catches it until QA.
    /// </summary>
    public LayerMatch? Match(string? layerFullPath)
    {
        var path = LayerPath.Normalize(layerFullPath);
        if (_memo.TryGetValue(path, out var cached)) return cached;

        var match = Resolve(path);
        _memo[path] = match;
        return match;
    }

    private LayerMatch? Resolve(string path)
    {
        if (path.Length == 0) return null;

        var leaf = LayerPath.Leaf(path);
        NcsLayerName.TryParse(leaf, out var ncs);

        foreach (var rule in _schema.Rules)
        {
            if (!Matches(rule, path, leaf, ncs)) continue;
            if (!_categories.TryGetValue(rule.Category, out var category)) continue;

            // A rule's own phase wins; otherwise an NCS status code supplies it. That
            // fallback is why NCS parsing is worth keeping around at all.
            var phase = rule.Phase ?? NcsLayerName.PhaseFor(ncs?.Status);
            return new LayerMatch(rule, category, path, rule.Role, phase, rule.Type);
        }
        return null;
    }

    private bool Matches(SurveyRule rule, string path, string leaf, NcsLayerName? ncs) => rule.Kind switch
    {
        RuleKind.Exact => string.Equals(path, LayerPath.Normalize(rule.Pattern), StringComparison.Ordinal),
        RuleKind.Glob => GlobFor(rule.Pattern).IsMatch(path),
        RuleKind.Regex => RegexFor(rule.Pattern).IsMatch(path),
        RuleKind.Ncs => MatchesNcs(rule.Pattern, ncs),
        _ => false,
    };

    /// <summary>
    /// An NCS pattern is the discipline-plus-major stem, optionally followed by
    /// `+MINOR` tokens that must all be present. `A-WALL+EXTR` therefore selects
    /// exterior walls without caring which other minor groups the layer carries.
    /// </summary>
    private static bool MatchesNcs(string pattern, NcsLayerName? ncs)
    {
        if (ncs is null || string.IsNullOrWhiteSpace(pattern)) return false;

        var parts = pattern.ToUpperInvariant().Split('+');
        if (!string.Equals(ncs.Stem, parts[0].Trim(), StringComparison.Ordinal)) return false;

        for (var i = 1; i < parts.Length; i++)
        {
            var token = parts[i].Trim();
            if (token.Length > 0 && !ncs.HasMinor(token)) return false;
        }
        return true;
    }

    private Regex GlobFor(string pattern)
    {
        var key = "g:" + pattern;
        if (_compiled.TryGetValue(key, out var cached)) return cached;

        var sb = new StringBuilder("^");
        foreach (var ch in LayerPath.Normalize(pattern))
        {
            if (ch == '*') sb.Append(".*");
            else if (ch == '?') sb.Append('.');
            else sb.Append(Regex.Escape(ch.ToString()));
        }
        sb.Append('$');

        var regex = new Regex(sb.ToString(), RegexOptions.CultureInvariant);
        _compiled[key] = regex;
        return regex;
    }

    private Regex RegexFor(string pattern)
    {
        var key = "r:" + pattern;
        if (_compiled.TryGetValue(key, out var cached)) return cached;

        // A malformed pattern in a hand-edited map must not take the whole solve down;
        // it degrades to "matches nothing" and the schema validator reports it.
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            regex = new Regex("(?!)", RegexOptions.CultureInvariant);
        }
        _compiled[key] = regex;
        return regex;
    }
}
