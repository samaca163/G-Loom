using System;
using System.IO;
using System.Text.Json;

namespace GLoom.Mcp.State;

/// <summary>
/// An open agent edit: the commit the definition stood at before the agent touched it, and
/// who is touching it. Held so that whatever the agent does next - through G-Loom's own
/// tools or through another server editing the same canvas - can be shown as a diff against
/// the checkpoint and rolled back to it in one move.
/// </summary>
public sealed record EditEnvelope(
    string RepoRoot,
    string DefinitionPath,
    string CheckpointSha,
    string? Agent,
    string? Session,
    string? Intent,
    DateTimeOffset OpenedAt)
{
    /// <summary>Two agents (or two sessions of one agent) must not share an envelope: the
    /// commit at the end is attributed to whoever opened it.</summary>
    public bool OpenedBy(string? agent, string? session) =>
        string.Equals(Agent ?? "", agent ?? "", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Session ?? "", session ?? "", StringComparison.Ordinal);

    public string Describe() =>
        (Agent is { Length: > 0 } a ? a : "an agent")
        + (Intent is { Length: > 0 } i ? $" (\"{i}\")" : "");
}

/// <summary>
/// One envelope per Rhino, on disk because the endpoint is stateless and each request
/// arrives on its own thread. Writes are whole-file and atomic; the dispatcher already
/// serializes tool calls, and the lock covers the panel reading it to render its row.
/// </summary>
public static class EnvelopeStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static EditEnvelope? Current
    {
        get
        {
            lock (Gate)
            {
                try
                {
                    var path = McpPaths.EnvelopeFile;
                    if (!File.Exists(path)) return null;
                    return JsonSerializer.Deserialize<EditEnvelope>(File.ReadAllText(path), Options);
                }
                catch
                {
                    // A malformed envelope must not wedge every write tool; treat it as none.
                    return null;
                }
            }
        }
    }

    public static void Open(EditEnvelope envelope)
    {
        lock (Gate)
        {
            McpPaths.Ensure();
            var tmp = McpPaths.EnvelopeFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(envelope, Options));
            File.Move(tmp, McpPaths.EnvelopeFile, overwrite: true);
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            try { File.Delete(McpPaths.EnvelopeFile); } catch { }
        }
    }
}
