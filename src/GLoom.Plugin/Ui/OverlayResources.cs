using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace GLoom.Ui;

/// <summary>
/// Process-lifetime GDI+ objects for the canvas diff overlay. The paint
/// handler runs every canvas frame; creating fonts, pens and brushes
/// per frame (13 Font sites, ~50 objects/frame before this existed) was
/// a measurable part of the pan/zoom stutter. Everything here is
/// deliberately never disposed and only touched from the UI thread.
///
/// One shared AdjustableArrowCap feeds both arrow pens: disposing a Pen
/// does NOT dispose its CustomEndCap, so the previous per-frame
/// `new AdjustableArrowCap` inside `using var pen` leaked a native GDI+
/// handle per moved component per frame.
/// </summary>
internal static class OverlayResources
{
    // Fonts (all Segoe UI, matching the sizes the painters always used).
    public static readonly Font F75 = new("Segoe UI", 7.5f);
    public static readonly Font F8 = new("Segoe UI", 8f);
    public static readonly Font F8Bold = new("Segoe UI", 8f, FontStyle.Bold);
    public static readonly Font F85 = new("Segoe UI", 8.5f);
    public static readonly Font F9 = new("Segoe UI", 9f);
    public static readonly Font F9Bold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font F11Bold = new("Segoe UI", 11f, FontStyle.Bold);

    private static readonly AdjustableArrowCap ArrowCap = new(5f, 6f, true);

    // Pens.
    public static readonly Pen HaloAdded = new(Color.FromArgb(220, 60, 200, 60), 3f);
    public static readonly Pen HaloModified = new(Color.FromArgb(220, 230, 200, 0), 3f);
    public static readonly Pen HaloMoved = new(Color.FromArgb(220, 60, 130, 220), 3f);
    public static readonly Pen YellowOutline = new(Color.FromArgb(220, 230, 200, 0), 1.5f);
    public static readonly Pen YellowDottedOutline = new(Color.FromArgb(220, 230, 200, 0), 1.5f)
    {
        DashStyle = DashStyle.Dot,
    };
    public static readonly Pen TrailOutline = new(Color.FromArgb(200, 60, 130, 220), 2f)
    {
        DashStyle = DashStyle.Dash,
    };
    public static readonly Pen MovedArrow = new(Color.FromArgb(220, 60, 130, 220), 2.5f)
    {
        CustomEndCap = ArrowCap,
    };
    public static readonly Pen MissingWire = new(Color.FromArgb(220, 220, 40, 40), 1.8f)
    {
        DashStyle = DashStyle.Dash,
        CustomEndCap = ArrowCap,
    };
    public static readonly Pen AddedWire = new(Color.FromArgb(220, 60, 200, 60), 2.5f);
    public static readonly Pen SliderTrack = new(Color.FromArgb(180, 130, 100, 0), 1.5f);
    public static readonly Pen RedGhostOutline = new(Color.FromArgb(220, 220, 40, 40), 2.5f);
    public static readonly Pen RedTrack = new(Color.FromArgb(220, 180, 30, 30), 1.5f);
    public static readonly Pen RedInnerOutline = new(Color.FromArgb(220, 180, 20, 20), 1.5f);

    // Brushes.
    public static readonly SolidBrush PanelPreviewFill = new(Color.FromArgb(235, 252, 245, 200));
    public static readonly SolidBrush PanelPreviewHeader = new(Color.FromArgb(255, 110, 80, 0));
    public static readonly SolidBrush PanelPreviewBody = new(Color.FromArgb(255, 60, 50, 10));
    public static readonly SolidBrush TrailFill = new(Color.FromArgb(40, 60, 130, 220));
    public static readonly SolidBrush GhostFill = new(Color.FromArgb(50, 230, 200, 0));
    public static readonly SolidBrush GhostText = new(Color.FromArgb(220, 110, 80, 0));
    public static readonly SolidBrush GhostLabelText = new(Color.FromArgb(230, 110, 80, 0));
    public static readonly SolidBrush SliderRangeDefault = new(Color.FromArgb(220, 130, 100, 0));
    public static readonly SolidBrush SliderRangeChanged = new(Color.FromArgb(255, 230, 120, 20));
    public static readonly SolidBrush RedGhostFill = new(Color.FromArgb(40, 220, 40, 40));
    public static readonly SolidBrush RedName = new(Color.FromArgb(255, 200, 30, 30));
    public static readonly SolidBrush RedKnob = new(Color.FromArgb(240, 220, 40, 40));
    public static readonly SolidBrush RedRange = new(Color.FromArgb(220, 180, 30, 30));
    public static readonly SolidBrush RedValue = new(Color.FromArgb(255, 180, 20, 20));
    public static readonly SolidBrush RedBool = new(Color.FromArgb(255, 200, 20, 20));

    // Theme-parameterized helpers for the painters that take a color
    // argument (gradient bar, MD slider box). Only the handful of fixed
    // theme colors ever reach these, so the caches stay tiny; arbitrary
    // user colors (swatch fills) keep their per-call brushes instead.
    private static readonly ConcurrentDictionary<int, Pen> s_pens15 = new();
    private static readonly ConcurrentDictionary<int, SolidBrush> s_fills40 = new();
    private static readonly ConcurrentDictionary<int, SolidBrush> s_brushes = new();

    public static Pen Pen15(Color color) =>
        s_pens15.GetOrAdd(color.ToArgb(), _ => new Pen(color, 1.5f));

    public static SolidBrush Fill40(Color color) =>
        s_fills40.GetOrAdd(
            Color.FromArgb(40, color.R, color.G, color.B).ToArgb(),
            _ => new SolidBrush(Color.FromArgb(40, color.R, color.G, color.B)));

    public static SolidBrush Brush(Color color) =>
        s_brushes.GetOrAdd(color.ToArgb(), _ => new SolidBrush(color));
}
