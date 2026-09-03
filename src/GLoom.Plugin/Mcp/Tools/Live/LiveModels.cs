using System;
using System.Collections.Generic;
using GLoom.Serialization;

namespace GLoom.Mcp.Tools.Live;

/// <summary>
/// What the live canvas knows and the recipe does not: runtime messages, data flowing
/// through parameters, solve state, the installed catalogue, pixels. These records are the
/// contract between the host adapter (UI thread, Grasshopper types) and the tools (request
/// thread, plain data), so the tools and their tests never see a Grasshopper type.
/// </summary>
public sealed record LiveDocumentInfo(
    string? FilePath,
    string DisplayName,
    bool IsActive,
    bool IsModified,
    bool Enabled,
    int ObjectCount,
    string SolutionState,
    double SolutionMs,
    int ErrorCount,
    int WarningCount);

public sealed record RuntimeMessage(string Level, string Text);

/// <summary>One output (or a free-floating parameter's own data) in summary: counts and a
/// glimpse of the first items, never the whole tree.</summary>
public sealed record OutputSummary(
    string InstanceGuid,
    string Name,
    string Nickname,
    string TypeName,
    int DataCount,
    int PathCount,
    IReadOnlyList<string> Preview);

public sealed record InputSummary(
    string InstanceGuid,
    string Name,
    string Nickname,
    string TypeName,
    int DataCount,
    int SourceCount,
    bool Optional);

/// <summary>The runtime sidecar of one object, keyed like the recipe by InstanceGuid.</summary>
public sealed record RuntimeObject(
    string InstanceGuid,
    string Level,
    IReadOnlyList<RuntimeMessage> Messages,
    bool Locked,
    bool? Hidden,
    string Phase,
    double ProcessorMs,
    IReadOnlyList<InputSummary> Inputs,
    IReadOnlyList<OutputSummary> Outputs,
    bool IsCluster,
    string? Library);

public sealed record LiveDocument(
    LiveDocumentInfo Info,
    CanonicalDocument Structure,
    IReadOnlyList<RuntimeObject> Runtime);

public sealed record DataItem(string TypeName, string Text, IReadOnlyList<double>? BoundsMin = null, IReadOnlyList<double>? BoundsMax = null);

public sealed record DataBranch(string Path, int Count, IReadOnlyList<DataItem> Items);

public sealed record OutputData(
    string InstanceGuid,
    string Name,
    string Nickname,
    string TypeName,
    int DataCount,
    int PathCount,
    IReadOnlyList<DataBranch> Branches,
    bool Truncated);

public sealed record ObjectRef(string InstanceGuid, string Name, string Nickname, string Kind);

public sealed record LiveOutputs(ObjectRef Object, IReadOnlyList<OutputData> Params);

public sealed record ObjectProblem(string InstanceGuid, string Name, string Nickname, IReadOnlyList<RuntimeMessage> Messages);

public sealed record SolveReport(
    bool Ran,
    bool SolverLocked,
    bool ExpiredAll,
    double DurationMs,
    string SolutionState,
    int ObjectCount,
    IReadOnlyList<ObjectProblem> Errors,
    IReadOnlyList<ObjectProblem> Warnings);

public sealed record LibraryInfo(string Name, string Version, string? Author, bool IsCore);

public sealed record CatalogueEntry(
    string ComponentGuid,
    string Name,
    string Nickname,
    string Description,
    string Category,
    string SubCategory,
    string Exposure,
    bool Obsolete,
    string Kind,
    LibraryInfo? Library,
    double? Score = null);

public sealed record CatalogueCategory(string Category, IReadOnlyList<string> SubCategories, int Count);

public sealed record ParamDescription(
    string Name,
    string Nickname,
    string Description,
    string TypeName,
    string Access,
    bool Optional);

/// <summary><paramref name="InstantiationError"/> is set when the component could not be
/// instantiated to read its parameters; Inputs and Outputs are then empty, not known.</summary>
public sealed record CatalogueDescription(
    CatalogueEntry Entry,
    IReadOnlyList<ParamDescription> Inputs,
    IReadOnlyList<ParamDescription> Outputs,
    string? ParamTypeName,
    IReadOnlyList<string> Keywords,
    string? InstantiationError = null);

public enum ImageRegion { Visible, All, Objects }

public sealed record CanvasImage(
    byte[] Png,
    int PixelWidth,
    int PixelHeight,
    double Zoom,
    double RegionX,
    double RegionY,
    double RegionWidth,
    double RegionHeight,
    int ObjectsInFrame);

public sealed record ActiveModel(
    string? Path,
    string Name,
    string Units,
    double AbsoluteTolerance,
    int ObjectCount,
    int LayerCount,
    bool Modified);

public sealed record RhinoContext(
    string RhinoVersion,
    string HostProcess,
    bool InsideRevit,
    string? RhinoInsideRevitVersion,
    string GrasshopperVersion,
    string GloomVersion,
    ActiveModel? ActiveModel,
    int OpenDefinitions,
    string? ActiveDefinition);
