using Grasshopper.GUI.Canvas;
using Rhino;

namespace GBim.Canvas;

/// <summary>
/// Subscribes to the canvas post-paint events. In Phase 0 it only logs the first
/// paint to prove the hooks fire; Phase 1 will paint diff overlays here.
/// </summary>
internal static class DiffOverlayPainter
{
    private static bool _attached;
    private static bool _firstPaintLogged;

    public static void Attach(GH_Canvas canvas)
    {
        if (_attached || canvas is null) return;

        canvas.CanvasPostPaintObjects += OnPostPaintObjects;
        canvas.CanvasPostPaintWires += OnPostPaintWires;
        _attached = true;
    }

    private static void OnPostPaintObjects(GH_Canvas sender)
        => LogFirstPaint("CanvasPostPaintObjects");

    private static void OnPostPaintWires(GH_Canvas sender)
        => LogFirstPaint("CanvasPostPaintWires");

    private static void LogFirstPaint(string source)
    {
        if (_firstPaintLogged) return;
        _firstPaintLogged = true;
        RhinoApp.WriteLine($"[G-BIM] First canvas paint via {source} - overlay machinery is wired up.");
    }
}
