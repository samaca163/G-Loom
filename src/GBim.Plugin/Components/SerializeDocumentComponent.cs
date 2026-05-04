using System;
using System.Drawing;
using GBim.Serialization;
using Grasshopper.Kernel;

namespace GBim.Components;

public sealed class SerializeDocumentComponent : GH_Component
{
    public SerializeDocumentComponent()
        : base(
            name: "Serialize Document",
            nickname: "GBim<>",
            description: "Serializes the current Grasshopper document to canonical JSON " +
                         "for G-BIM version control. Phase 1a: structural fields only.",
            category: "G-BIM",
            subCategory: "Diagnostics")
    {
    }

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        // No inputs - operates on the document this component is placed in.
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter(
            name: "JSON",
            nickname: "J",
            description: "Canonical JSON serialization of the current document.",
            access: GH_ParamAccess.item);

        pManager.AddIntegerParameter(
            name: "Object Count",
            nickname: "N",
            description: "Number of objects (components + free-floating params) serialized.",
            access: GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var document = OnPingDocument();
        if (document is null)
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Warning,
                "Component is not attached to a document.");
            return;
        }

        var canonical = DocumentSerializer.Serialize(document);
        var json = CanonicalJson.Write(canonical);

        DA.SetData(0, json);
        DA.SetData(1, canonical.Objects.Count);
    }

    protected override Bitmap? Icon => null;

    public override Guid ComponentGuid => new("3c9f1d5c-2d8e-4d1f-9b5a-9a0e7c1c8e10");
}
