using System.Collections.Generic;

namespace GLoom.Serialization;

/// <summary>
/// Schema v2 of the canonical, diff-friendly representation of a Grasshopper
/// document. Designed for stability across saves: deterministic field order,
/// objects sorted by InstanceGuid, sources sorted lexicographically.
///
/// v2 adds the optional <see cref="CanonicalObject.Persistent"/> field to
/// capture user-tweakable values (sliders, panels, booleans, value lists,
/// etc.) so a diff can show "this slider went from 5 to 10", not just
/// structural changes. v1 documents parse cleanly because Persistent has a
/// `= null` default; v1 readers ignore the new field.
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
    IReadOnlyList<CanonicalParameter> Outputs,
    PersistentData? Persistent = null);

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

/// <summary>
/// Captured user-tweakable state on a free-floating param. <see cref="Kind"/>
/// is a discriminator; for each kind, only the relevant sub-field is
/// populated (the rest serialize as null and JsonIgnore drops them). The
/// fat-union shape reads simpler in raw JSON than polymorphic discriminator
/// attributes, and diff code can switch on Kind without reflection.
///
/// Kinds emitted today:
///   "slider"    → <see cref="Slider"/>
///   "panel"     → <see cref="PanelText"/>
///   "boolean"   → <see cref="BooleanState"/>
///   "valuelist" → <see cref="ValueListSelected"/>
///   "color"     → <see cref="ColorArgb"/>
///   "data"      → <see cref="Digest"/> (SHA-256 opaque fallback for any
///                 persistent-typed param we don't yet model structurally
///                 — MD sliders, gradients, internalized geometry, etc.)
/// </summary>
public sealed record PersistentData(
    string Kind,
    SliderValue? Slider = null,
    string? PanelText = null,
    bool? BooleanState = null,
    IReadOnlyList<string>? ValueListSelected = null,
    string? ColorArgb = null,
    string? Digest = null);

public sealed record SliderValue(
    decimal Value,
    decimal Min,
    decimal Max,
    int Decimals,
    string Type);
