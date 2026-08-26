using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GLoom.Vcs;

/// <summary>
/// Git trailers as G-Loom writes and reads them: the last paragraph of a commit body,
/// every line of it shaped "Key: value". G-Loom's own keys all start with "Gloom-".
/// </summary>
public static class CommitTrailers
{
    private static readonly Regex TrailerLine = new(@"^([A-Za-z0-9][A-Za-z0-9-]*):\s*(.*)$", RegexOptions.Compiled);

    public sealed record Split(string Text, IReadOnlyDictionary<string, string> Trailers);

    public static Split Parse(string? body)
    {
        var none = new Split((body ?? "").Trim(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(body)) return none;

        var normalized = body.Replace("\r\n", "\n").TrimEnd();
        var cut = normalized.LastIndexOf("\n\n", StringComparison.Ordinal);
        var last = cut < 0 ? normalized : normalized[(cut + 2)..];

        var trailers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in last.Split('\n'))
        {
            var m = TrailerLine.Match(line.Trim());
            if (!m.Success) return none;
            trailers[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }
        if (trailers.Count == 0) return none;

        var text = cut < 0 ? "" : normalized[..cut].Trim();
        return new Split(text, trailers);
    }

    public static string Append(string? text, IEnumerable<KeyValuePair<string, string>> trailers)
    {
        var lines = new List<string>();
        foreach (var (k, v) in trailers)
            lines.Add($"{k}: {v.Replace('\n', ' ').Trim()}");
        var block = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(text) ? block : text.TrimEnd() + "\n\n" + block;
    }
}
