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
    private static readonly Color RootAmber = Color.FromArgb(255, 198, 134, 42);

    private static Bitmap? _projectRoot;
    private static Bitmap? _family;
    private static Bitmap? _surveySchema;
    private static Bitmap? _surveyClassify;
    private static bool _reported;

    /// <summary>Green root: "this is where the project begins".</summary>
    public static Bitmap? ProjectRoot => _projectRoot ??= Render(RootGreen);

    /// <summary>Plain ink root: the plugin- and ribbon-tab mark.</summary>
    public static Bitmap? Family => _family ??= Render(Ink);

    /// <summary>Hollow amber root: the survey vocabulary, held but not yet applied.</summary>
    public static Bitmap? SurveySchema => _surveySchema ??= Render(RootAmber, hollow: true);

    /// <summary>Filled amber root: the vocabulary settled onto geometry.</summary>
    public static Bitmap? SurveyClassify => _surveyClassify ??= Render(RootAmber);

    // Four marks is past what root colour alone can carry at 24px, so the root gains a
    // second axis - filled or hollow - rather than the silhouette changing per component.
    //
    // Three ways down, because the GDI+ under this code is not the same everywhere: Rhino
    // on macOS draws through libgdiplus, which implements neither the ImageAttributes
    // overload of DrawImage nor every interpolation mode the resampling step asks for.
    // Each rung uses cheaper primitives than the one above, so a gap in the host costs
    // sharpness rather than the whole mark.
    //
    // Each rung is checked for ink rather than only for not throwing. libgdiplus answers
    // an unimplemented drawing call by doing nothing at all as readily as by throwing,
    // and a silently empty bitmap would otherwise walk straight past a try/catch and onto
    // the canvas as an empty square.
    private static Bitmap? Render(Color rootFill, bool hollow = false)
    {
        var rungs = new (string Stage, Func<Bitmap> Make)[]
        {
            ("supersampled resample", () => { using var hi = Draw(rootFill, hollow, Supersample); return Resample(hi, sharp: true); }),
            ("plain resample", () => { using var hi = Draw(rootFill, hollow, Supersample); return Resample(hi, sharp: false); }),
            ("direct draw", () => Draw(rootFill, hollow, scale: 1)),
        };

        foreach (var (stage, make) in rungs)
        {
            try
            {
                var icon = make();
                if (HasInk(icon)) return icon;

                icon.Dispose();
                Report(stage, "it produced an empty image");
            }
            catch (Exception ex)
            {
                Report(stage, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Null, not a blank bitmap: Grasshopper draws a legible name tile for a null icon
        // and an empty square for a transparent one.
        return null;
    }

    private static bool HasInk(Bitmap icon)
    {
        try
        {
            for (var y = 0; y < icon.Height; y++)
                for (var x = 0; x < icon.Width; x++)
                    if (icon.GetPixel(x, y).A > 8) return true;

            return false;
        }
        catch (Exception)
        {
            // No way to tell, so assume the mark drew rather than discarding a good one.
            return true;
        }
    }

    /// <summary>
    /// Said once per session, not once per mark: four identical lines would read as four
    /// problems. A throwing icon must never take the ribbon tab down with it, so this is
    /// the only trace the failure leaves.
    /// </summary>
    private static void Report(string stage, string reason)
    {
        if (_reported) return;
        _reported = true;

        try
        {
            Rhino.RhinoApp.WriteLine($"[G-Loom] Canvas marks fell back from {stage}: {reason}");
        }
        catch (Exception)
        {
            // Nothing left to report it to.
        }
    }

    private static Bitmap Draw(Color rootFill, bool hollow, int scale)
    {
        float k = scale;
        var bmp = new Bitmap(Dim * scale, Dim * scale, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var branch = new Pen(Ink, 1.9f * k)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawLine(branch, 12f * k, 18.4f * k, 12f * k, 12.6f * k);
        g.DrawBezier(branch, P(12f, 13f, k), P(12f, 9.5f, k), P(6.6f, 10f, k), P(6.6f, 6.6f, k));
        g.DrawBezier(branch, P(12f, 13f, k), P(12f, 9.5f, k), P(17.4f, 10f, k), P(17.4f, 6.6f, k));

        using var outline = new Pen(Ink, 1.7f * k);
        using var tip = new SolidBrush(TipFill);
        Disc(g, tip, outline, 6.6f, 4.9f, 2.15f, k);
        Disc(g, tip, outline, 17.4f, 4.9f, 2.15f, k);

        using var root = new SolidBrush(hollow ? TipFill : rootFill);
        using var rootEdge = new Pen(rootFill, 1.7f * k);
        Disc(g, root, hollow ? rootEdge : outline, 12f, 19.2f, 3.4f, k);

        return bmp;
    }

    private static Bitmap Resample(Bitmap source, bool sharp)
    {
        var icon = new Bitmap(Dim, Dim, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(icon);
        g.Clear(Color.Transparent);

        if (!sharp)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(source, new Rectangle(0, 0, Dim, Dim));
            return icon;
        }

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // TileFlipXY stops the bicubic kernel from sampling transparent black
        // past the edges, which otherwise rims the mark with a faint halo.
        using var edge = new ImageAttributes();
        edge.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(source, new Rectangle(0, 0, Dim, Dim),
            0, 0, source.Width, source.Height, GraphicsUnit.Pixel, edge);
        return icon;
    }

    private static PointF P(float x, float y, float k) => new(x * k, y * k);

    private static void Disc(Graphics g, Brush fill, Pen outline, float cx, float cy, float r, float k)
    {
        var box = new RectangleF((cx - r) * k, (cy - r) * k, r * 2f * k, r * 2f * k);
        g.FillEllipse(fill, box);
        g.DrawEllipse(outline, box);
    }
}
