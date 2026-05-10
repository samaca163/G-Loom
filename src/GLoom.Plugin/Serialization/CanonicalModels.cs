using System.Collections.Generic;

namespace GLoom.Serialization;

/// <summary>
/// Schema v3 of the canonical, diff-friendly representation of a Grasshopper
/// document. Designed for stability across saves: deterministic field order,
/// objects sorted by InstanceGuid, sources sorted lexicographically.
///
/// v3 adds the optional <see cref="CanonicalObject.Bounds"/> field so the
/// on-canvas overlay can render an accurate ghost outline for deleted
/// components (the live canvas no longer carries them, so the schema has
/// to). v2 added <see cref="CanonicalObject.Persistent"/> for user-tweakable
/// values. Older documents parse cleanly because both new fields have
/// `= null` defaults; older plugin readers ignore them.
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
    PersistentData? Persistent = null,
    Bounds? Bounds = null);

public sealed record Pivot(float X, float Y);

public sealed record Bounds(float X, float Y, float Width, float Height);

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
