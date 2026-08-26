using GLoom.Serialization;

namespace GLoom.Core.Tests;

/// <summary>Builders for canonical documents small enough to read in a test.</summary>
internal static class Docs
{
    public static CanonicalDocument Doc(string name, params CanonicalObject[] objects) =>
        new(6, new DocumentMeta(name, ""), objects, Array.Empty<CanonicalGroup>());

    public static CanonicalObject Component(string id, string name, float x = 0, float y = 0,
        params CanonicalParameter[] inputs) =>
        new(id, "00000000-0000-0000-0000-00000000c0de", "component", name, name,
            new Pivot(x, y), inputs, Array.Empty<CanonicalParameter>());

    public static CanonicalObject Slider(string id, decimal value, float x = 0, float y = 0) =>
        new(id, "57da07bd-ecab-415d-9d86-af36d7073abc", "param", "Number Slider", "Slider",
            new Pivot(x, y), Array.Empty<CanonicalParameter>(), Array.Empty<CanonicalParameter>(),
            new PersistentData("slider", new SliderValue(value, 0, 100, 0, "integer")));

    public static CanonicalParameter Input(string id, string name, params string[] sources) =>
        new(id, name, name, "item", sources);

    public static string Guid(int n) => $"00000000-0000-0000-0000-{n:D12}";
}
