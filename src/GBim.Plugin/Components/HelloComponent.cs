using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace GBim.Components;

public sealed class HelloComponent : GH_Component
{
    public HelloComponent()
        : base(
            name: "G-BIM Status",
            nickname: "GBim?",
            description: "Reports that the G-BIM plugin is loaded.",
            category: "G-BIM",
            subCategory: "Diagnostics")
    {
    }

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        // Phase 0: no inputs.
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter(
            name: "Status",
            nickname: "S",
            description: "G-BIM plugin status message.",
            access: GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        DA.SetData(0, "G-BIM is loaded");
    }

    protected override Bitmap? Icon => null;

    public override Guid ComponentGuid => new("77343e7d-f73c-4629-b994-169bfa886aea");
}
