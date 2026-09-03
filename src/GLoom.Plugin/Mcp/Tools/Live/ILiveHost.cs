using System;
using System.Collections.Generic;
using GLoom.Mcp.Protocol;

namespace GLoom.Mcp.Tools.Live;

/// <summary>
/// The live canvas as the tools see it. The plugin's implementation marshals every call
/// through <c>UiThread.Run</c> and reads Grasshopper and Rhino there; tests hand the tools a
/// fake. A <paramref name="file"/> argument names an open document (absolute, or relative
/// to the active document's project); null means the active document. Implementations
/// throw <see cref="ToolArgumentException"/> for a document that is not open, an object
/// that cannot be found or is ambiguous, and an unknown component; a
/// <see cref="TimeoutException"/> means the UI thread was busy (a modal dialog, a solve).
/// </summary>
public interface ILiveHost
{
    IReadOnlyList<LiveDocumentInfo> Documents();

    /// <summary>The whole document: recipe structure plus the runtime sidecar of every object.
    /// <paramref name="previewItems"/> is how many items each output previews.</summary>
    LiveDocument ReadDocument(string? file, int previewItems);

    /// <summary>The data on one object's outputs (or a free-floating parameter's own data).
    /// <paramref name="objectRef"/> is an InstanceGuid or an exact name / nickname; <paramref name="param"/>
    /// narrows to one output by name, nickname or index.</summary>
    LiveOutputs ReadOutputs(string? file, string objectRef, string? param, int maxItemsPerParam, int maxTextLength);

    SolveReport Solve(string? file, bool expireAll, TimeSpan timeout);

    /// <summary>Sets persistent values on sliders, panels, toggles, value lists and colour
    /// swatches, as one undoable step. Every edit is attempted; one that cannot be applied is
    /// reported against its target rather than failing the batch.</summary>
    IReadOnlyList<ValueEditResult> SetValues(string? file, IReadOnlyList<ValueEdit> edits, bool solve);

    IReadOnlyList<CatalogueCategory> Categories();

    /// <summary>Grasshopper's own fuzzy search when <paramref name="query"/> is given, else every
    /// proxy in <paramref name="category"/>; results are unsorted and unpaged - the tool decides.</summary>
    IReadOnlyList<CatalogueEntry> Search(string? query, string? category, bool includeObsolete, int maxResults);

    CatalogueDescription Describe(Guid componentGuid);

    /// <summary>Pixels of the active canvas. <see cref="ImageRegion.Visible"/> is what the user sees;
    /// <see cref="ImageRegion.All"/> frames every object; <see cref="ImageRegion.Objects"/> frames the
    /// objects named by <paramref name="instanceGuids"/> or matched by <paramref name="query"/>.</summary>
    CanvasImage CanvasImage(string? file, ImageRegion region, IReadOnlyList<string>? instanceGuids, string? query, int maxWidth, int maxHeight);

    RhinoContext Context();
}
