using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GLoom.Ui;

/// <summary>
/// The G-Loom canvas marks, drawn rather than embedded so the plugin stays a
/// single .gha with no resource satellite and the family mark can never drift
/// away from its per-component variants.
///
/// The mark is a commit graph read bottom-up: two branch tips converging into
/// one root node. Everything above the root is the shared G-Loom signature;
/// a component says what it is by how the root is filled.
/// </summary>
internal static class GLoomIcons
{
    private const int Dim = 24;

    // GDI+ anti-aliasing of a 1.9px bezier at 24px leaves visible stair-steps;
    // drawing 4x up and resampling down matches what a real rasterizer gives.
    private const int Supersample = 4;

    private static readonly Color Ink = Color.FromArgb(255, 38, 42, 38);
    private static readonly Color TipFill = Color.FromArgb(255, 242, 244, 240);
    private static readonly Color RootGreen = Color.FromArgb(255, 47, 158, 99);

    private static Bitmap? _projectRoot;
    private static Bitmap? _family;

    /// <summary>Green root: "this is where the project begins".</summary>
    public static Bitmap ProjectRoot => _projectRoot ??= Render(RootGreen);

    /// <summary>Plain ink root: the plugin- and ribbon-tab mark.</summary>
    public static Bitmap Family => _family ??= Render(Ink);

    private static Bitmap Render(Color rootFill)
    {
        try
        {
            using var hi = RenderSupersampled(rootFill);
            return Downsample(hi);
        }
        catch (Exception)
        {
            // A missing icon costs a blank ribbon slot; a throwing one takes
            // the whole tab down with it.
            return new Bitmap(Dim, Dim, PixelFormat.Format32bppArgb);
        }
    }

    private static Bitmap RenderSupersampled(Color rootFill)
    {
        const float k = Supersample;
        var bmp = new Bitmap(Dim * Supersample, Dim * Supersample, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var branch = new Pen(Ink, 1.9f * k)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawLine(branch, 12f * k, 18.4f * k, 12f * k, 12.6f * k);
        g.DrawBezier(branch, P(12f, 13f), P(12f, 9.5f), P(6.6f, 10f), P(6.6f, 6.6f));
        g.DrawBezier(branch, P(12f, 13f), P(12f, 9.5f), P(17.4f, 10f), P(17.4f, 6.6f));

        using var outline = new Pen(Ink, 1.7f * k);
        using var tip = new SolidBrush(TipFill);
        Disc(g, tip, outline, 6.6f, 4.9f, 2.15f);
        Disc(g, tip, outline, 17.4f, 4.9f, 2.15f);

        using var root = new SolidBrush(rootFill);
        Disc(g, root, outline, 12f, 19.2f, 3.4f);

        return bmp;
    }

    private static Bitmap Downsample(Bitmap source)
    {
        var icon = new Bitmap(Dim, Dim, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(icon);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // TileFlipXY stops the bicubic kernel from sampling transparent black
        // past the edges, which otherwise rims the mark with a faint halo.
        using var edge = new ImageAttributes();
        edge.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(source, new Rectangle(0, 0, Dim, Dim),
            0, 0, source.Width, source.Height, GraphicsUnit.Pixel, edge);
        return icon;
    }

    private static PointF P(float x, float y) => new(x * Supersample, y * Supersample);

    private static void Disc(Graphics g, Brush fill, Pen outline, float cx, float cy, float r)
    {
        const float k = Supersample;
        var box = new RectangleF((cx - r) * k, (cy - r) * k, r * 2f * k, r * 2f * k);
        g.FillEllipse(fill, box);
        g.DrawEllipse(outline, box);
    }
}
