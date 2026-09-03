using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Live;
using GLoom.Serialization;
using GLoom.Vcs;
using Rhino;

namespace GLoom.Mcp.Host.Live;

/// <summary>
/// The plugin's <see cref="ILiveHost"/>: every method is one <see cref="UiThread.Run{T}"/>
/// that does all Grasshopper and Rhino work on the UI thread and hands back plain records,
/// so no Grasshopper object ever crosses to the request thread.
/// </summary>
public sealed class LiveHost : ILiveHost
{
    public IReadOnlyList<LiveDocumentInfo> Documents() => UiThread.Run(() =>
    {
        var active = Instances.ActiveCanvas?.Document;
        return OpenDocuments().Select(d => Info(d, active)).ToList();
    });

    public LiveDocument ReadDocument(string? file, int previewItems) => UiThread.Run(() =>
    {
        var doc = Resolve(file);
        var structure = DocumentSerializer.Serialize(doc);

        var runtime = new List<RuntimeObject>();
        foreach (var obj in doc.Objects)
        {
            switch (obj)
            {
                case IGH_Component component:
                    runtime.Add(RuntimeOf(component, component.Params?.Input, component.Params?.Output, previewItems));
                    break;
                case IGH_Param param when param.Attributes?.Parent is null:
                    runtime.Add(RuntimeOf(param, null, new[] { param }, previewItems));
                    break;
            }
        }

        return new LiveDocument(Info(doc, Instances.ActiveCanvas?.Document), structure, runtime);
    });

    public LiveOutputs ReadOutputs(string? file, string objectRef, string? param, int maxItemsPerParam, int maxTextLength) =>
        UiThread.Run(() =>
        {
            var doc = Resolve(file);
            var obj = FindObject(doc, objectRef);

            IReadOnlyList<IGH_Param> outputs = obj switch
            {
                IGH_Component component => component.Params?.Output?.ToList() ?? new List<IGH_Param>(),
                IGH_Param p => new[] { p },
                _ => throw new ToolArgumentException(
                    $"'{objectRef}' is a {obj.GetType().Name} without data outputs (a group, scribble or similar)."),
            };
            if (!string.IsNullOrWhiteSpace(param)) outputs = Pick(outputs, param.Trim(), objectRef);

            var kind = obj switch
            {
                IGH_Component => "component",
                IGH_Param { Attributes.Parent: not null } p => p.Kind.ToString().ToLowerInvariant(),
                _ => "param",
            };
            var reference = new ObjectRef(obj.InstanceGuid.ToString(), obj.Name ?? string.Empty, obj.NickName ?? string.Empty, kind);
            return new LiveOutputs(reference, outputs.Select(o => OutputDataOf(o, maxItemsPerParam, maxTextLength)).ToList());
        });

    public SolveReport Solve(string? file, bool expireAll, TimeSpan timeout) => UiThread.Run(() =>
    {
        var doc = Resolve(file);
        if (!GH_Document.EnableSolutions) return Report(doc, ran: false, solverLocked: true, expireAll, 0);
        // NewSolution returns without solving when the document is disabled, which the canvas
        // does to every tab it switches away from; the stale error list would then read as a result.
        if (!doc.Enabled)
            throw new ToolArgumentException(
                $"{doc.DisplayName} is disabled, so Grasshopper would not solve it: it is not the active tab, " +
                "or the Grasshopper window is hidden. Activate it in Grasshopper and call gloom_solve again.");
        if (doc.SolutionState == GH_ProcessStep.Process)
            throw new ToolArgumentException("A solution is already running; wait for it, then retry.");

        var clock = Stopwatch.StartNew();
        doc.NewSolution(expireAll, GH_SolutionMode.CommandLine);
        clock.Stop();
        return Report(doc, ran: true, solverLocked: false, expireAll, clock.Elapsed.TotalMilliseconds);
    }, timeout);

    public IReadOnlyList<CatalogueCategory> Categories() => UiThread.Run(() => LiveCatalogue.Categories());

    public IReadOnlyList<CatalogueEntry> Search(string? query, string? category, bool includeObsolete, int maxResults) =>
        UiThread.Run(() => LiveCatalogue.Search(query, category, includeObsolete, maxResults));

    public CatalogueDescription Describe(Guid componentGuid) => UiThread.Run(() => LiveCatalogue.Describe(componentGuid));

    public CanvasImage CanvasImage(
        string? file, ImageRegion region, IReadOnlyList<string>? instanceGuids, string? query, int maxWidth, int maxHeight) =>
        UiThread.Run(() => LiveCanvasImage.Capture(Resolve(file), region, instanceGuids, query, maxWidth, maxHeight));

    public RhinoContext Context() => UiThread.Run(() =>
    {
        var toolchain = ToolchainSnapshot.Capture();
        using var process = Process.GetCurrentProcess();

        var model = RhinoDoc.ActiveDoc;
        var active = Instances.ActiveCanvas?.Document;
        return new RhinoContext(
            RhinoVersion: RhinoApp.Version?.ToString() ?? "unknown",
            HostProcess: process.ProcessName,
            InsideRevit: toolchain.RhinoInsideRevit is not null,
            RhinoInsideRevitVersion: toolchain.RhinoInsideRevit,
            GrasshopperVersion: toolchain.Grasshopper,
            GloomVersion: toolchain.Gloom,
            ActiveModel: model is null ? null : new ActiveModel(
                Path: string.IsNullOrEmpty(model.Path) ? null : model.Path,
                Name: model.Name ?? string.Empty,
                Units: model.ModelUnitSystem.ToString(),
                AbsoluteTolerance: model.ModelAbsoluteTolerance,
                ObjectCount: model.Objects.Count,
                LayerCount: model.Layers.Count,
                Modified: model.Modified),
            OpenDefinitions: Instances.DocumentServer.DocumentCount,
            ActiveDefinition: active is { IsFilePathDefined: true } ? active.FilePath : null);
    });

    // ----- document resolution -----

    private static IEnumerable<GH_Document> OpenDocuments() => Instances.DocumentServer.OfType<GH_Document>();

    private static GH_Document Resolve(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return Instances.ActiveCanvas?.Document
                   ?? throw new ToolArgumentException("No active Grasshopper document.");

        var path = file.Trim();
        if (!Path.IsPathRooted(path))
        {
            var repo = DocumentTracker.Instance.State.RepoPath
                       ?? throw new ToolArgumentException(
                           $"'{file}' is relative but there is no active project to resolve it against; " +
                           "activate a definition inside a G-Loom project or pass an absolute path.");
            path = Path.Combine(repo, path);
        }
        var full = Path.GetFullPath(path);

        foreach (var doc in OpenDocuments())
            if (doc.IsFilePathDefined && string.Equals(Path.GetFullPath(doc.FilePath), full, StringComparison.OrdinalIgnoreCase))
                return doc;

        var open = OpenDocuments().Select(d => d.IsFilePathDefined ? d.FilePath : $"{d.DisplayName} (unsaved)");
        throw new ToolArgumentException(
            $"'{file}' is not open in Grasshopper (open: {string.Join("; ", open)}); " +
            "open it in Grasshopper first; gloom_documents lists what is open.");
    }

    private static IGH_DocumentObject FindObject(GH_Document doc, string objectRef)
    {
        var wanted = (objectRef ?? string.Empty).Trim();
        if (wanted.Length == 0) throw new ToolArgumentException("An object reference is required: an instance guid, name or nickname.");

        var objects = doc.Objects;
        List<IGH_DocumentObject> found;
        if (Guid.TryParse(wanted, out var id))
        {
            found = objects.Where(o => o.InstanceGuid == id).ToList();
            // gloom_read_document lists a component's input and output params by their own
            // guids; reading one of those shows the data reaching or leaving that port.
            if (found.Count == 0 && doc.FindObject(id, topLevelOnly: false) is IGH_Param { Attributes.Parent: not null } port)
                found.Add(port);
        }
        else
        {
            found = objects.Where(o => string.Equals(o.NickName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0)
                found = objects.Where(o => string.Equals(o.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            // The ambiguity message below quotes short guids, so accept them back.
            if (found.Count == 0 && wanted.Length >= 8)
                found = objects.Where(o => o.InstanceGuid.ToString().StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (found.Count == 0)
            throw new ToolArgumentException(
                $"No object '{objectRef}' in {doc.DisplayName}; pass an instance guid (of an object, or of one of " +
                "its inputs or outputs) or an exact name or nickname (gloom_read_document lists them).");
        if (found.Count > 1)
            throw new ToolArgumentException(
                $"'{objectRef}' matches {found.Count} objects; pass an instance guid: " +
                string.Join("; ", found.Select(Describe)));
        return found[0];
    }

    private static string Describe(IGH_DocumentObject o)
    {
        var pivot = o.Attributes?.Pivot ?? default;
        return FormattableString.Invariant(
            $"{o.NickName} ({o.Name}) {o.InstanceGuid.ToString().Substring(0, 8)} at ({pivot.X:0}, {pivot.Y:0})");
    }

    private static IReadOnlyList<IGH_Param> Pick(IReadOnlyList<IGH_Param> outputs, string param, string objectRef)
    {
        var picked = outputs
            .Where(o => string.Equals(o.Name, param, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(o.NickName, param, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (picked.Count == 0
            && int.TryParse(param, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < outputs.Count)
            picked.Add(outputs[index]);
        if (picked.Count == 0)
            throw new ToolArgumentException(
                $"'{param}' is not an output of '{objectRef}'; outputs: " +
                string.Join(", ", outputs.Select((o, i) => $"{i}: {o.NickName} ({o.Name})")));
        return picked;
    }

    // ----- shaping -----

    private static LiveDocumentInfo Info(GH_Document doc, GH_Document? active)
    {
        int errors = 0, warnings = 0;
        foreach (var obj in doc.ActiveObjects())
        {
            switch (obj.RuntimeMessageLevel)
            {
                case GH_RuntimeMessageLevel.Error: errors++; break;
                case GH_RuntimeMessageLevel.Warning: warnings++; break;
            }
        }

        return new LiveDocumentInfo(
            FilePath: doc.IsFilePathDefined ? doc.FilePath : null,
            DisplayName: doc.DisplayName ?? string.Empty,
            IsActive: ReferenceEquals(doc, active),
            IsModified: doc.IsModified,
            Enabled: doc.Enabled,
            ObjectCount: doc.ObjectCount,
            SolutionState: doc.SolutionState.ToString(),
            SolutionMs: doc.SolutionSpan.TotalMilliseconds,
            ErrorCount: errors,
            WarningCount: warnings);
    }

    private static RuntimeObject RuntimeOf(
        IGH_ActiveObject obj, IEnumerable<IGH_Param>? inputs, IEnumerable<IGH_Param>? outputs, int previewItems)
    {
        var messages = new List<RuntimeMessage>();
        Append(messages, obj, GH_RuntimeMessageLevel.Error, "error");
        Append(messages, obj, GH_RuntimeMessageLevel.Warning, "warning");
        Append(messages, obj, GH_RuntimeMessageLevel.Remark, "remark");

        return new RuntimeObject(
            InstanceGuid: obj.InstanceGuid.ToString(),
            Level: obj.RuntimeMessageLevel.ToString().ToLowerInvariant(),
            Messages: messages,
            Locked: obj.Locked,
            Hidden: (obj as IGH_PreviewObject)?.Hidden,
            Phase: obj.Phase.ToString(),
            ProcessorMs: obj.ProcessorTime.TotalMilliseconds,
            Inputs: inputs?.Select(InputOf).ToList() ?? new List<InputSummary>(),
            Outputs: outputs?.Select(o => OutputOf(o, previewItems)).ToList() ?? new List<OutputSummary>(),
            IsCluster: obj is GH_Cluster,
            Library: LibraryOf(obj));
    }

    private static void Append(List<RuntimeMessage> into, IGH_ActiveObject obj, GH_RuntimeMessageLevel level, string tag)
    {
        foreach (var text in obj.RuntimeMessages(level) ?? Array.Empty<string>())
            if (!string.IsNullOrEmpty(text)) into.Add(new RuntimeMessage(tag, text));
    }

    private static IReadOnlyList<RuntimeMessage> MessagesAt(IGH_ActiveObject obj, GH_RuntimeMessageLevel level, string tag)
    {
        var list = new List<RuntimeMessage>();
        Append(list, obj, level, tag);
        return list;
    }

    private static string? LibraryOf(IGH_DocumentObject obj)
    {
        try { return Instances.ComponentServer.FindAssemblyByObject(obj.ComponentGuid)?.Name; }
        catch { return null; }
    }

    private static InputSummary InputOf(IGH_Param p) => new(
        p.InstanceGuid.ToString(), p.Name ?? string.Empty, p.NickName ?? string.Empty, p.TypeName ?? string.Empty,
        p.VolatileDataCount, p.SourceCount, p.Optional);

    private static OutputSummary OutputOf(IGH_Param p, int previewItems)
    {
        var data = p.VolatileData;
        var preview = new List<string>();
        if (data is not null && previewItems > 0)
        {
            foreach (var goo in data.AllData(true))
            {
                preview.Add(GooText.Preview(goo));
                if (preview.Count >= previewItems) break;
            }
        }
        return new OutputSummary(
            p.InstanceGuid.ToString(), p.Name ?? string.Empty, p.NickName ?? string.Empty, p.TypeName ?? string.Empty,
            data?.DataCount ?? 0, data?.PathCount ?? 0, preview);
    }

    private static OutputData OutputDataOf(IGH_Param p, int maxItems, int maxTextLength)
    {
        var data = p.VolatileData;
        var branches = new List<DataBranch>();
        var budget = Math.Max(0, maxItems);
        var truncated = false;

        if (data is not null)
        {
            foreach (var path in data.Paths)
            {
                var branch = data.get_Branch(path);
                var items = new List<DataItem>();
                if (branch is not null)
                {
                    foreach (var entry in branch)
                    {
                        if (budget == 0) { truncated = true; break; }
                        items.Add(GooText.Item(entry as IGH_Goo, maxTextLength));
                        budget--;
                    }
                }
                branches.Add(new DataBranch(path?.ToString() ?? "{}", branch?.Count ?? 0, items));
            }
        }

        return new OutputData(
            p.InstanceGuid.ToString(), p.Name ?? string.Empty, p.NickName ?? string.Empty, p.TypeName ?? string.Empty,
            data?.DataCount ?? 0, data?.PathCount ?? 0, branches, truncated);
    }

    private static SolveReport Report(GH_Document doc, bool ran, bool solverLocked, bool expireAll, double durationMs)
    {
        var errors = new List<ObjectProblem>();
        var warnings = new List<ObjectProblem>();
        foreach (var obj in doc.ActiveObjects())
        {
            switch (obj.RuntimeMessageLevel)
            {
                case GH_RuntimeMessageLevel.Error:
                    errors.Add(Problem(obj, MessagesAt(obj, GH_RuntimeMessageLevel.Error, "error")));
                    break;
                case GH_RuntimeMessageLevel.Warning:
                    warnings.Add(Problem(obj, MessagesAt(obj, GH_RuntimeMessageLevel.Warning, "warning")));
                    break;
            }
        }

        return new SolveReport(
            Ran: ran,
            SolverLocked: solverLocked,
            ExpiredAll: ran && expireAll,
            DurationMs: durationMs,
            SolutionState: doc.SolutionState.ToString(),
            ObjectCount: doc.ObjectCount,
            Errors: errors,
            Warnings: warnings);
    }

    private static ObjectProblem Problem(IGH_ActiveObject obj, IReadOnlyList<RuntimeMessage> messages) =>
        new(obj.InstanceGuid.ToString(), obj.Name ?? string.Empty, obj.NickName ?? string.Empty, messages);
}
