using GBim.Canvas;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Rhino;

namespace GBim;

public sealed class GBimPriorityLoad : GH_AssemblyPriority
{
    public override GH_LoadingInstruction PriorityLoad()
    {
        // The active canvas may not exist yet at plugin-load time;
        // subscribe to CanvasCreated and also handle the already-created case.
        Instances.CanvasCreated += OnCanvasCreated;

        if (Instances.ActiveCanvas != null)
            OnCanvasCreated(Instances.ActiveCanvas);

        RhinoApp.WriteLine("[G-BIM] Plugin loaded.");
        return GH_LoadingInstruction.Proceed;
    }

    private static void OnCanvasCreated(GH_Canvas canvas)
    {
        DiffOverlayPainter.Attach(canvas);
    }
}
