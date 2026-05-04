using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace GBim;

public sealed class GBimAssemblyInfo : GH_AssemblyInfo
{
    public override string Name => "G-BIM";

    public override Bitmap? Icon => null;

    public override string Description =>
        "Git-style version control for Grasshopper 3D. " +
        "Commits, branches, and visual on-canvas diffs for .gh definitions.";

    public override Guid Id => new("fee7144c-f1fc-46d8-8710-100464ca0cb4");

    public override string AuthorName => "iSamacA";

    public override string AuthorContact => "samaca163@gmail.com";

    public override string Version => "0.0.1";
}
