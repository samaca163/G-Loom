using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GLoom.Serialization;

/// <summary>
/// Schema v2: toolchain (always) plus an always-available free-text Notes
/// field and three optional mode-aware sub-records. The mode sub-records
/// are independent - a tag can carry any combination, e.g. AEC + release
/// for a milestone that also produced a tooled output.
///
/// Backward compatibility: v1 messages (no Notes / Aec / Product / Release)
/// deserialize cleanly into this record because all the new params have
/// `= null` defaults; System.Text.Json calls the primary constructor with
/// defaults for missing JSON properties.
/// </summary>
public sealed record TagMetadata(
    int SchemaVersion,
    string TagName,
    string CommitSha,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    Toolchain Toolchain,
    string? Notes = null,
    AecMetadata? Aec = null,
    ProductMetadata? Product = null,
    ReleaseMetadata? Release = null);

public sealed record Toolchain(
    string Rhino,
    string Grasshopper,
    string? RhinoInsideRevit,
    string Gloom);

public sealed record AecMetadata(
    string? Phase,
    string? Submittal,
    string? SheetSet,
    string? Notes);

public sealed record ProductMetadata(
    string? Sku,
    string? Variant,
    string? Notes);

public sealed record ReleaseMetadata(
    string? Version,
    string? Notes);

public static class TagMetadataJson
{
    /// <summary>
    /// Tag metadata is written into a git tag message. Compact-but-readable:
    /// indented + camelCase, but null fields are omitted so an annotated
    /// tag created with only a name + notes doesn't produce a wall of
    /// `null`s under `git show &lt;tag&gt;`.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(TagMetadata m) =>
        JsonSerializer.Serialize(m, Options);

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
                Options);
        }
        catch
        {
            return null;
        }
    }
}
