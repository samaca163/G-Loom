using GBim.Canvas;
using GBim.Ui;
using GBim.Vcs;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Rhino;

namespace GBim;

public sealed class GBimPriorityLoad : GH_AssemblyPriority
{
    public override GH_LoadingInstruction PriorityLoad()
    {
        DocumentTracker.Instance.Initialize();
        GBimPanelHost.Register();

        Instances.CanvasCreated += OnCanvasCreated;
        if (Instances.ActiveCanvas != null)
            OnCanvasCreated(Instances.ActiveCanvas);

        RhinoApp.WriteLine("[G-BIM] Plugin loaded.");
        return GH_LoadingInstruction.Proceed;
    }

    private static bool _panelAutoOpened;

    private static void OnCanvasCreated(GH_Canvas canvas)
    {
        DiffOverlayPainter.Attach(canvas);

        // Auto-open the panel once on first canvas creation. After that the user
        // can close/dock/move it like any other Rhino panel, and re-open via the
        // `GBimPanel` Rhino command. Schedule on the Eto UI thread to be safe.
        if (_panelAutoOpened) return;
        _panelAutoOpened = true;
        try { Eto.Forms.Application.Instance.AsyncInvoke(GBimPanelHost.Open); }
        catch { GBimPanelHost.Open(); }
    }
}
