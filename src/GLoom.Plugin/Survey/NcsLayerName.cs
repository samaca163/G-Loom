using System.Text.RegularExpressions;

namespace GLoom.Survey;

/// <summary>
/// A layer name parsed against the US National CAD Standard / AIA layer grammar:
/// discipline, optional discipline modifier, a four-character major group, up to two
/// four-character minor groups, and an optional status field.
///
/// This is a matcher, never a source of truth. The NCS vocabulary is open by design -
/// any four characters is a legal user-defined major group - so a name alone cannot
/// prove what an element is. It earns its place because an NCS-compliant project
/// classifies with no map entries at all, and because the status field is the only
/// place a plan drawing usually records existing-versus-demolish.
/// </summary>
public sealed record NcsLayerName(
    string Discipline,
    string? Modifier,
    string Major,
    string? Minor1,
    string? Minor2,
    char? Status)
{
    // Minor groups are four characters and status is one, so the trailing optionals
    // cannot be confused for each other however many of them are present.
    private static readonly Regex Grammar = new(
        @"^([A-Z])([A-Z])?-([A-Z0-9]{4})(?:-([A-Z0-9]{4}))?(?:-([A-Z0-9]{4}))?(?:-([NEDFTMX1-9]))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string? leaf, out NcsLayerName? name)
    {
        name = null;
        if (string.IsNullOrWhiteSpace(leaf)) return false;

        var m = Grammar.Match(leaf.Trim());
        if (!m.Success) return false;

        name = new NcsLayerName(
            m.Groups[1].Value,
            m.Groups[2].Success ? m.Groups[2].Value : null,
            m.Groups[3].Value,
            m.Groups[4].Success ? m.Groups[4].Value : null,
            m.Groups[5].Success ? m.Groups[5].Value : null,
            m.Groups[6].Success ? m.Groups[6].Value[0] : null);
        return true;
    }

    /// <summary>
    /// The survey phase a status code implies, or null when the code carries no phase
    /// meaning. Digits are construction-phase numbers, not statuses, so they resolve to
    /// nothing rather than being guessed at.
    /// </summary>
    public static string? PhaseFor(char? status) => status switch
    {
        'E' => "EXISTING",
        'D' => "DEMOLISH",
        'N' => "NEW",
        'T' => "TEMPORARY",
        'F' => "NEW",
        'M' => "OTHER",
        'X' => "OTHER",
        _ => null,
    };

    /// <summary>
    /// The discipline-plus-major stem, which is what a rule matches on. Minor groups
    /// refine a category rather than choosing one.
    /// </summary>
    public string Stem => $"{Discipline}{Modifier}-{Major}";

    public bool HasMinor(string token) =>
        string.Equals(Minor1, token, System.StringComparison.Ordinal) ||
        string.Equals(Minor2, token, System.StringComparison.Ordinal);
}
