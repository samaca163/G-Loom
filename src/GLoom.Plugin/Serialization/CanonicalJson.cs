using System.Text.Encodings.Web;
using System.Text.Json;

namespace GLoom.Serialization;

/// <summary>
/// JSON encoding policy for the canonical document. Indented (line-diff-friendly),
/// camelCase, never-omit-fields so the schema stays rigid.
/// </summary>
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(CanonicalDocument doc) =>
        JsonSerializer.Serialize(doc, Options);
}
