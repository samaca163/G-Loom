using System.Runtime.InteropServices;
using GBim.Ui;
using Rhino;
using Rhino.Commands;

namespace GBim.Commands;

/// <summary>
/// Rhino command "GBimPanel" - shows the G-BIM panel. Useful when the panel
/// has been closed and the user wants it back without restarting Grasshopper.
/// Discovered by Rhino's plugin loader because this class is public, derives
/// from Command, has a parameterless ctor, and a unique [Guid] attribute.
/// </summary>
[Guid("ff62e097-0e40-4d58-9d8a-e23f9262a8d9")]
public sealed class GBimPanelCommand : Command
{
    public override string EnglishName => "GBimPanel";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        GBimPanelHost.Open();
        return Result.Success;
    }
}
