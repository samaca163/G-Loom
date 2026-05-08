using System;
using GBim.Vcs;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace GBim.Ui;

/// <summary>
/// Registers the G-BIM Eto panel with Rhino's panel system. Borrows
/// Grasshopper's plugin instance for registration since .gha plugins aren't
/// directly Rhino plugins.
/// </summary>
public static class GBimPanelHost
{
    private static readonly Guid GrasshopperPluginId = new("b45a29b1-4343-4035-989e-044e8580d9cf");

    private static bool _registered;

    public static bool IsRegistered => _registered;

    public static void Register()
    {
        if (_registered) return;

        var ghPlugin = PlugIn.Find(GrasshopperPluginId);
        if (ghPlugin is null)
        {
            RhinoApp.WriteLine("[G-BIM] Could not find Grasshopper plugin; panel not registered.");
            return;
        }

        try
        {
            Panels.RegisterPanel(ghPlugin, typeof(GBimPanel), "G-BIM", null);
            _registered = true;
            RhinoApp.WriteLine("[G-BIM] Panel registered.");
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-BIM] Panel registration failed: {ex.Message}");
        }
    }

    public static void Open()
    {
        if (!_registered)
        {
            RhinoApp.WriteLine("[G-BIM] Panel is not registered; cannot open.");
            return;
        }
        try { Panels.OpenPanel(GBimPanel.PanelId); }
        catch (Exception ex) { RhinoApp.WriteLine($"[G-BIM] Could not open panel: {ex.Message}"); }
    }
}
