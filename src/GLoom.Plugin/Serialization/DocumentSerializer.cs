using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;

namespace GLoom.Serialization;

/// <summary>
/// Walks a live <see cref="GH_Document"/> and emits a <see cref="CanonicalDocument"/>.
/// Phase 1b extended the structural pass with persistent value capture for the
/// free-floating params people actually iterate on (sliders, panels, booleans,
/// value lists, color swatches, MD sliders, gradients), plus a SHA-256 digest
/// fallback for params holding internalized data we don't structurally model.
/// Schema v3 adds bounds capture so the on-canvas overlay can render accurate
/// ghosts for deleted components.
/// </summary>
public static class DocumentSerializer
{
    private const int CurrentSchemaVersion = 5;

    public static CanonicalDocument Serialize(GH_Document document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var objects = new List<CanonicalObject>();
        var groups = new List<CanonicalGroup>();

        foreach (var obj in document.Objects)
        {
            switch (obj)
            {
                case IGH_Component component:
                    objects.Add(SerializeComponent(component));
                    break;

                case IGH_Param param when param.Attributes?.Parent is null:
                    // Free-floating param (slider, panel, value list, etc.).
                    objects.Add(SerializeFreeFloatingParam(param));
                    break;

                case GH_Group group:
                    groups.Add(SerializeGroup(group));
                    break;
            }
        }

        // Stable ordering for diff: lexicographic by InstanceGuid string.
        objects.Sort((a, b) => string.CompareOrdinal(a.InstanceGuid, b.InstanceGuid));
        groups.Sort((a, b) => string.CompareOrdinal(a.InstanceGuid, b.InstanceGuid));

        return new CanonicalDocument(
            SchemaVersion: CurrentSchemaVersion,
            Document: ExtractMeta(document),
            Objects: objects,
            Groups: groups);
    }

    private static DocumentMeta ExtractMeta(GH_Document document)
    {
        // GH_DocumentProperties only carries Description / CopyRight / Date / etc.
        // The document name lives on GH_Document.DisplayName. Author is tracked by
        // Git on commit, so we do not duplicate it here.
        return new DocumentMeta(
            Name: NullSafe(document.DisplayName),
            Description: NullSafe(document.Properties?.Description));
    }

    private static CanonicalObject SerializeComponent(IGH_Component component)
    {
        var inputs = component.Params.Input
            .Select(SerializeParam)
            .ToList();
        var outputs = component.Params.Output
            .Select(SerializeParam)
            .ToList();

        return new CanonicalObject(
            InstanceGuid: Format(component.InstanceGuid),
            ComponentGuid: Format(component.ComponentGuid),
            Kind: "component",
            Name: NullSafe(component.Name),
            Nickname: NullSafe(component.NickName),
            Pivot: ExtractPivot(component),
            Inputs: inputs,
            Outputs: outputs,
            Bounds: ExtractBounds(component));
    }

    private static CanonicalObject SerializeFreeFloatingParam(IGH_Param param)
    {
        // A free-floating param IS its own output. Model it as a 0-output object
        // whose single synthetic "input" carries its upstream sources, so wire
        // diffing still works against its Sources list.
        var syntheticInput = SerializeParam(param);

        return new CanonicalObject(
            InstanceGuid: Format(param.InstanceGuid),
            ComponentGuid: Format(param.ComponentGuid),
            Kind: "param",
            Name: NullSafe(param.Name),
            Nickname: NullSafe(param.NickName),
            Pivot: ExtractPivot(param),
            Inputs: new[] { syntheticInput },
            Outputs: Array.Empty<CanonicalParameter>(),
            Persistent: CapturePersistent(param),
            Bounds: ExtractBounds(param));
    }

    /// <summary>
    /// Captures the user-tweakable state of a free-floating param. Returns
    /// null when the param has no persistent state worth recording (no
    /// match against the typed handlers AND no internalized data).
    ///
    /// Special-cased kinds get structured representations so the diff can
    /// say "slider went from 5 to 10" rather than "opaque blob changed".
    /// Anything we don't have a typed handler for falls back to a
    /// SHA-256 digest of the param's GH-serialized form when it carries
    /// internalized data; that's enough to detect change without trying
    /// to render it.
    /// </summary>
    private static PersistentData? CapturePersistent(IGH_Param param)
    {
        switch (param)
        {
            case GH_NumberSlider slider:
                return new PersistentData(
                    Kind: "slider",
                    Slider: new SliderValue(
                        Value: slider.CurrentValue,
                        Min: slider.Slider.Minimum,
                        Max: slider.Slider.Maximum,
                        Decimals: slider.Slider.DecimalPlaces,
                        Type: slider.Slider.Type.ToString()));

            case GH_Panel panel:
                return new PersistentData(
                    Kind: "panel",
                    PanelText: NullSafe(panel.UserText));

            case GH_BooleanToggle toggle:
                return new PersistentData(
                    Kind: "boolean",
                    BooleanState: toggle.Value);

            case GH_ValueList valueList:
                {
                    var selected = (valueList.SelectedItems ?? new List<GH_ValueListItem>())
                        .Select(i => i.Name ?? string.Empty)
                        .OrderBy(s => s, StringComparer.Ordinal)
                        .ToList();
                    var allItems = (valueList.ListItems ?? new List<GH_ValueListItem>())
                        .Select(i => new ValueListItem(i.Name ?? string.Empty, i.Expression ?? string.Empty))
                        .OrderBy(i => i.Name, StringComparer.Ordinal)
                        .ToList();
                    return new PersistentData(
                        Kind: "valuelist",
                        ValueListSelected: selected,
                        ValueListItems: allItems,
                        ValueListMode: valueList.ListMode.ToString());
                }

            case GH_ColourSwatch swatch:
                return new PersistentData(
                    Kind: "color",
                    ColorArgb: swatch.SwatchColour.ToArgb().ToString("X8"));
        }

        // Generic fallback for any persistent-typed param holding
        // internalized data (MD slider, gradient, baked geometry on a
        // Curve param, etc.). PersistentDataCount lives on the generic
        // GH_PersistentParam<T>; rather than reflect across all the typed
        // variants, we ask via dynamic and treat zero/exception as
        // "nothing to capture".
        var count = TryGetPersistentDataCount(param);
        if (count > 0)
        {
            return new PersistentData(
                Kind: "data",
                Digest: ComputeParamDigest(param));
        }

        return null;
    }

    private static int TryGetPersistentDataCount(IGH_Param param)
    {
        try
        {
            dynamic dyn = param;
            return (int)dyn.PersistentDataCount;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// SHA-256 of the param's full GH XML serialization. Includes more than
    /// just the persistent data (the param's own attributes ride along too)
    /// but for diff purposes that's acceptable: structural fields are
    /// captured separately, so a digest mismatch on a structurally-stable
    /// param means the persistent payload moved.
    /// </summary>
    private static string ComputeParamDigest(IGH_Param param)
    {
        try
        {
            var chunk = new GH_LooseChunk("Persistent");
            param.Write(chunk);
            var bytes = Encoding.UTF8.GetBytes(chunk.Serialize_Xml());
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "unknown";
        }
    }

    private static CanonicalParameter SerializeParam(IGH_Param param)
    {
        var sources = param.Sources?
            .Select(s => Format(s.InstanceGuid))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList() ?? new List<string>();

        return new CanonicalParameter(
            InstanceGuid: Format(param.InstanceGuid),
            Name: NullSafe(param.Name),
            Nickname: NullSafe(param.NickName),
            Access: param.Access.ToString().ToLowerInvariant(),
            Sources: sources);
    }

    private static CanonicalGroup SerializeGroup(GH_Group group)
    {
        var members = (group.ObjectIDs ?? new List<Guid>())
            .Select(Format)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return new CanonicalGroup(
            InstanceGuid: Format(group.InstanceGuid),
            Name: NullSafe(group.NickName),
            Members: members);
    }

    private static Pivot ExtractPivot(IGH_DocumentObject obj)
    {
        var p = obj.Attributes?.Pivot ?? PointF.Empty;
        return new Pivot(p.X, p.Y);
    }

    private static Bounds? ExtractBounds(IGH_DocumentObject obj)
    {
        var b = obj.Attributes?.Bounds ?? RectangleF.Empty;
        if (b.IsEmpty || (b.Width == 0 && b.Height == 0)) return null;
        return new Bounds(b.X, b.Y, b.Width, b.Height);
    }

    private static string Format(Guid g) => g.ToString("D");

    private static string NullSafe(string? value) => value ?? string.Empty;
}
