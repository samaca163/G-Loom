using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
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
            var icon = BuildLogo();
            Panels.RegisterPanel(ghPlugin, typeof(GBimPanel), "G-BIM", icon);
            _registered = true;
            RhinoApp.WriteLine($"[G-BIM] Panel registered (icon: {(icon != null ? $"{icon.Width}x{icon.Height}" : "none")}).");
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-BIM] Panel registration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Renders a "G" mark and packages it as a 32-bit-alpha .ico (PNG payload,
    /// Vista+ format) so Rhino's panel tabs render the alpha properly. The
    /// older Bitmap.GetHicon() path produces an HICON with a 1-bit mask,
    /// which makes anti-aliased text disappear into the mask and shows up as
    /// a blank tab.
    /// </summary>
    private static Icon? BuildLogo()
    {
        try
        {
            byte[] png32 = RenderGlyphPng(32, 24f);
            return PngToIcon(png32, 32);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-BIM] Could not build logo icon: {ex.Message}");
            return null;
        }
    }

    private static byte[] RenderGlyphPng(int dim, float fontPx)
    {
        using var bmp = new Bitmap(dim, dim, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            var size = g.MeasureString("G", font);
            g.DrawString("G", font, brush,
                (dim - size.Width) / 2f,
                (dim - size.Height) / 2f - 1f);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static Icon PngToIcon(byte[] png, int dim)
    {
        // ICO file with one PNG-encoded image.
        // Layout: ICONDIR (6) + ICONDIRENTRY (16) + PNG bytes.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // ICONDIR
        w.Write((ushort)0);   // reserved
        w.Write((ushort)1);   // type = ICO
        w.Write((ushort)1);   // image count
        // ICONDIRENTRY
        w.Write((byte)(dim >= 256 ? 0 : dim)); // width (0 means 256)
        w.Write((byte)(dim >= 256 ? 0 : dim)); // height
        w.Write((byte)0);     // palette colors (0 = >= 256)
        w.Write((byte)0);     // reserved
        w.Write((ushort)1);   // color planes
        w.Write((ushort)32);  // bits per pixel
        w.Write((uint)png.Length);
        w.Write((uint)22);    // offset = 6 + 16
        w.Write(png);
        ms.Position = 0;
        return new Icon(ms);
    }

    public static void Open()
    {
        if (!_registered)
        {
            RhinoApp.WriteLine("[G-BIM] Panel is not registered; cannot open.");
            return;
        }
        try
        {
            // Dock alongside any already-open panel (Properties, Layers, etc.)
            // instead of opening a fresh floating window. A panel that has only
            // ever lived as a floating window does NOT appear in Rhino's
            // panel-tab right-click selector, so users who close it can't get
            // it back. Docking it once teaches Rhino the layout and the panel
            // sticks in the selector from then on. After the first dock the
            // user can drag it anywhere; Rhino remembers the position.
            var openIds = Panels.GetOpenPanelIds();
            if (openIds != null)
            {
                foreach (var pid in openIds)
                {
                    if (pid == GBimPanel.PanelId) continue;
                    Panels.OpenPanelAsSibling(GBimPanel.PanelId, pid, true);
                    // AsSibling's makeSelected flag isn't always honored when
                    // the host panel grabs focus right after; re-issue
                    // OpenPanel to force ours to the front of its tab group.
                    Panels.OpenPanel(GBimPanel.PanelId, true);
                    return;
                }
            }
            // No host panel to dock against - fall back to the default open
            // (will float, but at least we tried).
            Panels.OpenPanel(GBimPanel.PanelId, true);
        }
        catch (Exception ex) { RhinoApp.WriteLine($"[G-BIM] Could not open panel: {ex.Message}"); }
    }
}
