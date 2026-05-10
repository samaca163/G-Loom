using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using GLoom.Serialization;
using GLoom.Vcs;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Rhino;
using Bounds = GLoom.Serialization.Bounds;
using WinFormsMouseEventArgs = System.Windows.Forms.MouseEventArgs;

namespace GLoom.Ui;

/// <summary>
/// Singleton overlay that paints colored highlights on top of the
/// Grasshopper canvas to surface what's changed since HEAD: green halos
/// around added nodes, yellow around modified, blue around moved-only,
/// and red ghost markers at the old pivot of removed nodes (which
/// aren't on the live canvas anymore). Driven by the same DocumentDiff
/// engine the per-commit drawer inspector uses; on the overlay side the
/// "from" is always HEAD's .gloom.json and the "to" is the live
/// document state via DocumentSerializer.
///
/// Throttled by a 250ms minimum recompute interval so a flurry of
/// SolutionEnd events during slider drag doesn't pin the CPU. The
/// CanvasPostPaintObjects event fires every paint cycle anyway, so
/// the cached diff renders for free between recomputes.
/// </summary>
public sealed class CanvasDiffOverlay
{
    private static readonly Lazy<CanvasDiffOverlay> _instance = new(() => new CanvasDiffOverlay());
    public static CanvasDiffOverlay Instance => _instance.Value;

    // Translucent so the underlying node rendering still reads through.
    private static readonly Color AddedColor    = Color.FromArgb(220,  60, 200,  60);
    private static readonly Color ModifiedColor = Color.FromArgb(220, 230, 200,   0);
    private static readonly Color MovedColor    = Color.FromArgb(220,  60, 130, 220);
    private static readonly Color RemovedColor  = Color.FromArgb(220, 220,  40,  40);

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMilliseconds(250);

    public bool Enabled { get; private set; }

    private bool _showAdded = true;
    private bool _showModified = true;
    private bool _showMoved = true;
    private bool _showDeleted = true;
    private bool _hoverDetailsOnly;

    public bool ShowAdded { get => _showAdded; set => SetAndRefresh(ref _showAdded, value); }
    public bool ShowModified { get => _showModified; set => SetAndRefresh(ref _showModified, value); }
    public bool ShowMoved { get => _showMoved; set => SetAndRefresh(ref _showMoved, value); }
    public bool ShowDeleted { get => _showDeleted; set => SetAndRefresh(ref _showDeleted, value); }
    public bool HoverDetailsOnly { get => _hoverDetailsOnly; set => SetAndRefresh(ref _hoverDetailsOnly, value); }

    public event EventHandler? EnabledChanged;
    public event EventHandler? SettingsChanged;

    private DocumentDiff? _cachedDiff;
    private HashSet<string> _hoverableIds = new(StringComparer.Ordinal);
    private string? _hoveredId;
    private DateTime _lastComputed = DateTime.MinValue;
    private bool _initialized;

    private void SetAndRefresh(ref bool field, bool value)
    {
        if (field == value) return;
        field = value;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (Enabled) Instances.ActiveCanvas?.Refresh();
    }

    private CanvasDiffOverlay() { }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Instances.CanvasCreated += HookCanvas;
        if (Instances.ActiveCanvas is { } ac) HookCanvas(ac);

        // Tab/file/repo switches invalidate the cache so the next paint
        // recomputes against the new document's HEAD.
        DocumentTracker.Instance.StateChanged += (_, _) =>
        {
            _lastComputed = DateTime.MinValue;
            _cachedDiff = null;
            if (Enabled) Instances.ActiveCanvas?.Refresh();
        };
    }

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        _lastComputed = DateTime.MinValue;
        EnabledChanged?.Invoke(this, EventArgs.Empty);
        Instances.ActiveCanvas?.Refresh();
        RhinoApp.WriteLine($"[G-Loom] Diff overlay {(enabled ? "on" : "off")}.");
    }

    private void HookCanvas(GH_Canvas canvas)
    {
        if (canvas is null) return;
        canvas.CanvasPostPaintObjects += OnPostPaintObjects;
        canvas.MouseMove += OnCanvasMouseMove;
        canvas.MouseLeave += OnCanvasMouseLeave;
    }

    private void OnCanvasMouseMove(object? sender, WinFormsMouseEventArgs e)
    {
        if (!Enabled) return;
        if (sender is not GH_Canvas canvas || canvas.Document is null) return;
        if (_hoverableIds.Count == 0)
        {
            ClearHover(canvas);
            return;
        }

        PointF worldPt;
        try
        {
            worldPt = canvas.Viewport.UnprojectPoint(new Point(e.X, e.Y));
        }
        catch
        {
            return;
        }

        string? newHover = null;
        foreach (var obj in canvas.Document.Objects)
        {
            var id = obj.InstanceGuid.ToString("D");
            if (!_hoverableIds.Contains(id)) continue;
            var bounds = obj.Attributes?.Bounds ?? RectangleF.Empty;
            if (!bounds.IsEmpty && bounds.Contains(worldPt))
                newHover = id;
        }

        if (newHover != _hoveredId)
        {
            _hoveredId = newHover;
            canvas.Refresh();
        }
    }

    private void OnCanvasMouseLeave(object? sender, EventArgs e)
    {
        if (sender is GH_Canvas canvas) ClearHover(canvas);
    }

    private void ClearHover(GH_Canvas canvas)
    {
        if (_hoveredId is null) return;
        _hoveredId = null;
        if (Enabled) canvas.Refresh();
    }

    private void OnPostPaintObjects(GH_Canvas canvas)
    {
        if (!Enabled) return;

        var now = DateTime.UtcNow;
        if (now - _lastComputed > StaleAfter)
        {
            _cachedDiff = ComputeLiveDiff();
            _lastComputed = now;
            RebuildHoverableIndex();
        }

        var diff = _cachedDiff;
        if (diff is null || diff.IsEmpty) return;

        try
        {
            Paint(canvas, diff, this, _hoveredId);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Overlay paint error: {ex.Message}");
        }
    }

    private void RebuildHoverableIndex()
    {
        _hoverableIds.Clear();
        if (_cachedDiff is null) return;
        foreach (var c in _cachedDiff.ObjectsModified)
        {
            // Anything with extras worth revealing on hover: a movement
            // trail, a persistent ghost-below, or a panel ghost-above.
            if ((c.Kinds & (ObjectChangeKind.Moved | ObjectChangeKind.PersistentChanged)) != 0)
                _hoverableIds.Add(c.To.InstanceGuid);
        }
    }

    private static DocumentDiff? ComputeLiveDiff()
    {
        var state = DocumentTracker.Instance.State;
        if (state.Document is null
            || state.RepoPath is null
            || state.FilePath is null
            || state.CanonicalJsonFullPath is null)
            return null;

        var jsonRel = Path.GetRelativePath(state.RepoPath, state.CanonicalJsonFullPath);
        var headJson = GLoomRepository.ReadFileAtCommit(state.RepoPath, "HEAD", jsonRel);
        var headDoc = CanonicalJson.TryParse(headJson);
        if (headDoc is null) return null;

        CanonicalDocument liveDoc;
        try
        {
            liveDoc = DocumentSerializer.Serialize(state.Document);
        }
        catch
        {
            return null;
        }

        return DocumentDiff.Compute(headDoc, liveDoc);
    }

    private static void Paint(GH_Canvas canvas, DocumentDiff diff, CanvasDiffOverlay s, string? hoveredId)
    {
        var graphics = canvas.Graphics;
        var doc = canvas.Document;
        if (graphics is null || doc is null) return;

        // Live objects keyed by the same string form CanonicalObject uses
        // (Guid.ToString("D")), so the lookup matches the diff entries.
        var liveById = doc.Objects.ToDictionary(o => o.InstanceGuid.ToString("D"));

        if (s.ShowDeleted)
            foreach (var removed in diff.ObjectsRemoved)
                PaintDeletedGhost(graphics, removed);

        // Bucket each modified change as either "Moved" (move-only) or
        // "Modified" (everything else). A change carrying both Moved AND
        // a non-Moved kind goes in Modified so it doesn't disappear when
        // the user only unchecked Moved.
        foreach (var change in diff.ObjectsModified)
        {
            if (!liveById.TryGetValue(change.To.InstanceGuid, out var live)) continue;
            var bucket = change.Kinds == ObjectChangeKind.Moved
                ? ChangeBucket.Moved
                : ChangeBucket.Modified;
            if (!ShowsBucket(s, bucket)) continue;

            var isHovered = hoveredId == change.To.InstanceGuid;
            var showExtras = !s.HoverDetailsOnly || isHovered;

            if ((change.Kinds & ObjectChangeKind.Moved) != 0 && s.ShowMoved && showExtras)
                PaintMovementTrail(graphics, live, change.From);

            if ((change.Kinds & ObjectChangeKind.PersistentChanged) != 0
                && s.ShowModified
                && showExtras
                && change.From.Persistent?.Kind != "panel"
                && change.To.Persistent?.Kind != "panel")
            {
                PaintPersistentGhost(graphics, live, change.From, change.To);
            }
        }

        // Halos paint after extras so they sit on top.
        foreach (var change in diff.ObjectsModified)
        {
            if (!liveById.TryGetValue(change.To.InstanceGuid, out var live)) continue;
            var bucket = change.Kinds == ObjectChangeKind.Moved
                ? ChangeBucket.Moved
                : ChangeBucket.Modified;
            if (!ShowsBucket(s, bucket)) continue;

            var color = change.Kinds == ObjectChangeKind.Moved ? MovedColor : ModifiedColor;
            PaintHalo(graphics, live, color);
        }

        if (s.ShowAdded)
        {
            foreach (var added in diff.ObjectsAdded)
                if (liveById.TryGetValue(added.InstanceGuid, out var live))
                    PaintHalo(graphics, live, AddedColor);
        }

        // Panel previews follow the HoverDetailsOnly mode: in always-show,
        // every changed panel renders its preview above; in hover-only,
        // only the hovered panel does.
        if (s.ShowModified)
        {
            foreach (var change in diff.ObjectsModified)
            {
                if (change.From.Persistent?.Kind != "panel") continue;
                if (!liveById.TryGetValue(change.To.InstanceGuid, out var live)) continue;

                var isHovered = hoveredId == change.To.InstanceGuid;
                if (s.HoverDetailsOnly && !isHovered) continue;

                PaintPanelHoverPreview(graphics, live, change.From);
            }
        }
    }

    private enum ChangeBucket { Moved, Modified }

    private static bool ShowsBucket(CanvasDiffOverlay s, ChangeBucket b) => b switch
    {
        ChangeBucket.Moved    => s.ShowMoved,
        ChangeBucket.Modified => s.ShowModified,
        _                     => false,
    };

    /// <summary>
    /// Hover preview anchored above the live panel: yellow tooltip-style
    /// box with a "was:" header and the OLD panel content wrapped to the
    /// panel's width. Only invoked for wired panels (the diff already
    /// filtered standalone-panel content edits out).
    /// </summary>
    private static void PaintPanelHoverPreview(Graphics g, IGH_DocumentObject live, CanonicalObject from)
    {
        var oldText = from.Persistent?.PanelText ?? string.Empty;
        if (string.IsNullOrEmpty(oldText)) oldText = "(empty)";

        var liveBounds = live.Attributes?.Bounds ?? RectangleF.Empty;
        if (liveBounds.IsEmpty) return;

        using var headerFont = new Font("Segoe UI", 8f, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", 9f);

        var maxBodyWidth = Math.Max(liveBounds.Width, 220f) - 12f;
        var headerSize = g.MeasureString("was:", headerFont);
        var bodySize = g.MeasureString(oldText, bodyFont, (int)maxBodyWidth);

        var boxW = Math.Max(liveBounds.Width, bodySize.Width + 12f);
        var boxH = headerSize.Height + 4f + bodySize.Height + 10f;
        var box = new RectangleF(
            liveBounds.X,
            liveBounds.Top - boxH - 6f,
            boxW,
            boxH);

        using var fill = new SolidBrush(Color.FromArgb(235, 252, 245, 200));
        using var outline = new Pen(Color.FromArgb(220, 230, 200, 0), 1.5f);
        g.FillRectangle(fill, box);
        g.DrawRectangle(outline, box.X, box.Y, box.Width, box.Height);

        using var headerBrush = new SolidBrush(Color.FromArgb(255, 110, 80, 0));
        g.DrawString("was:", headerFont, headerBrush, box.X + 6f, box.Y + 5f);

        using var bodyBrush = new SolidBrush(Color.FromArgb(255, 60, 50, 10));
        var bodyRect = new RectangleF(
            box.X + 6f,
            box.Y + 5f + headerSize.Height + 2f,
            box.Width - 12f,
            bodySize.Height);
        g.DrawString(oldText, bodyFont, bodyBrush, bodyRect);
    }

    private static void PaintHalo(Graphics g, IGH_DocumentObject obj, Color color)
    {
        var bounds = obj.Attributes?.Bounds ?? RectangleF.Empty;
        if (bounds.IsEmpty) return;
        var halo = bounds;
        halo.Inflate(6f, 6f);
        using var pen = new Pen(color, 3f);
        g.DrawRectangle(pen, halo.X, halo.Y, halo.Width, halo.Height);
    }

    /// <summary>
    /// Movement viz: a dashed translucent rect at the OLD position (same
    /// size as the live one — pure moves don't change size) plus a solid
    /// arrow from the old center to the new center.
    /// </summary>
    private static void PaintMovementTrail(Graphics g, IGH_DocumentObject live, CanonicalObject from)
    {
        var liveBounds = live.Attributes?.Bounds ?? RectangleF.Empty;
        var livePivot = live.Attributes?.Pivot ?? PointF.Empty;
        if (liveBounds.IsEmpty) return;

        var dx = from.Pivot.X - livePivot.X;
        var dy = from.Pivot.Y - livePivot.Y;
        var oldBounds = new RectangleF(
            liveBounds.X + dx,
            liveBounds.Y + dy,
            liveBounds.Width,
            liveBounds.Height);

        using var fill = new SolidBrush(Color.FromArgb(40, 60, 130, 220));
        using var outline = new Pen(Color.FromArgb(200, 60, 130, 220), 2f)
        {
            DashStyle = DashStyle.Dash,
        };
        g.FillRectangle(fill, oldBounds);
        g.DrawRectangle(outline, oldBounds.X, oldBounds.Y, oldBounds.Width, oldBounds.Height);

        var oldCenter = new PointF(
            oldBounds.X + oldBounds.Width / 2f,
            oldBounds.Y + oldBounds.Height / 2f);
        var newCenter = new PointF(
            liveBounds.X + liveBounds.Width / 2f,
            liveBounds.Y + liveBounds.Height / 2f);

        // Skip the arrow if the move is tiny (centers within a few pixels
        // - happens during attribute sync and reads as visual noise).
        var manhattan = Math.Abs(oldCenter.X - newCenter.X) + Math.Abs(oldCenter.Y - newCenter.Y);
        if (manhattan < 8f) return;

        using var arrowPen = new Pen(MovedColor, 2.5f)
        {
            CustomEndCap = new AdjustableArrowCap(5f, 6f, true),
        };
        g.DrawLine(arrowPen, oldCenter, newCenter);
    }

    /// <summary>
    /// Persistent-change viz: a "ghost" panel rendered just below the live
    /// component showing the OLD persistent state. Slider gets a phantom
    /// track + knob; color swatch gets a fill of the old color; the rest
    /// fall back to a labeled rect ("was: ...").
    /// </summary>
    private static void PaintPersistentGhost(
        Graphics g, IGH_DocumentObject live, CanonicalObject from, CanonicalObject to)
    {
        var liveBounds = live.Attributes?.Bounds ?? RectangleF.Empty;
        if (liveBounds.IsEmpty) return;

        var oldKind = from.Persistent?.Kind ?? "(none)";
        var oldData = from.Persistent;

        // Per-kind ghost sizing. Sliders need vertical room for track +
        // value text. Value-list label is multi-line and we measure it
        // to avoid bloating the rect when the live component (e.g. a
        // CheckList-mode value list) is much taller than the label
        // needs; also gets extra horizontal padding so its multi-line
        // text doesn't wrap aggressively in narrow components. Color
        // matches live since the swatch IS the visual. Other kinds cap
        // at a small height so a tall live component doesn't produce
        // a tall mostly-empty ghost.
        using var labelFontForSizing = new Font("Segoe UI", 8f);
        var ghostX = liveBounds.X;
        var ghostWidth = liveBounds.Width;
        float ghostHeight;
        if (oldKind == "slider")
        {
            ghostHeight = Math.Max(34f, liveBounds.Height + 12f);
        }
        else if (oldKind == "valuelist")
        {
            // Modest extra horizontal room (30px each side) so labels
            // like "mode: DropDown → CheckList" don't break mid-word
            // in narrow value lists. Centered on the live component.
            const float extraSide = 30f;
            ghostX = liveBounds.X - extraSide;
            ghostWidth = liveBounds.Width + extraSide * 2f;

            var preview = SummarizeValueListLabel(from.Persistent!, to.Persistent);
            var innerWidth = Math.Max(20f, ghostWidth - 8f);
            var measured = g.MeasureString(preview, labelFontForSizing, (int)innerWidth);
            ghostHeight = Math.Max(20f, measured.Height + 8f);
        }
        else if (oldKind == "color")
        {
            ghostHeight = Math.Max(20f, Math.Min(liveBounds.Height, 40f));
        }
        else
        {
            ghostHeight = Math.Min(Math.Max(20f, liveBounds.Height), 28f);
        }
        var ghost = new RectangleF(
            ghostX,
            liveBounds.Bottom + 4f,
            ghostWidth,
            ghostHeight);

        using var ghostFill = new SolidBrush(Color.FromArgb(50, 230, 200, 0));
        using var ghostOutline = new Pen(Color.FromArgb(220, 230, 200, 0), 1.5f)
        {
            DashStyle = DashStyle.Dot,
        };

        if (oldKind == "color" && !string.IsNullOrEmpty(oldData?.ColorArgb))
        {
            var swatchColor = TryParseArgb(oldData.ColorArgb!) ?? Color.Gray;
            using var swatchFill = new SolidBrush(swatchColor);
            g.FillRectangle(swatchFill, ghost);
            g.DrawRectangle(ghostOutline, ghost.X, ghost.Y, ghost.Width, ghost.Height);

            var hsvaLabel = ColorHsvaLabel(swatchColor);
            using var font = new Font("Segoe UI", 8f);
            using var textBrush = new SolidBrush(Color.FromArgb(220, 110, 80, 0));
            var size = g.MeasureString(hsvaLabel, font);
            g.DrawString(hsvaLabel, font, textBrush,
                ghost.X + Math.Max(4f, (ghost.Width - size.Width) / 2f),
                ghost.Bottom + 2f);
            return;
        }

        g.FillRectangle(ghostFill, ghost);
        g.DrawRectangle(ghostOutline, ghost.X, ghost.Y, ghost.Width, ghost.Height);

        if (oldKind == "slider" && oldData?.Slider is { } sv)
            PaintSliderInside(g, ghost, sv, to.Persistent?.Slider);

        var label = OldStateLabel(from.Persistent, to.Persistent);
        if (!string.IsNullOrEmpty(label))
        {
            using var font = new Font("Segoe UI", 8f, FontStyle.Regular);
            using var textBrush = new SolidBrush(Color.FromArgb(220, 110, 80, 0));
            // Measure with the same inner width DrawString uses, so the
            // wrap count agrees and we don't underestimate height.
            var innerWidth = Math.Max(20f, ghost.Width - 8f);
            var measured = g.MeasureString(label, font, (int)innerWidth);
            // Multi-line labels (value list) render top-aligned so the
            // first line sits cleanly inside the rect. Single-line
            // labels stay bottom-aligned with the existing 2px padding
            // to preserve the slider visual layout.
            var multiline = label.Contains('\n');
            var y = multiline
                ? ghost.Y + 4f
                : ghost.Bottom - measured.Height - 2f;
            g.DrawString(label, font, textBrush,
                new RectangleF(ghost.X + 4f, y, innerWidth, measured.Height));
        }
    }

    private static void PaintSliderInside(
        Graphics g, RectangleF ghost, SliderValue oldSv, SliderValue? newSv)
    {
        // Range labels frame the track. If min/max also changed (not just
        // the value), the changed end renders in orange so the reader can
        // tell at a glance "the slider's range was different here", not
        // just the position on the track.
        using var rangeFont = new Font("Segoe UI", 7.5f);
        var minText = FormatSliderValue(oldSv.Min, oldSv.Decimals);
        var maxText = FormatSliderValue(oldSv.Max, oldSv.Decimals);
        var minSize = g.MeasureString(minText, rangeFont);
        var maxSize = g.MeasureString(maxText, rangeFont);

        var trackY = ghost.Top + ghost.Height * 0.30f;
        var trackLeft = ghost.Left + 4f + minSize.Width + 4f;
        var trackRight = ghost.Right - 4f - maxSize.Width - 4f;
        if (trackRight <= trackLeft) return;

        using var trackPen = new Pen(Color.FromArgb(180, 130, 100, 0), 1.5f);
        g.DrawLine(trackPen, trackLeft, trackY, trackRight, trackY);

        var range = (float)(oldSv.Max - oldSv.Min);
        var ratio = range > 0
            ? (float)((double)(oldSv.Value - oldSv.Min) / range)
            : 0.5f;
        ratio = Math.Clamp(ratio, 0f, 1f);
        var knobX = trackLeft + ratio * (trackRight - trackLeft);

        using var knobBrush = new SolidBrush(Color.FromArgb(230, 180, 140, 0));
        var knobR = 4.5f;
        g.FillEllipse(knobBrush, knobX - knobR, trackY - knobR, knobR * 2f, knobR * 2f);

        var defaultColor = Color.FromArgb(220, 130, 100, 0);
        var changedColor = Color.FromArgb(255, 230, 120, 20);
        var minChanged = newSv is not null && newSv.Min != oldSv.Min;
        var maxChanged = newSv is not null && newSv.Max != oldSv.Max;

        using var minBrush = new SolidBrush(minChanged ? changedColor : defaultColor);
        using var maxBrush = new SolidBrush(maxChanged ? changedColor : defaultColor);

        g.DrawString(minText, rangeFont, minBrush,
            ghost.Left + 4f, trackY - minSize.Height / 2f);
        g.DrawString(maxText, rangeFont, maxBrush,
            ghost.Right - 4f - maxSize.Width, trackY - maxSize.Height / 2f);
    }

    private static string FormatSliderValue(decimal value, int decimals)
    {
        if (decimals < 0) decimals = 0;
        if (decimals > 10) decimals = 10;
        return value.ToString($"F{decimals}", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? OldStateLabel(PersistentData? from, PersistentData? to)
    {
        if (from is null) return "was: (none)";

        return from.Kind switch
        {
            "slider"    => from.Slider is { } sv
                ? $"was: {FormatSliderValue(sv.Value, sv.Decimals)}"
                : "was: (slider)",
            "panel"     => "was: " + Truncate(from.PanelText ?? "", 24),
            "boolean"   => $"was: {(from.BooleanState == true ? "True" : "False")}",
            "valuelist" => SummarizeValueListLabel(from, to),
            "color"     => $"was: #{from.ColorArgb}",
            "data"      => "was: data",
            _           => null,
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    /// <summary>
    /// Value-list overlay label: multi-line with the OLD selection on
    /// the first line, item adds/removes on the second (when v4+ data
    /// is available), and a mode change on the third (v5+). Each
    /// section is omitted if it has nothing to report. Same-name
    /// expression edits are not surfaced - GH normalizes Expression
    /// between sessions, so flagging them would produce false positives.
    /// </summary>
    private static string SummarizeValueListLabel(PersistentData from, PersistentData? to)
    {
        var lines = new List<string>
        {
            "was: " + Truncate(string.Join(", ", from.ValueListSelected ?? Array.Empty<string>()), 24),
        };

        if (from.ValueListItems is not null && to?.ValueListItems is not null)
        {
            var oldNames = from.ValueListItems.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
            var newNames = to.ValueListItems.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);

            var added = newNames.Except(oldNames, StringComparer.Ordinal).Count();
            var removed = oldNames.Except(newNames, StringComparer.Ordinal).Count();

            var bits = new List<string>();
            if (added > 0) bits.Add($"+{added}");
            if (removed > 0) bits.Add($"-{removed}");

            if (bits.Count > 0) lines.Add("items: " + string.Join(" ", bits));
        }

        if (from.ValueListMode is not null && to?.ValueListMode is not null
            && from.ValueListMode != to.ValueListMode)
        {
            lines.Add($"mode: {from.ValueListMode} → {to.ValueListMode}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// HSVA readout for the swatch ghost. Hue in degrees; SVA as
    /// percentages (matches GH's own swatch picker convention more
    /// closely than raw 0..1 floats and reads at a glance).
    /// </summary>
    private static string ColorHsvaLabel(Color c)
    {
        var h = c.GetHue();
        var s = c.GetSaturation() * 100f;
        var v = c.GetBrightness() * 100f;
        var a = c.A / 255f * 100f;
        return $"was: HSVA  {h:0}°  {s:0}%  {v:0}%  {a:0}%";
    }

    private static Color? TryParseArgb(string hex)
    {
        try
        {
            var argb = unchecked((int)Convert.ToUInt32(hex, 16));
            return Color.FromArgb(argb);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deletion viz: render the deleted component's last-known state as a
    /// red-themed ghost at its captured bounds. Each persistent kind
    /// (slider, panel, boolean toggle, value list, color swatch) draws
    /// its own visualization inside the rect so the reader sees what was
    /// there - what value the slider held, what the panel said, what
    /// color the swatch was - rather than a generic crossed-out box.
    /// Generic components (no persistent state) just get the bounds rect.
    /// Pre-v3 commits without captured bounds fall back to a default rect.
    /// </summary>
    private static void PaintDeletedGhost(Graphics g, CanonicalObject deleted)
    {
        RectangleF rect;
        if (deleted.Bounds is { } b && b.Width > 0 && b.Height > 0)
        {
            rect = new RectangleF(b.X, b.Y, b.Width, b.Height);
        }
        else
        {
            rect = new RectangleF(deleted.Pivot.X, deleted.Pivot.Y, 100f, 60f);
        }

        using var fill = new SolidBrush(Color.FromArgb(40, 220, 40, 40));
        using var outline = new Pen(RemovedColor, 2.5f);
        g.FillRectangle(fill, rect);
        g.DrawRectangle(outline, rect.X, rect.Y, rect.Width, rect.Height);

        switch (deleted.Persistent?.Kind)
        {
            case "slider":    PaintDeletedSlider(g, rect, deleted.Persistent.Slider);          break;
            case "panel":     PaintDeletedTextBody(g, rect, deleted.Persistent.PanelText);     break;
            case "boolean":   PaintDeletedBoolean(g, rect, deleted.Persistent.BooleanState);   break;
            case "valuelist": PaintDeletedValueList(g, rect, deleted.Persistent.ValueListSelected); break;
            case "color":     PaintDeletedSwatch(g, rect, deleted.Persistent.ColorArgb);       break;
        }

        var name = string.IsNullOrEmpty(deleted.Nickname) ? deleted.Name : deleted.Nickname;
        if (!string.IsNullOrEmpty(name))
        {
            using var font = new Font("Segoe UI", 9f, FontStyle.Bold);
            using var textBrush = new SolidBrush(Color.FromArgb(255, 200, 30, 30));
            var size = g.MeasureString(name, font);
            g.DrawString(name, font, textBrush,
                rect.X + (rect.Width - size.Width) / 2f,
                rect.Bottom + 2f);
        }
    }

    private static void PaintDeletedSlider(Graphics g, RectangleF rect, SliderValue? sv)
    {
        if (sv is null) return;

        using var rangeFont = new Font("Segoe UI", 7.5f);
        var minText = FormatSliderValue(sv.Min, sv.Decimals);
        var maxText = FormatSliderValue(sv.Max, sv.Decimals);
        var minSize = g.MeasureString(minText, rangeFont);
        var maxSize = g.MeasureString(maxText, rangeFont);

        var trackY = rect.Top + rect.Height * 0.62f;
        var trackLeft = rect.Left + 4f + minSize.Width + 4f;
        var trackRight = rect.Right - 4f - maxSize.Width - 4f;
        if (trackRight <= trackLeft) return;

        using var trackPen = new Pen(Color.FromArgb(220, 180, 30, 30), 1.5f);
        g.DrawLine(trackPen, trackLeft, trackY, trackRight, trackY);

        var range = (float)(sv.Max - sv.Min);
        var ratio = range > 0
            ? (float)((double)(sv.Value - sv.Min) / range)
            : 0.5f;
        ratio = Math.Clamp(ratio, 0f, 1f);
        var knobX = trackLeft + ratio * (trackRight - trackLeft);

        using var knobBrush = new SolidBrush(Color.FromArgb(240, 220, 40, 40));
        var knobR = 4.5f;
        g.FillEllipse(knobBrush, knobX - knobR, trackY - knobR, knobR * 2f, knobR * 2f);

        using var rangeBrush = new SolidBrush(Color.FromArgb(220, 180, 30, 30));
        g.DrawString(minText, rangeFont, rangeBrush, rect.Left + 4f, trackY - minSize.Height / 2f);
        g.DrawString(maxText, rangeFont, rangeBrush, rect.Right - 4f - maxSize.Width, trackY - maxSize.Height / 2f);

        // Lift the value text out of the rect so it doesn't compete
        // with the track line for short slider bounds (where 'top + 2'
        // pushes the glyphs into the track at 62% height). Mirrors the
        // pattern of placing the component name below the rect.
        var valueText = FormatSliderValue(sv.Value, sv.Decimals);
        using var valueFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var valueBrush = new SolidBrush(Color.FromArgb(255, 180, 20, 20));
        var valSize = g.MeasureString(valueText, valueFont);
        g.DrawString(valueText, valueFont, valueBrush,
            rect.X + (rect.Width - valSize.Width) / 2f,
            rect.Top - valSize.Height - 2f);
    }

    private static void PaintDeletedTextBody(Graphics g, RectangleF rect, string? text)
    {
        var body = string.IsNullOrEmpty(text) ? "(empty)" : text;
        using var font = new Font("Segoe UI", 8.5f);
        using var brush = new SolidBrush(Color.FromArgb(255, 180, 20, 20));
        var bodyRect = new RectangleF(rect.X + 4f, rect.Y + 4f, rect.Width - 8f, rect.Height - 8f);
        g.DrawString(body, font, brush, bodyRect);
    }

    private static void PaintDeletedBoolean(Graphics g, RectangleF rect, bool? state)
    {
        var label = state == true ? "True" : "False";
        using var font = new Font("Segoe UI", 11f, FontStyle.Bold);
        using var brush = new SolidBrush(Color.FromArgb(255, 200, 20, 20));
        var size = g.MeasureString(label, font);
        g.DrawString(label, font, brush,
            rect.X + (rect.Width - size.Width) / 2f,
            rect.Y + (rect.Height - size.Height) / 2f);
    }

    private static void PaintDeletedValueList(Graphics g, RectangleF rect, IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0) return;
        var text = string.Join(", ", items);
        PaintDeletedTextBody(g, rect, text);
    }

    private static void PaintDeletedSwatch(Graphics g, RectangleF rect, string? colorArgb)
    {
        if (string.IsNullOrEmpty(colorArgb)) return;
        var color = TryParseArgb(colorArgb!) ?? Color.Gray;
        var inner = new RectangleF(rect.X + 6f, rect.Y + 6f, rect.Width - 12f, rect.Height - 12f);
        using var swatchFill = new SolidBrush(Color.FromArgb(220, color.R, color.G, color.B));
        g.FillRectangle(swatchFill, inner);
        using var innerOutline = new Pen(Color.FromArgb(220, 180, 20, 20), 1.5f);
        g.DrawRectangle(innerOutline, inner.X, inner.Y, inner.Width, inner.Height);
    }
}
