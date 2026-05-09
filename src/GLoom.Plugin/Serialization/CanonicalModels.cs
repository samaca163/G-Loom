using System.Collections.Generic;

namespace GLoom.Serialization;

/// <summary>
/// Schema v1 of the canonical, diff-friendly representation of a Grasshopper
/// document. Designed for stability across saves: deterministic field order,
/// objects sorted by InstanceGuid, sources sorted lexicographically.
/// </summary>
public sealed record CanonicalDocument(
    int SchemaVersion,
    DocumentMeta Document,
    IReadOnlyList<CanonicalObject> Objects,
    IReadOnlyList<CanonicalGroup> Groups);

public sealed record DocumentMeta(
    string Name,
    string Description);

public sealed record CanonicalObject(
    string InstanceGuid,
    string ComponentGuid,
    string Kind,
    string Name,
    string Nickname,
    Pivot Pivot,
    IReadOnlyList<CanonicalParameter> Inputs,
    IReadOnlyList<CanonicalParameter> Outputs);

public sealed record Pivot(float X, float Y);

public sealed record CanonicalParameter(
    string InstanceGuid,
    string Name,
    string Nickname,
    string Access,
    IReadOnlyList<string> Sources);

public sealed record CanonicalGroup(
    string InstanceGuid,
    string Name,
    IReadOnlyList<string> Members);
