using System;
using System.Text.Json;

namespace GLoom.Serialization;

/// <summary>
/// What we capture into a tag's annotated message at tag time. Schema is
/// versioned so we can add per-mode fields (AEC submittal info, product
/// cert, release notes) in later phases without breaking older readers.
/// </summary>
public sealed record TagMetadata(
    int SchemaVersion,
    string TagName,
    string CommitSha,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    Toolchain Toolchain);

/// <summary>
/// Versions of the runtime that produced the tagged recipe. The point is
/// decade-horizon recoverability: in 2032 a reader needs to know which
/// Rhino / Grasshopper / RhinoInsideRevit / G-Loom build was used so the
/// recipe can be rerun (or its caveats understood).
/// </summary>
public sealed record Toolchain(
    string Rhino,
    string Grasshopper,
    string? RhinoInsideRevit,
    string Gloom);

public static class TagMetadataJson
{
    public static string Write(TagMetadata m) =>
        JsonSerializer.Serialize(m, CanonicalJson.Options);

    /// <summary>
    /// Best-effort parse of the tag message body. Returns null for
    /// lightweight tags (no message), tags created before this feature, or
    /// any message that isn't valid JSON of the expected shape. The first
    /// `{` is the JSON anchor so a tag whose message has a leading line
    /// (e.g. "gloom-tag-metadata-v1\n{ ... }") still parses.
    /// </summary>
    public static TagMetadata? TryRead(string? messageBody)
    {
        if (string.IsNullOrWhiteSpace(messageBody)) return null;
        var idx = messageBody.IndexOf('{');
        if (idx < 0) return null;
        try
        {
            return JsonSerializer.Deserialize<TagMetadata>(
                messageBody.AsSpan(idx),
                CanonicalJson.Options);
        }
        catch
        {
            return null;
        }
    }
}
