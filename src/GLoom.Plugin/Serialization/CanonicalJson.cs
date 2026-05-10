using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GLoom.Serialization;

/// <summary>
/// JSON encoding policy for the canonical document. Indented (line-diff-friendly),
/// camelCase, and null-fields-omitted so optional schema additions
/// (PersistentData, future per-mode fields) don't write null walls into
/// every document. Existing required fields are non-nullable so this only
/// affects newly-added optional ones.
/// </summary>
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(CanonicalDocument doc) =>
        JsonSerializer.Serialize(doc, Options);
}
