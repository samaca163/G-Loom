using System;
using GLoom.Components;
using GLoom.Ui;
using GLoom.Vcs;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Rhino;

namespace GLoom;

public sealed class GLoomPriorityLoad : GH_AssemblyPriority
{
    public override GH_LoadingInstruction PriorityLoad()
    {
        RegisterRibbonTab();
        DocumentTracker.Instance.Initialize();
        CanvasDiffOverlay.Instance.Initialize();
        GLoomPanelHost.Register();

        Instances.CanvasCreated += OnCanvasCreated;
        if (Instances.ActiveCanvas != null)
            OnCanvasCreated(Instances.ActiveCanvas);

        RhinoApp.WriteLine("[G-Loom] Plugin loaded.");
        return GH_LoadingInstruction.Proceed;
    }

    /// <summary>
    /// Gives the G-Loom ribbon tab the plugin's mark. Without this Grasshopper
    /// falls back to a generated letter tile for the category.
    /// </summary>
    private static void RegisterRibbonTab()
    {
        try
        {
            // The symbol name is registered either way: it is what Grasshopper falls back
            // to when the mark could not be drawn, and it is the only branding left then.
            var mark = GLoomIcons.Family;
            if (mark is not null)
                Instances.ComponentServer.AddCategoryIcon(GLoomComponent.Tab, mark);
            Instances.ComponentServer.AddCategorySymbolName(GLoomComponent.Tab, 'G');
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Could not brand the ribbon tab: {ex.Message}");
        }
    }

    private static bool _panelAutoOpened;

    private static void OnCanvasCreated(GH_Canvas canvas)
    {
        // Auto-open the panel once on first canvas creation. After that the user
        // can close/dock/move it like any other Rhino panel, and re-open via
        // Rhino's panel menu (right-click any docked panel tab to see the list).
        // Schedule on the Eto UI thread to be safe.
        if (_panelAutoOpened) return;
        _panelAutoOpened = true;
        try { Eto.Forms.Application.Instance.AsyncInvoke(GLoomPanelHost.Open); }
        catch { GLoomPanelHost.Open(); }
    }
}
