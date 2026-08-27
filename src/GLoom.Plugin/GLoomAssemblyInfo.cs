using System;
using System.Drawing;
using GLoom.Ui;
using Grasshopper.Kernel;

namespace GLoom;

public sealed class GLoomAssemblyInfo : GH_AssemblyInfo
{
    public override string Name => "G-Loom";

    public override Bitmap? Icon => GLoomIcons.Family;

    public override string Description =>
        "Version control for parametric Grasshopper systems. " +
        "Branches, commits, tags, and on-canvas diffs over the recipe " +
        "instead of the geometry.";

    public override Guid Id => new("fee7144c-f1fc-46d8-8710-100464ca0cb4");

    public override string AuthorName => "iSamacA";

    public override string AuthorContact => "samaca163@gmail.com";

    // Keep in lockstep with <Version> in GLoom.Plugin.csproj.
    public override string Version => "0.3.0-mcp.2";
}
