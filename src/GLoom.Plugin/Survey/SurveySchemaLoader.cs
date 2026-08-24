using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GLoom.Vcs;

namespace GLoom.Survey;

public sealed record LoadedSchema(
    SurveySchema Schema,
    RuleMatcher Matcher,
    string Hash,
    string Source,
    bool IsBuiltIn,
    IReadOnlyList<SchemaIssue> Issues)
{
    public string Version => $"{Schema.Id}@{Hash}";
}

/// <summary>
/// Finds, parses and caches the survey schema. A project's own file wins; the built-in
/// default is the fallback so the components work in any Rhino with no setup.
///
/// Reads are memoized behind a stat key the way <see cref="DocumentTracker"/> memoizes
/// its commit match: a schema is read once per edit, never once per solve.
/// </summary>
public static class SurveySchemaLoader
{
    public const string FolderName = ".gloom";
    public const string FileName = "survey-schema.json";

    // Grasshopper solves on the UI thread, so plain fields need no synchronisation -
    // the same reasoning DocumentTracker records for its own caches.
    private static CacheKey? _key;
    private static LoadedSchema? _cached;
    private static LoadedSchema? _builtIn;

    private sealed record CacheKey(string Path, long Length, long WriteTicks);

    /// <summary>
    /// <paramref name="explicitPath"/> wins when given. Otherwise the schema is looked
    /// for at &lt;project root&gt;/.gloom/survey-schema.json, resolved from
    /// <paramref name="startPath"/> - normally the host definition's own file, so the
    /// answer is machine-independent in every clone.
    /// </summary>
    public static LoadedSchema Load(string? explicitPath, string? startPath, bool force = false)
    {
        var path = Resolve(explicitPath, startPath);
        if (path is null) return BuiltIn();

        var key = new CacheKey(path, StatLength(path), StatWriteTicks(path));
        if (!force && _cached is not null && key == _key) return _cached;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as absent: fall back so the definition keeps
            // solving, but say which file could not be read.
            var fallback = BuiltIn();
            var issues = new List<SchemaIssue>(fallback.Issues)
            {
                new("unreadable", path, ex.Message),
            };
            return fallback with { Issues = issues };
        }

        var parsed = SurveySchemaJson.TryParse(text);
        var findings = SurveySchemaJson.Validate(parsed);

        if (parsed is null)
        {
            var fallback = BuiltIn();
            var issues = new List<SchemaIssue>(findings) { new("fallback", path, "Using the built-in schema instead.") };
            var result = fallback with { Issues = issues };
            _key = key;
            _cached = result;
            return result;
        }

        var loaded = new LoadedSchema(parsed, new RuleMatcher(parsed), HashOf(text), path, false, findings);
        _key = key;
        _cached = loaded;
        return loaded;
    }

    public static LoadedSchema BuiltIn()
    {
        if (_builtIn is not null) return _builtIn;

        var schema = SurveySchemaJson.TryParse(DefaultSchema.Json)
            ?? throw new InvalidOperationException("The built-in survey schema failed to parse.");

        _builtIn = new LoadedSchema(
            schema,
            new RuleMatcher(schema),
            HashOf(DefaultSchema.Json),
            "built-in",
            true,
            SurveySchemaJson.Validate(schema));
        return _builtIn;
    }

    /// <summary>The path a project would put its schema at, whether or not it exists.</summary>
    public static string? ExpectedPathFor(string? startPath)
    {
        var root = RepoDiscovery.FindRepoRoot(startPath, allowMissingStart: true);
        return root is null ? null : Path.Combine(root, FolderName, FileName);
    }

    private static string? Resolve(string? explicitPath, string? startPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var trimmed = explicitPath!.Trim().Trim('"');
            return File.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;
        }

        var expected = ExpectedPathFor(startPath);
        return expected is not null && File.Exists(expected) ? expected : null;
    }

    /// <summary>
    /// Twelve hex characters of SHA-256 over the file text. Short enough to sit inside a
    /// user-text value on every object, long enough that two schemas never collide in
    /// practice - and computed in process, because this path must never spawn git.
    /// </summary>
    private static string HashOf(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(12);
        for (var i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static long StatLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return -1; }
    }

    private static long StatWriteTicks(string path)
    {
        try { return new FileInfo(path).LastWriteTimeUtc.Ticks; }
        catch (Exception) { return 0; }
    }
}
