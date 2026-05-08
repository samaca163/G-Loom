using System;
using System.Drawing;
using System.Linq;
using GBim.Vcs;
using Grasshopper.Kernel;

namespace GBim.Components;

public sealed class LogComponent : GH_Component
{
    public LogComponent()
        : base(
            name: "Log",
            nickname: "GBim^",
            description: "Lists recent commits in the G-BIM repository at the given folder.",
            category: "G-BIM",
            subCategory: "Vcs")
    {
    }

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddTextParameter(
            "Folder", "F",
            "Working directory of the Git repository.",
            GH_ParamAccess.item);

        pManager.AddIntegerParameter(
            "Limit", "N",
            "Maximum number of commits to return.",
            GH_ParamAccess.item, 10);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter("SHA", "S", "Commit SHAs (newest first).", GH_ParamAccess.list);
        pManager.AddTextParameter("Author", "A", "Authors.", GH_ParamAccess.list);
        pManager.AddTextParameter("Date", "D", "Commit dates (ISO 8601 local time).", GH_ParamAccess.list);
        pManager.AddTextParameter("Message", "M", "Commit messages (subject line).", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string folder = string.Empty;
        int limit = 10;

        if (!DA.GetData(0, ref folder)) return;
        DA.GetData(1, ref limit);

        try
        {
            var commits = GBimRepository.Log(folder, limit);

            DA.SetDataList(0, commits.Select(c => c.Sha));
            DA.SetDataList(1, commits.Select(c => c.Author));
            DA.SetDataList(2, commits.Select(c => c.When.ToString("yyyy-MM-dd HH:mm:ss")));
            DA.SetDataList(3, commits.Select(c => c.Message));

            if (commits.Count == 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No commits found (folder is not a Git repo or has no commits yet).");
        }
        catch (Exception ex)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
        }
    }

    protected override Bitmap? Icon => null;

    public override Guid ComponentGuid => new("8fb955e8-bfc7-4eb1-b771-e98ef9252f00");
}
