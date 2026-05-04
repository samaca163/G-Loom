using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GBim.Serialization;

/// <summary>
/// Walks a live <see cref="GH_Document"/> and emits a <see cref="CanonicalDocument"/>.
/// Phase 1a: structural only - components, parameters, wires (via param sources),
/// groups. Persistent data (slider values, panel text, internalised geometry) is
/// out of scope and will land in Phase 1b.
/// </summary>
public static class DocumentSerializer
{
    private const int CurrentSchemaVersion = 1;

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
            Outputs: outputs);
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
            Outputs: Array.Empty<CanonicalParameter>());
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

    private static string Format(Guid g) => g.ToString("D");

    private static string NullSafe(string? value) => value ?? string.Empty;
}
