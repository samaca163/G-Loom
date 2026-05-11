using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using GH_IO.Serialization;
using GLoom.Serialization;
using GLoom.Vcs;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Rhino;
using Bounds = GLoom.Serialization.Bounds;
using WinFormsMouseEventArgs = System.Windows.Forms.MouseEventArgs;
using WinFormsMouseButtons = System.Windows.Forms.MouseButtons;
using WinFormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;

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

    public bool Enabled { get; private set; } = true;

    private bool _showAdded = true;
    private bool _showModified = true;
    private bool _showMoved = true;
    private bool _showDeleted = true;
    private bool _hoverDetailsOnly;
    private string _comparisonReference = "HEAD";

    public bool ShowAdded { get => _showAdded; set => SetAndRefresh(ref _showAdded, value); }
    public bool ShowModified { get => _showModified; set => SetAndRefresh(ref _showModified, value); }
    public bool ShowMoved { get => _showMoved; set => SetAndRefresh(ref _showMoved, value); }
    public bool ShowDeleted { get => _showDeleted; set => SetAndRefresh(ref _showDeleted, value); }
    public bool HoverDetailsOnly { get => _hoverDetailsOnly; set => SetAndRefresh(ref _hoverDetailsOnly, value); }

    /// <summary>
    /// Which commit the overlay compares the live state against. "HEAD"
    /// (default) means "what have I changed since the last commit"; any
    /// other SHA / ref means "what's changed since this version" -
    /// useful for visualising the cumulative delta from a tag or older
    /// commit, not just the most recent one.
    /// </summary>
    public string ComparisonReference => _comparisonReference;

    public void SetComparisonReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) reference = "HEAD";
        if (_comparisonReference == reference) return;
        _comparisonReference = reference;
        _lastComputed = DateTime.MinValue;
        _cachedDiff = null;
        ComparisonReferenceChanged?.Invoke(this, EventArgs.Empty);
        Instances.ActiveCanvas?.Refresh();
    }

    public event EventHandler? EnabledChanged;
    public event EventHandler? SettingsChanged;
    public event EventHandler? ComparisonReferenceChanged;

    private DocumentDiff? _cachedDiff;
    private CanonicalDocument? _cachedFromDoc;
    private HashSet<string> _hoverableIds = new(StringComparer.Ordinal);
    private string? _hoveredId;
    private DateTime _lastComputed = DateTime.MinValue;
    private bool _initialized;
    private string? _lastTrackedFilePath;
    private string? _lastTrackedRepoPath;

    // Painted ghost regions, refreshed every Paint cycle. Right-click
    // hit-test consults this to decide whether to surface the restore
    // menu on any given canvas position.
    private readonly List<GhostRegion> _ghostRegions = new();

    private enum GhostRestoreKind { Pivot, Persistent, Deleted }

    private sealed record GhostRegion(
        RectangleF Rect,
        GhostRestoreKind Kind,
        ObjectChange? Change,
        CanonicalObject? Deleted);

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

        // Any tracker state change invalidates the diff cache (the live
        // doc may have been edited). The comparison reference, however,
        // only snaps back to HEAD when the FILE or REPO actually
        // changed - so an in-place edit (e.g. restoring a component)
        // doesn't silently drop the user's picked reference.
        DocumentTracker.Instance.StateChanged += (_, _) =>
        {
            var state = DocumentTracker.Instance.State;
            var fileOrRepoChanged = state.FilePath != _lastTrackedFilePath
                                 || state.RepoPath != _lastTrackedRepoPath;
            _lastTrackedFilePath = state.FilePath;
            _lastTrackedRepoPath = state.RepoPath;

            _lastComputed = DateTime.MinValue;
            _cachedDiff = null;

            if (fileOrRepoChanged && _comparisonReference != "HEAD")
            {
                _comparisonReference = "HEAD";
                ComparisonReferenceChanged?.Invoke(this, EventArgs.Empty);
            }

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

    private readonly Dictionary<GH_Canvas, GhostRightClickHook> _rightClickHooks = new();

    private void HookCanvas(GH_Canvas canvas)
    {
        if (canvas is null) return;
        canvas.CanvasPostPaintObjects += OnPostPaintObjects;
        canvas.MouseMove += OnCanvasMouseMove;
        canvas.MouseLeave += OnCanvasMouseLeave;

        // Win32-level right-click intercept. Subscribing to MouseDown
        // doesn't help - GH_Canvas processes WM_RBUTTONDOWN internally
        // and shows its own context menu regardless of subscribers.
        // A NativeWindow attached to the canvas's HWND sees the message
        // first; if our hit-test matches a ghost we handle the click
        // ourselves and don't forward, suppressing GH's menu cleanly.
        if (!_rightClickHooks.ContainsKey(canvas))
            _rightClickHooks[canvas] = new GhostRightClickHook(this, canvas);
    }

    /// <summary>
    /// Called by the WM_RBUTTONDOWN interceptor. Returns true if the
    /// click landed on a ghost (we handled it; GH's menu should not
    /// show), false if no ghost was under the cursor (let GH proceed
    /// with its normal right-click handling).
    /// </summary>
    private bool TryHandleRightClickAt(GH_Canvas canvas, Point clientPt)
    {
        if (!Enabled || _ghostRegions.Count == 0) return false;

        PointF worldPt;
        try { worldPt = canvas.Viewport.UnprojectPoint(clientPt); }
        catch { return false; }

        // Topmost-painted region wins (later registrations sit on top).
        for (var i = _ghostRegions.Count - 1; i >= 0; i--)
        {
            var region = _ghostRegions[i];
            if (region.Rect.Contains(worldPt))
            {
                ShowRestoreMenu(canvas, clientPt, region);
                return true;
            }
        }
        return false;
    }

    private sealed class GhostRightClickHook : System.Windows.Forms.NativeWindow
    {
        private const int WM_RBUTTONDOWN = 0x0204;

        private readonly CanvasDiffOverlay _overlay;
        private readonly GH_Canvas _canvas;

        public GhostRightClickHook(CanvasDiffOverlay overlay, GH_Canvas canvas)
        {
            _overlay = overlay;
            _canvas = canvas;
            if (canvas.IsHandleCreated) AssignHandle(canvas.Handle);
            canvas.HandleCreated += OnHandleCreated;
            canvas.HandleDestroyed += OnHandleDestroyed;
        }

        private void OnHandleCreated(object? sender, EventArgs e) => AssignHandle(_canvas.Handle);
        private void OnHandleDestroyed(object? sender, EventArgs e) => ReleaseHandle();

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_RBUTTONDOWN)
            {
                // LParam packs client X (low word) and Y (high word).
                var lParam = m.LParam.ToInt32();
                var x = (short)(lParam & 0xFFFF);
                var y = (short)((lParam >> 16) & 0xFFFF);
                if (_overlay.TryHandleRightClickAt(_canvas, new Point(x, y)))
                    return;
            }
            base.WndProc(ref m);
        }
    }

    private void ShowRestoreMenu(GH_Canvas canvas, Point screenLoc, GhostRegion region)
    {
        var name = RegionDisplayName(region);
        var refLabel = _comparisonReference == "HEAD"
            ? "HEAD"
            : (_comparisonReference.Length >= 7 ? _comparisonReference[..7] : _comparisonReference);
        var actionLabel = region.Kind switch
        {
            GhostRestoreKind.Pivot      => $"Restore {name}'s position to {refLabel}",
            GhostRestoreKind.Persistent => $"Restore {name}'s value to {refLabel}",
            GhostRestoreKind.Deleted    => $"Restore deleted {name} ({refLabel})",
            _ => $"Restore {name} to {refLabel}",
        };

        var menu = new WinFormsContextMenuStrip();
        menu.Items.Add(actionLabel, null, (_, _) => DoRestore(region));
        menu.Show(canvas, screenLoc);
    }

    private static string RegionDisplayName(GhostRegion region)
    {
        var obj = region.Deleted ?? region.Change?.To;
        if (obj is null) return "component";
        var n = string.IsNullOrEmpty(obj.Nickname) ? obj.Name : obj.Nickname;
        return string.IsNullOrEmpty(n) ? "component" : n;
    }

    private void DoRestore(GhostRegion region)
    {
        var doc = Instances.ActiveCanvas?.Document;
        if (doc is null) return;

        try
        {
            doc.UndoUtil.RecordEvent("G-Loom: restore from overlay");
            switch (region.Kind)
            {
                case GhostRestoreKind.Pivot when region.Change is { } c:
                    RestorePivot(doc, c);
                    break;
                case GhostRestoreKind.Persistent when region.Change is { } c:
                    RestorePersistent(doc, c);
                    break;
                case GhostRestoreKind.Deleted when region.Deleted is { } d:
                    RestoreDeleted(doc, d);
                    break;
            }
            doc.NewSolution(false);
            _lastComputed = DateTime.MinValue;
            Instances.ActiveCanvas?.Refresh();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Restore failed: {ex.Message}");
        }
    }

    private static void RestorePivot(GH_Document doc, ObjectChange change)
    {
        if (!Guid.TryParse(change.To.InstanceGuid, out var id)) return;
        var live = doc.Objects.FirstOrDefault(o => o.InstanceGuid == id);
        if (live?.Attributes is null) return;

        live.Attributes.Pivot = new PointF(change.From.Pivot.X, change.From.Pivot.Y);
        live.Attributes.ExpireLayout();
        live.ExpireSolution(false);
    }

    private static void RestorePersistent(GH_Document doc, ObjectChange change)
    {
        if (!Guid.TryParse(change.To.InstanceGuid, out var id)) return;
        var live = doc.Objects.FirstOrDefault(o => o.InstanceGuid == id);
        if (live is null) return;

        ApplyPersistent(live, change.From.Persistent);
        live.ExpireSolution(false);
    }

    /// <summary>
    /// Recreate a deleted component from its captured CanonicalObject.
    /// Best-effort: we set the type, instance GUID, pivot, persistent
    /// state, and reconnect input wires to source params that still
    /// exist on the live canvas. If a source was also deleted (cascade),
    /// that wire is silently dropped - the user can fix it manually or
    /// restore the source first.
    /// </summary>
    private static void RestoreDeleted(GH_Document doc, CanonicalObject deleted)
    {
        if (!Guid.TryParse(deleted.ComponentGuid, out var typeGuid)) return;
        if (!Guid.TryParse(deleted.InstanceGuid, out var instanceGuid)) return;

        var newObj = Instances.ComponentServer.EmitObject(typeGuid);
        if (newObj is null)
        {
            RhinoApp.WriteLine($"[G-Loom] Restore failed: component type {typeGuid} not registered.");
            return;
        }

        newObj.NewInstanceGuid(instanceGuid);
        doc.AddObject(newObj, false);

        if (newObj.Attributes is not null)
        {
            newObj.Attributes.Pivot = new PointF(deleted.Pivot.X, deleted.Pivot.Y);
            newObj.Attributes.ExpireLayout();
        }

        // Restore the original input + output param GUIDs. GH assigns
        // fresh ones on EmitObject; without restoration, downstream
        // consumers' from-doc source GUIDs wouldn't match this new
        // component's outputs, breaking missing-wire arrows AND any
        // future "compare to old commit" diff that references those
        // param GUIDs as wire sources.
        if (newObj is IGH_Component component)
        {
            for (var i = 0; i < deleted.Inputs.Count && i < component.Params.Input.Count; i++)
                if (Guid.TryParse(deleted.Inputs[i].InstanceGuid, out var g))
                    component.Params.Input[i].NewInstanceGuid(g);
            for (var i = 0; i < deleted.Outputs.Count && i < component.Params.Output.Count; i++)
                if (Guid.TryParse(deleted.Outputs[i].InstanceGuid, out var g))
                    component.Params.Output[i].NewInstanceGuid(g);
        }

        ApplyPersistent(newObj, deleted.Persistent);

        // Wire reconnection: deleted.Inputs[i].Sources contains the
        // upstream params' InstanceGuids at deletion time.
        if (newObj is IGH_Component component2)
        {
            for (var i = 0; i < deleted.Inputs.Count && i < component2.Params.Input.Count; i++)
                ReconnectSources(doc, component2.Params.Input[i], deleted.Inputs[i].Sources);
        }
        else if (newObj is IGH_Param freeParam && deleted.Inputs.Count > 0)
        {
            ReconnectSources(doc, freeParam, deleted.Inputs[0].Sources);
        }

        newObj.ExpireSolution(false);
    }

    private static void ReconnectSources(GH_Document doc, IGH_Param input, IReadOnlyList<string> sourceGuids)
    {
        foreach (var srcGuidStr in sourceGuids)
        {
            if (!Guid.TryParse(srcGuidStr, out var srcGuid)) continue;
            if (doc.Objects.FirstOrDefault(o => o.InstanceGuid == srcGuid) is IGH_Param srcParam)
                input.AddSource(srcParam);
        }
    }

    /// <summary>
    /// Apply a captured PersistentData onto a live IGH_DocumentObject.
    /// Used by both modified-restore (apply OLD value to existing live
    /// object) and deletion-restore (apply OLD value to freshly recreated
    /// object). Unsupported kinds and missing fields no-op gracefully.
    /// </summary>
    private static void ApplyPersistent(IGH_DocumentObject obj, PersistentData? from)
    {
        if (from is null) return;

        switch (from.Kind)
        {
            case "slider" when obj is GH_NumberSlider slider && from.Slider is { } sv:
                slider.Slider.Minimum = sv.Min;
                slider.Slider.Maximum = sv.Max;
                slider.Slider.DecimalPlaces = sv.Decimals;
                slider.SetSliderValue(sv.Value);
                break;

            case "panel" when obj is GH_Panel panel:
                panel.UserText = from.PanelText ?? string.Empty;
                break;

            case "boolean" when obj is GH_BooleanToggle toggle && from.BooleanState is { } b:
                toggle.Value = b;
                break;

            case "color" when obj is GH_ColourSwatch swatch && !string.IsNullOrEmpty(from.ColorArgb):
                try
                {
                    var argb = unchecked((int)Convert.ToUInt32(from.ColorArgb, 16));
                    swatch.SwatchColour = Color.FromArgb(argb);
                }
                catch { /* malformed hex - leave as-is */ }
                break;
        }
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

    private DocumentDiff? ComputeLiveDiff()
    {
        var state = DocumentTracker.Instance.State;
        if (state.Document is null
            || state.RepoPath is null
            || state.FilePath is null
            || state.CanonicalJsonFullPath is null)
        {
            _cachedFromDoc = null;
            return null;
        }

        var jsonRel = Path.GetRelativePath(state.RepoPath, state.CanonicalJsonFullPath);
        var historicJson = GLoomRepository.ReadFileAtCommit(state.RepoPath, _comparisonReference, jsonRel);
        var headDoc = CanonicalJson.TryParse(historicJson);
        if (headDoc is null) { _cachedFromDoc = null; return null; }

        CanonicalDocument liveDoc;
        try
        {
            liveDoc = DocumentSerializer.Serialize(state.Document);
        }
        catch
        {
            _cachedFromDoc = null;
            return null;
        }

        // Stash the from-doc so paint can find downstream consumers of
        // deleted components and visualize the missing output wires.
        _cachedFromDoc = headDoc;
        return DocumentDiff.Compute(headDoc, liveDoc);
    }

    private static void Paint(GH_Canvas canvas, DocumentDiff diff, CanvasDiffOverlay s, string? hoveredId)
    {
        var graphics = canvas.Graphics;
        var doc = canvas.Document;
        if (graphics is null || doc is null) return;

        // Reset the right-click hit-test list every paint cycle. Each
        // helper that paints a "ghost" surface registers its rect so
        // OnCanvasMouseDown can find it on right-click.
        s._ghostRegions.Clear();

        // Live objects keyed by the same string form CanonicalObject uses
        // (Guid.ToString("D")), so the lookup matches the diff entries.
        var liveById = doc.Objects.ToDictionary(o => o.InstanceGuid.ToString("D"));

        var deletionRects = new Dictionary<string, RectangleF>(StringComparer.Ordinal);
        if (s.ShowDeleted)
        {
            foreach (var removed in diff.ObjectsRemoved)
            {
                var rect = PaintDeletedGhost(graphics, removed);
                if (!rect.IsEmpty)
                {
                    deletionRects[removed.InstanceGuid] = rect;
                    s._ghostRegions.Add(new GhostRegion(rect, GhostRestoreKind.Deleted, null, removed));
                }
            }

        }

        // Wire deltas: red dashed arrows for missing wires (existed in
        // from but not in live) + green beziers for added wires (in
        // live but not in from). Both follow the same per-port grip
        // anchors so they read like proper GH wires - bezier curves
        // landing on the actual input/output ports rather than just
        // bounding-rect edges.
        if (s._cachedFromDoc is not null)
        {
            PaintMissingWires(graphics, diff, doc, liveById, deletionRects);
            PaintAddedWires(graphics, diff, doc, liveById);
        }

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
            {
                var rect = PaintMovementTrail(graphics, live, change.From);
                if (!rect.IsEmpty)
                    s._ghostRegions.Add(new GhostRegion(rect, GhostRestoreKind.Pivot, change, null));
            }

            if ((change.Kinds & ObjectChangeKind.PersistentChanged) != 0
                && s.ShowModified
                && showExtras
                && change.From.Persistent?.Kind != "panel"
                && change.To.Persistent?.Kind != "panel")
            {
                var rect = PaintPersistentGhost(graphics, live, change.From, change.To);
                if (!rect.IsEmpty)
                    s._ghostRegions.Add(new GhostRegion(rect, GhostRestoreKind.Persistent, change, null));
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
    /// arrow from the old center to the new center. Returns the OLD
    /// bounds rect so the right-click hit-test knows where to look for
    /// "restore position" requests.
    /// </summary>
    private static RectangleF PaintMovementTrail(Graphics g, IGH_DocumentObject live, CanonicalObject from)
    {
        var liveBounds = live.Attributes?.Bounds ?? RectangleF.Empty;
        var livePivot = live.Attributes?.Pivot ?? PointF.Empty;
        if (liveBounds.IsEmpty) return RectangleF.Empty;

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
        if (manhattan >= 8f)
        {
            using var arrowPen = new Pen(MovedColor, 2.5f)
            {
                CustomEndCap = new AdjustableArrowCap(5f, 6f, true),
            };
            g.DrawLine(arrowPen, oldCenter, newCenter);
        }

        return oldBounds;
    }

    /// <summary>
    /// Persistent-change viz: a "ghost" panel rendered just below the live
    /// component showing the OLD persistent state. Slider gets a phantom
    /// track + knob; color swatch gets a fill of the old color; the rest
    /// fall back to a labeled rect ("was: ...").
    /// </summary>
    private static RectangleF PaintPersistentGhost(
        Graphics g, IGH_DocumentObject live, CanonicalObject from, CanonicalObject to)
    {
        var liveBounds = live.Attributes?.Bounds ?? RectangleF.Empty;
        if (liveBounds.IsEmpty) return RectangleF.Empty;

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
            return ghost;
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

        return ghost;
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
    /// the first line, item adds/removes/expression-edits on the second
    /// (when v4+ data is available), and a mode change on the third
    /// (v5+). Each section is omitted if it has nothing to report.
    /// Expression comparison uses ValueListItem.Canonicalize so GH's
    /// session-to-session normalization doesn't trigger a false
    /// "expression changed" entry.
    /// </summary>
    private static string SummarizeValueListLabel(PersistentData from, PersistentData? to)
    {
        var lines = new List<string>
        {
            "was: " + Truncate(string.Join(", ", from.ValueListSelected ?? Array.Empty<string>()), 24),
        };

        if (from.ValueListItems is not null && to?.ValueListItems is not null)
        {
            var oldByName = from.ValueListItems.ToDictionary(i => i.Name, StringComparer.Ordinal);
            var newByName = to.ValueListItems.ToDictionary(i => i.Name, StringComparer.Ordinal);

            var added = newByName.Keys.Except(oldByName.Keys, StringComparer.Ordinal).Count();
            var removed = oldByName.Keys.Except(newByName.Keys, StringComparer.Ordinal).Count();
            var changed = oldByName.Count(kv =>
                newByName.TryGetValue(kv.Key, out var n)
                && !string.Equals(
                    ValueListItem.Canonicalize(n.Expression),
                    ValueListItem.Canonicalize(kv.Value.Expression),
                    StringComparison.Ordinal));

            var bits = new List<string>();
            if (added > 0) bits.Add($"+{added}");
            if (removed > 0) bits.Add($"-{removed}");
            if (changed > 0) bits.Add($"~{changed}");

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
    /// <summary>
    /// Red dashed arrows for any wire that existed in the from-doc but
    /// doesn't in the live state. Two sources of "missing wires":
    ///  - A live consumer whose input lost a source (modified, kind
    ///    includes WiresChanged). Common cases: a manually-disconnected
    ///    wire, a deleted source, OR the post-restore case where we
    ///    recreated a deleted component but didn't reconnect its
    ///    downstream consumers.
    ///  - A deleted consumer (in ObjectsRemoved) — every input source
    ///    is by definition a missing wire since the consumer is gone.
    ///
    /// Source position resolves through two anchor maps: live
    /// (component output param GUIDs and free-floating param GUIDs map
    /// to right-edge of the owning visual) and ghost (deletion rects of
    /// removed components). If neither lookup succeeds (cascade-deleted
    /// source AND cascade-deleted consumer with no visible anchors),
    /// the wire is silently dropped.
    /// </summary>
    private static void PaintMissingWires(
        Graphics g,
        DocumentDiff diff,
        GH_Document doc,
        Dictionary<string, IGH_DocumentObject> liveById,
        Dictionary<string, RectangleF> deletionRects)
    {
        // Source-anchor lookups keyed by output PARAM GUID (the GUID
        // that appears in downstream input.Sources). Live anchors come
        // from per-port OutputGrip; ghost anchors are distributed
        // along the deletion rect's right edge per output index.
        var liveOutputAnchors = BuildLiveOutputAnchors(doc);

        var ghostOutputAnchors = new Dictionary<string, PointF>(StringComparer.Ordinal);
        foreach (var removed in diff.ObjectsRemoved)
        {
            if (!deletionRects.TryGetValue(removed.InstanceGuid, out var rect)) continue;

            if (removed.Outputs.Count > 0)
            {
                // Distribute output anchors along the right edge so a
                // multi-output deleted component shows distinct arrow
                // starting points per output instead of all collapsing
                // to the same right-middle point.
                var n = removed.Outputs.Count;
                for (var i = 0; i < n; i++)
                    ghostOutputAnchors[removed.Outputs[i].InstanceGuid] = GhostPortAnchor(rect, leftEdge: false, i, n);
            }
            else
            {
                ghostOutputAnchors[removed.InstanceGuid] = GhostPortAnchor(rect, leftEdge: false, 0, 1);
            }
        }

        using var pen = new Pen(Color.FromArgb(220, 220, 40, 40), 1.8f)
        {
            DashStyle = DashStyle.Dash,
            CustomEndCap = new AdjustableArrowCap(5f, 6f, true),
        };

        // Pass 1: live consumers with WiresChanged. The consumer anchor
        // is the SPECIFIC input port's InputGrip (X / Y / Z on a Pt
        // construct, etc.) - not just the consumer's left edge - so
        // the arrow lands on the actual port the wire was attached to.
        // The arrow persists until the input has SOMETHING wired into
        // it - any source counts, not necessarily the originally-
        // intended one. So if the user reconnects to a different
        // source the arrow goes away just the same.
        foreach (var change in diff.ObjectsModified)
        {
            if ((change.Kinds & ObjectChangeKind.WiresChanged) == 0) continue;
            if (!liveById.TryGetValue(change.To.InstanceGuid, out var consumer)) continue;

            var consumerInputs = LiveInputsOf(consumer);
            for (var i = 0; i < change.From.Inputs.Count && i < change.To.Inputs.Count; i++)
            {
                // Anything plugged into this input clears the arrow.
                if (change.To.Inputs[i].Sources.Count > 0) continue;

                foreach (var src in change.From.Inputs[i].Sources)
                {
                    if (!TryFindWireAnchor(src, liveOutputAnchors, ghostOutputAnchors, out var srcAnchor)) continue;
                    var consumerAnchor = LiveInputAnchor(consumer, consumerInputs, i);
                    DrawWireBezier(g, pen, srcAnchor, consumerAnchor);
                }
            }
        }

        // Pass 2: deleted consumers - every input source is missing.
        // Per-input-index anchors along the left edge so multi-input
        // deleted components show distinct arrow endpoints per input
        // instead of collapsing to a single left-middle point.
        foreach (var removed in diff.ObjectsRemoved)
        {
            if (!deletionRects.TryGetValue(removed.InstanceGuid, out var rect)) continue;
            var inputCount = removed.Inputs.Count;

            for (var i = 0; i < removed.Inputs.Count; i++)
            {
                var consumerAnchor = GhostPortAnchor(rect, leftEdge: true, i, inputCount);
                foreach (var src in removed.Inputs[i].Sources)
                {
                    if (TryFindWireAnchor(src, liveOutputAnchors, ghostOutputAnchors, out var srcAnchor))
                        DrawWireBezier(g, pen, srcAnchor, consumerAnchor);
                }
            }
        }
    }

    /// <summary>
    /// Green solid bezier for any wire that exists in live but didn't
    /// in the from-doc. Two sources of "added wires":
    ///  - A completely new component (in ObjectsAdded). Every input
    ///    source is new by definition.
    ///  - An existing component that gained a new source on one of
    ///    its inputs (in ObjectsModified with WiresChanged).
    /// Both kinds anchor on the SPECIFIC live ports (per-output
    /// OutputGrip and per-input InputGrip) so the bezier overlays the
    /// actual wire path GH already paints, reading as a green
    /// "this wire is new since reference" highlight.
    /// </summary>
    private static void PaintAddedWires(
        Graphics g,
        DocumentDiff diff,
        GH_Document doc,
        Dictionary<string, IGH_DocumentObject> liveById)
    {
        var liveOutputAnchors = BuildLiveOutputAnchors(doc);
        if (liveOutputAnchors.Count == 0) return;

        using var pen = new Pen(Color.FromArgb(220, 60, 200, 60), 2.5f);

        // Pass A: brand-new components - every input source is new.
        foreach (var added in diff.ObjectsAdded)
        {
            if (!liveById.TryGetValue(added.InstanceGuid, out var consumer)) continue;
            var consumerInputs = LiveInputsOf(consumer);

            for (var i = 0; i < added.Inputs.Count; i++)
            {
                foreach (var src in added.Inputs[i].Sources)
                {
                    if (!liveOutputAnchors.TryGetValue(src, out var srcAnchor)) continue;
                    var consumerAnchor = LiveInputAnchor(consumer, consumerInputs, i);
                    DrawWireBezier(g, pen, srcAnchor, consumerAnchor);
                }
            }
        }

        // Pass B: existing components with new sources on existing inputs.
        foreach (var change in diff.ObjectsModified)
        {
            if ((change.Kinds & ObjectChangeKind.WiresChanged) == 0) continue;
            if (!liveById.TryGetValue(change.To.InstanceGuid, out var consumer)) continue;

            var consumerInputs = LiveInputsOf(consumer);
            for (var i = 0; i < change.From.Inputs.Count && i < change.To.Inputs.Count; i++)
            {
                var fromSet = new HashSet<string>(change.From.Inputs[i].Sources, StringComparer.Ordinal);
                foreach (var src in change.To.Inputs[i].Sources)
                {
                    if (fromSet.Contains(src)) continue;
                    if (!liveOutputAnchors.TryGetValue(src, out var srcAnchor)) continue;
                    var consumerAnchor = LiveInputAnchor(consumer, consumerInputs, i);
                    DrawWireBezier(g, pen, srcAnchor, consumerAnchor);
                }
            }
        }
    }

    /// <summary>
    /// GH-style wire bezier: horizontal-leaning curve from source's
    /// right side to target's left side, with control-point bend
    /// proportional to horizontal distance (clamped to 40px so very
    /// close ports still render as a curve, not a straight segment).
    /// </summary>
    private static void DrawWireBezier(Graphics g, Pen pen, PointF source, PointF target)
    {
        var bend = Math.Max(40f, Math.Abs(target.X - source.X) * 0.5f);
        var c1 = new PointF(source.X + bend, source.Y);
        var c2 = new PointF(target.X - bend, target.Y);
        g.DrawBezier(pen, source, c1, c2, target);
    }

    private static Dictionary<string, PointF> BuildLiveOutputAnchors(GH_Document doc)
    {
        var anchors = new Dictionary<string, PointF>(StringComparer.Ordinal);
        foreach (var obj in doc.Objects)
        {
            if (obj is IGH_Component comp)
            {
                foreach (var output in comp.Params.Output)
                {
                    var grip = output.Attributes?.OutputGrip ?? PointF.Empty;
                    if (grip == PointF.Empty)
                    {
                        var b = comp.Attributes?.Bounds ?? RectangleF.Empty;
                        if (b.IsEmpty) continue;
                        grip = new PointF(b.Right, b.Y + b.Height / 2f);
                    }
                    anchors[output.InstanceGuid.ToString("D")] = grip;
                }
            }
            else if (obj is IGH_Param param)
            {
                var grip = param.Attributes?.OutputGrip ?? PointF.Empty;
                if (grip == PointF.Empty)
                {
                    var b = param.Attributes?.Bounds ?? RectangleF.Empty;
                    if (b.IsEmpty) continue;
                    grip = new PointF(b.Right, b.Y + b.Height / 2f);
                }
                anchors[param.InstanceGuid.ToString("D")] = grip;
            }
        }
        return anchors;
    }

    /// <summary>
    /// Evenly distributes port anchors along the LEFT or RIGHT edge of
    /// a ghost rect by index, mirroring how GH lays out input/output
    /// ports vertically. For a single port it returns the edge midpoint,
    /// matching the previous behavior.
    /// </summary>
    private static PointF GhostPortAnchor(RectangleF rect, bool leftEdge, int index, int totalCount)
    {
        var x = leftEdge ? rect.X : rect.Right;
        var y = totalCount > 0
            ? rect.Top + (index + 0.5f) * (rect.Height / totalCount)
            : rect.Y + rect.Height / 2f;
        return new PointF(x, y);
    }

    private static IList<IGH_Param>? LiveInputsOf(IGH_DocumentObject obj) => obj switch
    {
        IGH_Component comp => comp.Params.Input,
        _ => null,
    };

    private static PointF LiveInputAnchor(IGH_DocumentObject consumer, IList<IGH_Param>? inputs, int index)
    {
        // Component input: per-port InputGrip.
        if (inputs is not null && index >= 0 && index < inputs.Count)
        {
            var grip = inputs[index].Attributes?.InputGrip ?? PointF.Empty;
            if (grip != PointF.Empty) return grip;
        }
        // Free-floating param: its own InputGrip.
        if (consumer is IGH_Param freeParam && index == 0)
        {
            var grip = freeParam.Attributes?.InputGrip ?? PointF.Empty;
            if (grip != PointF.Empty) return grip;
        }
        // Fallback: left-middle of the consumer's bounds.
        var b = consumer.Attributes?.Bounds ?? RectangleF.Empty;
        if (b.IsEmpty) return PointF.Empty;
        return new PointF(b.X, b.Y + b.Height / 2f);
    }

    private static bool TryFindWireAnchor(
        string sourceGuid,
        Dictionary<string, PointF> liveOutputAnchors,
        Dictionary<string, PointF> ghostOutputAnchors,
        out PointF anchor)
    {
        if (liveOutputAnchors.TryGetValue(sourceGuid, out anchor)) return true;
        return ghostOutputAnchors.TryGetValue(sourceGuid, out anchor);
    }

    private static RectangleF PaintDeletedGhost(Graphics g, CanonicalObject deleted)
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

        return rect;
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
