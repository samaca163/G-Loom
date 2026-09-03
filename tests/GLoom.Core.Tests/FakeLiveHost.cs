using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Live;
using GLoom.Serialization;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

/// <summary>
/// An ILiveHost with a small canned world and two switches per method: throw a
/// TimeoutException (the UI thread was busy) or a ToolArgumentException (bad input), so the
/// tools' error paths are exercised without Grasshopper.
/// </summary>
internal sealed class FakeLiveHost : ILiveHost
{
    public List<LiveDocumentInfo> DocumentList { get; } = new();
    public LiveDocument? Document { get; set; }
    public LiveOutputs? Outputs { get; set; }
    public SolveReport? Report { get; set; }
    public List<CatalogueCategory> CategoryList { get; } = new();
    public List<CatalogueEntry> SearchResults { get; } = new();
    public CatalogueDescription? Description { get; set; }
    public CanvasImage? Image { get; set; }
    public RhinoContext? ContextValue { get; set; }

    /// <summary>Method names that throw TimeoutException.</summary>
    public HashSet<string> Timeouts { get; } = new(StringComparer.Ordinal);

    /// <summary>Method names that throw ToolArgumentException, with the message.</summary>
    public Dictionary<string, string> Rejections { get; } = new(StringComparer.Ordinal);

    public List<(string Method, object?[] Args)> Calls { get; } = new();

    public int CallsTo(string method) => Calls.Count(c => c.Method == method);
    public object?[] ArgsOf(string method) => Calls.Last(c => c.Method == method).Args;

    public const string TrackedGh = "Coding/tower.gh";
    public static readonly string ErrorGuid = Guid(2), WarningGuid = Guid(3), SilentGuid = Guid(4);

    /// <summary>Two documents (a tracked active one inside <paramref name="repo"/>, an unsaved one),
    /// a five-object structure with an error, a warning and an object without runtime, and canned
    /// answers for every other method.</summary>
    public static FakeLiveHost Canned(GitRepo repo)
    {
        var gh = repo.Write(TrackedGh, "gh bytes");
        var host = new FakeLiveHost();
        var active = new LiveDocumentInfo(gh, "tower", true, true, true, 5, "PostProcess", 12.5, 1, 1);
        host.DocumentList.Add(active);
        host.DocumentList.Add(new LiveDocumentInfo(null, "Untitled", false, false, true, 0, "PreProcess", 0, 0, 0));

        var structure = Doc("tower",
            Slider(Guid(1), 5),
            Component(Guid(2), "Circle", 10, 10, Input(Guid(20), "R", Guid(1))),
            Component(Guid(3), "Loft", 20, 20),
            Component(Guid(4), "Extrude", 30, 30),
            Slider(Guid(5), 7));
        var runtime = new List<RuntimeObject>
        {
            Runtime(Guid(1), "blank", Array.Empty<RuntimeMessage>(),
                outputs: new[] { new OutputSummary(Guid(1), "Number Slider", "Slider", "Number", 1, 1, new[] { "5" }) }),
            Runtime(Guid(2), "error", new[] { new RuntimeMessage("error", "Input parameter R failed to collect data"), new RuntimeMessage("error", "Radius must be positive") },
                inputs: new[] { new InputSummary(Guid(20), "Radius", "R", "Number", 1, 1, false) },
                outputs: new[] { new OutputSummary(Guid(21), "Circle", "C", "Curve", 0, 0, Array.Empty<string>()) }),
            Runtime(Guid(3), "warning", new[] { new RuntimeMessage("warning", "Loft needs at least two curves") }),
            Runtime(Guid(5), "remark", new[] { new RuntimeMessage("remark", "fine") },
                outputs: new[] { new OutputSummary(Guid(5), "Number Slider", "Slider", "Number", 1, 1, new[] { "7" }) }),
        };
        host.Document = new LiveDocument(active, structure, runtime);

        host.Outputs = new LiveOutputs(
            new ObjectRef(Guid(2), "Circle", "Circle", "component"),
            new[]
            {
                new OutputData(Guid(21), "Circle", "C", "Curve", 3, 2, new[]
                {
                    new DataBranch("{0;0}", 2, new[]
                    {
                        new DataItem("Curve", "Closed planar curve", new[] { -1.0, -1.0, 0.0 }, new[] { 1.0, 1.0, 0.0 }),
                        new DataItem("Curve", "Closed planar curve", new[] { -2.0, -2.0, 0.0 }, new[] { 2.0, 2.0, 0.0 }),
                    }),
                    new DataBranch("{0;1}", 1, new[] { new DataItem("Number", "3.5") }),
                }, false),
            });

        host.Report = new SolveReport(true, false, false, 42.5, "PostProcess", 5,
            new[] { new ObjectProblem(Guid(2), "Circle", "Circle", new[] { new RuntimeMessage("error", "Radius must be positive") }) },
            new[] { new ObjectProblem(Guid(3), "Loft", "Loft", new[] { new RuntimeMessage("warning", "Loft needs at least two curves") }) });

        host.CategoryList.Add(new CatalogueCategory("Params", new[] { "Input", "Primitive" }, 40));
        host.CategoryList.Add(new CatalogueCategory("Curve", new[] { "Primitive" }, 12));

        var core = new LibraryInfo("Grasshopper", "8.0", "McNeel", true);
        host.SearchResults.Add(Entry(Guid(101), "Circle", "Curve", "Primitive", core, 0.5));
        host.SearchResults.Add(Entry(Guid(102), "Arc", "Curve", "Primitive", core, 0.9));
        host.SearchResults.Add(Entry(Guid(103), "Number Slider", "Params", "Input", core, 0.7));

        host.Description = new CatalogueDescription(
            Entry(Guid(101), "Circle", "Curve", "Primitive", core),
            new[] { new ParamDescription("Plane", "P", "Base plane", "Plane", "item", false), new ParamDescription("Radius", "R", "Radius", "Number", "item", false) },
            new[] { new ParamDescription("Circle", "C", "Resulting circle", "Curve", "item", false) },
            null,
            new[] { "circle", "round" });

        host.Image = new CanvasImage(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }, 800, 600, 1.5, 10, 20, 300, 200, 5);

        host.ContextValue = new RhinoContext("8.15.25", "Rhino", false, null, "8.15.0", "0.3.0-mcp.3",
            new ActiveModel("C:/models/site.3dm", "site", "Millimeters", 0.001, 10, 3, false), 2, gh);
        return host;
    }

    public static CatalogueEntry Entry(string guid, string name, string category, string sub, LibraryInfo lib, double? score = null) =>
        new(guid, name, name[..Math.Min(3, name.Length)], $"{name} description", category, sub, "primary", false, "component", lib, score);

    private static RuntimeObject Runtime(string guid, string level, IReadOnlyList<RuntimeMessage> messages,
        IReadOnlyList<InputSummary>? inputs = null, IReadOnlyList<OutputSummary>? outputs = null) =>
        new(guid, level, messages, false, null, "Computed", 1.5,
            inputs ?? Array.Empty<InputSummary>(), outputs ?? Array.Empty<OutputSummary>(), false, "Grasshopper");

    private T Answer<T>(string method, T value, params object?[] args)
    {
        Calls.Add((method, args));
        if (Timeouts.Contains(method)) throw new TimeoutException($"{method} timed out");
        if (Rejections.TryGetValue(method, out var message)) throw new ToolArgumentException(message);
        return value ?? throw new InvalidOperationException($"{method} has no canned answer");
    }

    public IReadOnlyList<LiveDocumentInfo> Documents() => Answer("Documents", DocumentList);

    public LiveDocument ReadDocument(string? file, int previewItems) => Answer("ReadDocument", Document, file, previewItems)!;

    public LiveOutputs ReadOutputs(string? file, string objectRef, string? param, int maxItemsPerParam, int maxTextLength) =>
        Answer("ReadOutputs", Outputs, file, objectRef, param, maxItemsPerParam, maxTextLength)!;

    public SolveReport Solve(string? file, bool expireAll, TimeSpan timeout) => Answer("Solve", Report, file, expireAll, timeout)!;

    /// <summary>Echoes each edit back as applied, so a test can assert on what reached the host
    /// without a canvas. Set <see cref="ValueResults"/> to script refusals instead.</summary>
    public List<ValueEditResult>? ValueResults { get; set; }

    public IReadOnlyList<ValueEditResult> SetValues(string? file, IReadOnlyList<ValueEdit> edits, bool solve) =>
        Answer("SetValues",
            ValueResults ?? edits
                .Select(e => new ValueEditResult(e.Target, true, Guid(1), e.Target, e.Target, "slider", "0", e.Value))
                .ToList(),
            file, edits, solve);

    public IReadOnlyList<CatalogueCategory> Categories() => Answer("Categories", CategoryList);

    public IReadOnlyList<CatalogueEntry> Search(string? query, string? category, bool includeObsolete, int maxResults) =>
        Answer("Search", SearchResults, query, category, includeObsolete, maxResults);

    public CatalogueDescription Describe(Guid componentGuid) => Answer("Describe", Description, componentGuid)!;

    public CanvasImage CanvasImage(string? file, ImageRegion region, IReadOnlyList<string>? instanceGuids, string? query, int maxWidth, int maxHeight) =>
        Answer("CanvasImage", Image, file, region, instanceGuids, query, maxWidth, maxHeight)!;

    public RhinoContext Context() => Answer("Context", ContextValue)!;
}
