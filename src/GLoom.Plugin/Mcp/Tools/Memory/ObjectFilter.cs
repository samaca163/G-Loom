using System;
using System.Linq;
using GLoom.Mcp.Protocol;
using GLoom.Serialization;

namespace GLoom.Mcp.Tools.Memory;

/// <summary>The object filter the recipe and live readers share, so a "kind" or "query"
/// means the same thing to gloom_read_version and gloom_read_document.</summary>
internal static class ObjectFilter
{
    public static readonly string[] Kinds = { "component", "param" };

    public const string KindDescription =
        "Only objects of this kind: \"component\" (has inputs and outputs) or \"param\" (a free-floating " +
        "slider, panel, toggle, value list, swatch or other parameter). Omit for both.";

    /// <summary>Null for a blank kind; the trimmed kind when it is one of <see cref="Kinds"/>.</summary>
    public static string? ValidateKind(string? kind)
    {
        var k = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
        if (k is not null && !Kinds.Contains(k, StringComparer.OrdinalIgnoreCase))
            throw new ToolArgumentException(
                $"\"kind\" must be \"component\" or \"param\" (got \"{k}\"); sliders, panels, toggles and value lists are " +
                "params, so use \"query\" to narrow by name.");
        return k;
    }

    public static bool Matches(CanonicalObject o, string q) =>
        o.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || o.Nickname.Contains(q, StringComparison.OrdinalIgnoreCase)
        || o.InstanceGuid.Contains(q, StringComparison.OrdinalIgnoreCase);
}
