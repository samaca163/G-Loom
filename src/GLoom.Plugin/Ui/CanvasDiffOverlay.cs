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
/// The paint handler is strictly read-only against the cached diff:
/// recompute is event-driven (document SolutionEnd / ObjectsAdded /
/// ObjectsDeleted / UndoStateChanged, tracker StateChanged, reference
/// changes), debounced to a 250ms floor, with the historic side parsed
/// once per resolved commit SHA and fetched off the UI thread. Pan/zoom
/// frames therefore never run git, JSON parsing or serialization -
/// that inline work was the canvas stutter the old paint-clock scheme
/// caused. A failed historic fetch latches off (no timed retry) until
/// the next state or reference change.
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

    // Minimum interval between recomputes: a slider-drag flurry of
    // SolutionEnd events coalesces into one trailing recompute per window.
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
        _historicDirty = true;
        _historicUnavailable = false;
        _liveDirty = true;
        SetDiff(null, null, null);
        ComparisonReferenceChanged?.Invoke(this, EventArgs.Empty);
        ScheduleRecompute();
    }

    public event EventHandler? EnabledChanged;
    public event EventHandler? SettingsChanged;
    public event EventHandler? ComparisonReferenceChanged;

    private DocumentDiff? _cachedDiff;
    private CanonicalDocument? _cachedFromDoc;
    private GH_Document? _diffDoc;
    private readonly List<(string Id, IGH_DocumentObject Obj)> _hoverTargets = new();
    private string? _hoveredId;
    private bool _initialized;
    private string? _lastTrackedFilePath;
    private string? _lastTrackedRepoPath;

    // Recompute pipeline state. All flags are UI-thread-only; the historic
    // cache is concurrent because the fetch stage populates it off-thread.
    private bool _liveDirty = true;
    private bool _historicDirty = true;
    private bool _historicUnavailable;
    private bool _fetchInFlight;
    private bool _recomputePending;
    private DateTime _lastRecomputeUtc = DateTime.MinValue;
    private HistoricSnapshot? _historic;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CanonicalDocument>
        _historicCache = new(StringComparer.Ordinal);

    private sealed record HistoricSnapshot(string RepoPath, string JsonRel, string Sha, CanonicalDocument Doc);

    private GH_Document? _hookedDoc;
    private GH_Document.SolutionEndEventHandler? _onSolutionEnd;
    private GH_Document.ObjectsAddedEventHandler? _onObjectsAdded;
    private GH_Document.ObjectsDeletedEventHandler? _onObjectsDeleted;
    private GH_Document.UndoStateChangedEventHandler? _onUndoStateChanged;

    // Painted ghost regions, refreshed every Paint cycle. Right-click
    // hit-test consults this to decide whether to surface the restore
    // menu on any given canvas position.
    private readonly List<GhostRegion> _ghostRegions = new();

    private enum GhostRestoreKind { Pivot, Persistent, Deleted }

    private readonly record struct GhostRegion(
        RectangleF Rect,
        GhostRestoreKind Kind,
        ObjectChange? Change,
        CanonicalObject? Deleted);

    // Rebuilt once per recompute, read every frame. The old code rebuilt a
    // Guid.ToString-keyed dictionary of ALL document objects per frame, and
    // walked every object's outputs TWICE per frame for wire anchors -
    // O(document) work per paint regardless of diff size. Object references
    // stay valid between recomputes; per-frame reads are just live
    // bounds/grip property lookups, so halos and wires still track drags.
    private readonly Dictionary<string, IGH_DocumentObject> _liveById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (IGH_Param Param, IGH_DocumentObject Owner)> _liveOutputSources
        = new(StringComparer.Ordinal);

    private void SetAndRefresh(ref bool field, bool value)
    {
        if (field == value) return;
        field = value;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        // Deferred Invalidate, not synchronous Refresh: filters apply at
        // paint time against the cached diff, so a toggle needs no
        // recompute and the next paint pass is soon enough.
        if (Enabled) Instances.ActiveCanvas?.Invalidate();
    }

    private CanvasDiffOverlay() { }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Instances.CanvasCreated += HookCanvas;
        Instances.CanvasDestroyed += UnhookCanvas;
        if (Instances.ActiveCanvas is { } ac) HookCanvas(ac);

        // Any tracker state change invalidates the diff cache (the live
        // doc may have been edited, HEAD may have moved). The comparison
        // reference, however, only snaps back to HEAD when the FILE or
        // REPO actually changed - so an in-place edit (e.g. restoring a
        // component) doesn't silently drop the user's picked reference.
        DocumentTracker.Instance.StateChanged += (_, _) =>
        {
            var state = DocumentTracker.Instance.State;
            var fileOrRepoChanged = state.FilePath != _lastTrackedFilePath
                                 || state.RepoPath != _lastTrackedRepoPath;
            _lastTrackedFilePath = state.FilePath;
            _lastTrackedRepoPath = state.RepoPath;

            RehookDocumentEvents(state.Document);

            _liveDirty = true;
            _historicDirty = true;
            _historicUnavailable = false;
            if (fileOrRepoChanged)
            {
                _historicCache.Clear();
                SetDiff(null, null, null);
            }

            if (fileOrRepoChanged && _comparisonReference != "HEAD")
            {
                _comparisonReference = "HEAD";
                ComparisonReferenceChanged?.Invoke(this, EventArgs.Empty);
            }

            if (Enabled)
            {
                Instances.ActiveCanvas?.Invalidate();
                ScheduleRecompute();
            }
        };

        RehookDocumentEvents(DocumentTracker.Instance.State.Document);
        ScheduleRecompute();
    }

    private void RehookDocumentEvents(GH_Document? doc)
    {
        if (ReferenceEquals(_hookedDoc, doc)) return;
        if (_hookedDoc is not null)
        {
            _hookedDoc.SolutionEnd -= _onSolutionEnd;
            _hookedDoc.ObjectsAdded -= _onObjectsAdded;
            _hookedDoc.ObjectsDeleted -= _onObjectsDeleted;
            _hookedDoc.UndoStateChanged -= _onUndoStateChanged;
        }
        _hookedDoc = doc;
        if (doc is null) return;

        // UndoStateChanged is the move/rename signal: drag-moves record an
        // undo event without solving, so SolutionEnd alone would miss them.
        _onSolutionEnd ??= (_, _) => MarkLiveDirty();
        _onObjectsAdded ??= (_, _) => MarkLiveDirty();
        _onObjectsDeleted ??= (_, _) => MarkLiveDirty();
        _onUndoStateChanged ??= (_, _) => MarkLiveDirty();
        doc.SolutionEnd += _onSolutionEnd;
        doc.ObjectsAdded += _onObjectsAdded;
        doc.ObjectsDeleted += _onObjectsDeleted;
        doc.UndoStateChanged += _onUndoStateChanged;
    }

    private void MarkLiveDirty()
    {
        _liveDirty = true;
        ScheduleRecompute();
    }

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        if (enabled)
        {
            _liveDirty = true;
            _historicDirty = true;
            _historicUnavailable = false;
            ScheduleRecompute();
        }
        EnabledChanged?.Invoke(this, EventArgs.Empty);
        Instances.ActiveCanvas?.Invalidate();
        RhinoApp.WriteLine($"[G-Loom] Diff overlay {(enabled ? "on" : "off")}.");
    }

    // Typed as object so the field declaration doesn't drag the
    // GhostRightClickHook type (NativeWindow-derived) into the JIT for
    // the constructor on macOS, where NativeWindow isn't part of
    // Rhino's WinForms shim and would fail to load.
    private readonly Dictionary<GH_Canvas, object> _rightClickHooks = new();
    private readonly HashSet<GH_Canvas> _hookedCanvases = new();

    private void HookCanvas(GH_Canvas canvas)
    {
        // Idempotency guard: Initialize() hooks the already-active canvas
        // AND subscribes CanvasCreated; without this a double subscription
        // would paint the whole overlay twice per frame.
        if (canvas is null || !_hookedCanvases.Add(canvas)) return;
        canvas.CanvasPostPaintObjects += OnPostPaintObjects;
        canvas.MouseMove += OnCanvasMouseMove;
        canvas.MouseLeave += OnCanvasMouseLeave;

        // Win32-level right-click intercept. Subscribing to MouseDown
        // doesn't help - GH_Canvas processes WM_RBUTTONDOWN internally
        // and shows its own context menu regardless of subscribers.
        // A NativeWindow attached to the canvas's HWND sees the message
        // first; if our hit-test matches a ghost we handle the click
        // ourselves and don't forward, suppressing GH's menu cleanly.
        // macOS Rhino's WinForms shim doesn't ship NativeWindow at all,
        // so on non-Windows we skip the hook (right-click restore
        // becomes unavailable there until we add a Cocoa-side path).
        if (OperatingSystem.IsWindows())
            InstallWindowsRightClickHook(canvas);
    }

    private void UnhookCanvas(GH_Canvas canvas)
    {
        if (canvas is null || !_hookedCanvases.Remove(canvas)) return;
        canvas.CanvasPostPaintObjects -= OnPostPaintObjects;
        canvas.MouseMove -= OnCanvasMouseMove;
        canvas.MouseLeave -= OnCanvasMouseLeave;

        if (OperatingSystem.IsWindows())
            RemoveWindowsRightClickHook(canvas);
    }

    private void InstallWindowsRightClickHook(GH_Canvas canvas)
    {
        if (!_rightClickHooks.ContainsKey(canvas))
            _rightClickHooks[canvas] = new GhostRightClickHook(this, canvas);
    }

    private void RemoveWindowsRightClickHook(GH_Canvas canvas)
    {
        if (_rightClickHooks.Remove(canvas, out var hook))
            ((GhostRightClickHook)hook).Detach();
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

        /// <summary>
        /// Fully detaches from the canvas so the overlay singleton doesn't
        /// pin destroyed canvases for the process lifetime.
        /// </summary>
        public void Detach()
        {
            _canvas.HandleCreated -= OnHandleCreated;
            _canvas.HandleDestroyed -= OnHandleDestroyed;
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }

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
            MarkLiveDirty();
            Instances.ActiveCanvas?.Invalidate();
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

        // Hover only affects rendering in hover-details mode (paint's
        // showExtras is unconditionally true otherwise), so in the default
        // always-show mode the old per-move hit test - which walked EVERY
        // document object allocating a Guid string each, then forced a
        // synchronous full-canvas repaint that changed no pixels - was
        // pure overhead.
        if (!HoverDetailsOnly || _hoverTargets.Count == 0)
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

        // Last match wins for overlapping items, as before.
        string? newHover = null;
        foreach (var (id, obj) in _hoverTargets)
        {
            var bounds = obj.Attributes?.Bounds ?? RectangleF.Empty;
            if (!bounds.IsEmpty && bounds.Contains(worldPt))
                newHover = id;
        }

        if (newHover != _hoveredId)
        {
            _hoveredId = newHover;
            canvas.Invalidate();
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
        if (Enabled) canvas.Invalidate();
    }

    private void OnPostPaintObjects(GH_Canvas canvas)
    {
        if (!Enabled) return;

        // Strictly read-only: the recompute pipeline runs off the paint
        // path entirely. The doc-identity guard prevents painting one
        // document's overlay onto another canvas between a tab switch
        // and the next recompute.
        var diff = _cachedDiff;
        if (diff is null || diff.IsEmpty) return;
        if (!ReferenceEquals(_diffDoc, canvas.Document)) return;

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
        _hoverTargets.Clear();
        if (_cachedDiff is null) return;
        foreach (var c in _cachedDiff.ObjectsModified)
        {
            // Anything with extras worth revealing on hover: a movement
            // trail, a persistent ghost-below, or a panel ghost-above.
            if ((c.Kinds & (ObjectChangeKind.Moved | ObjectChangeKind.PersistentChanged)) != 0
                && _liveById.TryGetValue(c.To.InstanceGuid, out var obj))
            {
                _hoverTargets.Add((c.To.InstanceGuid, obj));
            }
        }
    }

    // ---- recompute pipeline ------------------------------------------
    //
    // Stage A (background): resolve the comparison reference to a commit
    // SHA, then fetch + parse the historic .gloom.json - cached per SHA,
    // so steady-state editing never spawns git at all.
    // Stage B (UI thread): serialize the live document (GH objects are
    // UI-thread-only), diff, swap the cache, invalidate the canvas.
    // Scheduling is event-driven with a 250ms floor; there is no timer
    // and no polling - a clean, idle document costs nothing.

    private void ScheduleRecompute()
    {
        if (!Enabled || _recomputePending) return;
        _recomputePending = true;

        var wait = StaleAfter - (DateTime.UtcNow - _lastRecomputeUtc);
        if (wait <= TimeSpan.Zero)
        {
            if (!TryInvokeOnUi(RunRecompute)) _recomputePending = false;
        }
        else
        {
            System.Threading.Tasks.Task.Delay(wait).ContinueWith(_ =>
            {
                if (!TryInvokeOnUi(RunRecompute)) _recomputePending = false;
            });
        }
    }

    private static bool TryInvokeOnUi(Action action)
    {
        try
        {
            Eto.Forms.Application.Instance.AsyncInvoke(action);
            return true;
        }
        catch
        {
            // Eto not attached yet (very early load); the next dirty event
            // reschedules.
            return false;
        }
    }

    private void RunRecompute()
    {
        _recomputePending = false;
        if (!Enabled) return;

        var state = DocumentTracker.Instance.State;
        if (state.Document is null
            || state.RepoPath is null
            || state.FilePath is null
            || state.CanonicalJsonFullPath is null)
        {
            SetDiff(null, null, null);
            return;
        }

        var jsonRel = Path.GetRelativePath(state.RepoPath, state.CanonicalJsonFullPath);

        if (_historicDirty || _historic is null
            || _historic.RepoPath != state.RepoPath
            || _historic.JsonRel != jsonRel)
        {
            if (_historicUnavailable) return;
            if (_fetchInFlight) return;

            _fetchInFlight = true;
            var repo = state.RepoPath;
            var reference = _comparisonReference;
            System.Threading.Tasks.Task.Run(() => FetchHistoric(repo, jsonRel, reference));
            return;
        }

        if (!_liveDirty) return;

        CanonicalDocument liveDoc;
        try
        {
            liveDoc = DocumentSerializer.Serialize(state.Document);
        }
        catch (Exception ex)
        {
            _liveDirty = false;
            RhinoApp.WriteLine($"[G-Loom] Overlay serialize failed: {ex.Message}");
            return;
        }

        _liveDirty = false;
        _lastRecomputeUtc = DateTime.UtcNow;
        SetDiff(DocumentDiff.Compute(_historic.Doc, liveDoc), _historic.Doc, state.Document);
    }

    private void FetchHistoric(string repoPath, string jsonRel, string reference)
    {
        var sha = GLoomRepository.ResolveCommit(repoPath, reference);
        CanonicalDocument? doc = null;
        if (sha is not null)
        {
            var key = repoPath + "|" + jsonRel + "|" + sha;
            if (!_historicCache.TryGetValue(key, out doc))
            {
                var json = GLoomRepository.ReadFileAtCommit(repoPath, sha, jsonRel);
                doc = CanonicalJson.TryParse(json);
                if (doc is not null)
                {
                    // Content-addressed by SHA, so entries never go stale;
                    // the bound only guards against unbounded growth from
                    // many reference-compare picks in one session.
                    if (_historicCache.Count > 4) _historicCache.Clear();
                    _historicCache[key] = doc;
                }
            }
        }

        if (!TryInvokeOnUi(() => OnHistoricFetched(repoPath, jsonRel, reference, sha, doc)))
            _fetchInFlight = false;
    }

    private void OnHistoricFetched(
        string repoPath, string jsonRel, string reference, string? sha, CanonicalDocument? doc)
    {
        _fetchInFlight = false;

        // Drop stale results: the tracked file or the picked reference
        // moved on while the fetch ran. The dirty flags are still set, so
        // rescheduling recomputes against the new state.
        var state = DocumentTracker.Instance.State;
        var currentJsonRel = state.RepoPath is not null && state.CanonicalJsonFullPath is not null
            ? Path.GetRelativePath(state.RepoPath, state.CanonicalJsonFullPath)
            : null;
        if (state.RepoPath != repoPath || currentJsonRel != jsonRel || _comparisonReference != reference)
        {
            ScheduleRecompute();
            return;
        }

        if (sha is null || doc is null)
        {
            // Latch OFF instead of retrying on a clock: a repo whose
            // .gloom.json was never committed used to re-spawn git every
            // 250ms forever. Only a state change, a reference change or a
            // re-enable resets the latch.
            _historicUnavailable = true;
            _historic = null;
            SetDiff(null, null, null);
            RhinoApp.WriteLine($"[G-Loom] Overlay: no comparison data at {reference}.");
            return;
        }

        _historic = new HistoricSnapshot(repoPath, jsonRel, sha, doc);
        _historicDirty = false;
        // The baseline changed, so the live side must re-diff against it
        // even if no canvas edit happened since the last recompute.
        _liveDirty = true;
        RunRecompute();
    }

    private void SetDiff(DocumentDiff? diff, CanonicalDocument? fromDoc, GH_Document? diffDoc)
    {
        _cachedDiff = diff;
        _cachedFromDoc = fromDoc;
        _diffDoc = diffDoc;
        RebuildLiveCaches(diff is null ? null : diffDoc);
        RebuildHoverableIndex();
        Instances.ActiveCanvas?.Invalidate();
    }

    private void RebuildLiveCaches(GH_Document? doc)
    {
        _liveById.Clear();
        _liveOutputSources.Clear();
        if (doc is null) return;

        foreach (var obj in doc.Objects)
        {
            _liveById[obj.InstanceGuid.ToString("D")] = obj;
            if (obj is IGH_Component comp)
            {
                foreach (var output in comp.Params.Output)
                    _liveOutputSources[output.InstanceGuid.ToString("D")] = (output, comp);
            }
            else if (obj is IGH_Param param)
            {
                _liveOutputSources[param.InstanceGuid.ToString("D")] = (param, param);
            }
        }
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
        // (Guid.ToString("D")); rebuilt per recompute, not per frame.
        var liveById = s._liveById;

        // World-space viewport for culling: items far outside the visible
        // region skip their text/gradient rendering entirely. Margins are
        // generous so labels and previews extending past item bounds never
        // pop at the viewport edge; hit-test registration and wire anchors
        // are NOT culled (an off-screen deleted source still anchors a
        // wire to an on-screen consumer).
        var visible = canvas.Viewport.VisibleRegion;

        var deletionRects = new Dictionary<string, RectangleF>(StringComparer.Ordinal);
        if (s.ShowDeleted)
        {
            foreach (var removed in diff.ObjectsRemoved)
            {
                var rect = DeletedGhostRect(removed);
                deletionRects[removed.InstanceGuid] = rect;
                s._ghostRegions.Add(new GhostRegion(rect, GhostRestoreKind.Deleted, null, removed));
                if (!visible.IntersectsWith(RectangleF.Inflate(rect, 80f, 80f))) continue;
                PaintDeletedGhost(graphics, removed, rect);
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
            PaintMissingWires(graphics, diff, s._liveOutputSources, liveById, deletionRects, visible);
            PaintAddedWires(graphics, diff, s._liveOutputSources, liveById, visible);
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

            // Cull on the union of live and (for moves) old bounds so a
            // trail whose far end is on-screen still renders. The vertical
            // margin tracks the item's own height because slider and
            // MD-slider ghosts mirror the live component's height below its
            // bounds (a user-resized MD slider can be arbitrarily tall).
            var liveBounds = live.Attributes?.Bounds ?? RectangleF.Empty;
            if (!liveBounds.IsEmpty)
            {
                var cullRect = liveBounds;
                if ((change.Kinds & ObjectChangeKind.Moved) != 0)
                {
                    var livePivot = live.Attributes?.Pivot ?? PointF.Empty;
                    var old = liveBounds;
                    old.Offset(change.From.Pivot.X - livePivot.X, change.From.Pivot.Y - livePivot.Y);
                    cullRect = RectangleF.Union(cullRect, old);
                }
                var vMargin = Math.Max(200f, liveBounds.Height + 60f);
                if (!visible.IntersectsWith(RectangleF.Inflate(cullRect, 120f, vMargin))) continue;
            }

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

            var b = live.Attributes?.Bounds ?? RectangleF.Empty;
            if (!b.IsEmpty && !visible.IntersectsWith(RectangleF.Inflate(b, 20f, 20f))) continue;

            var pen = change.Kinds == ObjectChangeKind.Moved
                ? OverlayResources.HaloMoved
                : OverlayResources.HaloModified;
            PaintHalo(graphics, live, pen);
        }

        if (s.ShowAdded)
        {
            foreach (var added in diff.ObjectsAdded)
            {
                if (!liveById.TryGetValue(added.InstanceGuid, out var live)) continue;
                var b = live.Attributes?.Bounds ?? RectangleF.Empty;
                if (!b.IsEmpty && !visible.IntersectsWith(RectangleF.Inflate(b, 20f, 20f))) continue;
                PaintHalo(graphics, live, OverlayResources.HaloAdded);
            }
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

                // Never culled: the preview box extends above the panel by
                // the full wrapped height of the OLD text, which is
                // unbounded - no fixed margin is safe, and changed panels
                // are few.
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

        var headerFont = OverlayResources.F8Bold;
        var bodyFont = OverlayResources.F9;

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

        g.FillRectangle(OverlayResources.PanelPreviewFill, box);
        g.DrawRectangle(OverlayResources.YellowOutline, box.X, box.Y, box.Width, box.Height);

        g.DrawString("was:", headerFont, OverlayResources.PanelPreviewHeader, box.X + 6f, box.Y + 5f);

        var bodyRect = new RectangleF(
            box.X + 6f,
            box.Y + 5f + headerSize.Height + 2f,
            box.Width - 12f,
            bodySize.Height);
        g.DrawString(oldText, bodyFont, OverlayResources.PanelPreviewBody, bodyRect);
    }

    private static void PaintHalo(Graphics g, IGH_DocumentObject obj, Pen pen)
    {
        var bounds = obj.Attributes?.Bounds ?? RectangleF.Empty;
        if (bounds.IsEmpty) return;
        var halo = bounds;
        halo.Inflate(6f, 6f);
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

        g.FillRectangle(OverlayResources.TrailFill, oldBounds);
        g.DrawRectangle(OverlayResources.TrailOutline,
            oldBounds.X, oldBounds.Y, oldBounds.Width, oldBounds.Height);

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
            g.DrawLine(OverlayResources.MovedArrow, oldCenter, newCenter);

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
        var labelFontForSizing = OverlayResources.F8;
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
        else if (oldKind == "gradient")
        {
            // Gradient bar + label below; mirror the live gradient's
            // height so it reads as "the OLD gradient lived here".
            ghostHeight = Math.Max(28f, Math.Min(liveBounds.Height, 44f));
        }
        else if (oldKind == "mdslider")
        {
            // Match the live component bounds so the ghost reads as
            // "the OLD slider lived right here" and the X/Y dot lands
            // at the same screen position the live triangle would
            // occupy for those values.
            ghostHeight = liveBounds.Height;
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

        var ghostFill = OverlayResources.GhostFill;
        var ghostOutline = OverlayResources.YellowDottedOutline;

        if (oldKind == "color" && !string.IsNullOrEmpty(oldData?.ColorArgb))
        {
            var swatchColor = TryParseArgb(oldData.ColorArgb!) ?? Color.Gray;
            using var swatchFill = new SolidBrush(swatchColor);
            g.FillRectangle(swatchFill, ghost);
            g.DrawRectangle(ghostOutline, ghost.X, ghost.Y, ghost.Width, ghost.Height);

            var hsvaLabel = ColorHsvaLabel(swatchColor);
            var font = OverlayResources.F8;
            var size = g.MeasureString(hsvaLabel, font);
            g.DrawString(hsvaLabel, font, OverlayResources.GhostText,
                ghost.X + Math.Max(4f, (ghost.Width - size.Width) / 2f),
                ghost.Bottom + 2f);
            return ghost;
        }

        if (oldKind == "gradient" && oldData?.GradientStops is { Count: > 0 } stops)
        {
            PaintGradientBar(g, ghost, stops, Color.FromArgb(220, 230, 200, 0));
            return ghost;
        }

        if (oldKind == "mdslider" && oldData?.MdSlider is { } md)
        {
            PaintMdSliderBox(g, ghost, md, Color.FromArgb(220, 230, 200, 0), Color.FromArgb(230, 180, 140, 0));
            return ghost;
        }

        g.FillRectangle(ghostFill, ghost);
        g.DrawRectangle(ghostOutline, ghost.X, ghost.Y, ghost.Width, ghost.Height);

        if (oldKind == "slider" && oldData?.Slider is { } sv)
            PaintSliderInside(g, ghost, sv, to.Persistent?.Slider);

        var label = OldStateLabel(from.Persistent, to.Persistent);
        if (!string.IsNullOrEmpty(label))
        {
            var font = OverlayResources.F8;
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
            g.DrawString(label, font, OverlayResources.GhostText,
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
        var rangeFont = OverlayResources.F75;
        var minText = FormatSliderValue(oldSv.Min, oldSv.Decimals);
        var maxText = FormatSliderValue(oldSv.Max, oldSv.Decimals);
        var minSize = g.MeasureString(minText, rangeFont);
        var maxSize = g.MeasureString(maxText, rangeFont);

        var trackY = ghost.Top + ghost.Height * 0.30f;
        var trackLeft = ghost.Left + 4f + minSize.Width + 4f;
        var trackRight = ghost.Right - 4f - maxSize.Width - 4f;
        if (trackRight <= trackLeft) return;

        g.DrawLine(OverlayResources.SliderTrack, trackLeft, trackY, trackRight, trackY);

        var range = (float)(oldSv.Max - oldSv.Min);
        var ratio = range > 0
            ? (float)((double)(oldSv.Value - oldSv.Min) / range)
            : 0.5f;
        ratio = Math.Clamp(ratio, 0f, 1f);
        var knobX = trackLeft + ratio * (trackRight - trackLeft);

        var knobBrush = OverlayResources.Brush(Color.FromArgb(230, 180, 140, 0));
        var knobR = 4.5f;
        g.FillEllipse(knobBrush, knobX - knobR, trackY - knobR, knobR * 2f, knobR * 2f);

        var minChanged = newSv is not null && newSv.Min != oldSv.Min;
        var maxChanged = newSv is not null && newSv.Max != oldSv.Max;

        var minBrush = minChanged ? OverlayResources.SliderRangeChanged : OverlayResources.SliderRangeDefault;
        var maxBrush = maxChanged ? OverlayResources.SliderRangeChanged : OverlayResources.SliderRangeDefault;

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
            "gradient"  => null, // label drawn inside PaintGradientBar
            "mdslider"  => null, // label drawn inside PaintMdSliderBox
            "data"      => "was: data",
            _           => null,
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    /// <summary>
    /// Renders a horizontal gradient bar inside the given rect with the
    /// supplied stops. Each pair of adjacent stops paints a linear
    /// gradient region between their normalized positions (0..1). The
    /// outline color is themed by caller - yellow for modification
    /// ghosts, red for deletion ghosts.
    /// </summary>
    private static void PaintGradientBar(Graphics g, RectangleF rect, IReadOnlyList<GradientStop> stops, Color outlineColor)
    {
        if (stops.Count == 0) return;
        var sorted = stops.OrderBy(s => s.Position).ToList();
        var bar = new RectangleF(rect.X + 4f, rect.Y + 4f, rect.Width - 8f, rect.Height - 8f);
        if (bar.Width <= 1f || bar.Height <= 1f) return;

        // One LinearGradientBrush with InterpolationColors covers the
        // whole bar — eliminates the sub-pixel seams that appeared
        // when stitching multiple per-segment brushes together.
        // ColorBlend requires positions sorted in 0..1 with both
        // endpoints present, so pad the boundary colors when the
        // actual stops don't span the full range.
        var colors = new List<Color>();
        var positions = new List<float>();
        var first = TryParseArgb(sorted[0].ColorArgb) ?? Color.Gray;
        var last = TryParseArgb(sorted[^1].ColorArgb) ?? Color.Gray;
        if (sorted[0].Position > 0d)
        {
            colors.Add(first);
            positions.Add(0f);
        }
        foreach (var s in sorted)
        {
            colors.Add(TryParseArgb(s.ColorArgb) ?? Color.Gray);
            positions.Add((float)Math.Clamp(s.Position, 0d, 1d));
        }
        if (sorted[^1].Position < 1d)
        {
            colors.Add(last);
            positions.Add(1f);
        }
        // ColorBlend rejects duplicate positions; nudge any collision
        // by a hair so a stop right at 0 or 1 paired with our padding
        // doesn't throw.
        for (int i = 1; i < positions.Count; i++)
            if (positions[i] <= positions[i - 1])
                positions[i] = Math.Min(1f, positions[i - 1] + 1e-4f);

        using (var lgb = new LinearGradientBrush(
            new PointF(bar.X, bar.Y),
            new PointF(bar.Right, bar.Y),
            colors[0], colors[^1]))
        {
            lgb.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend(colors.Count)
            {
                Colors = colors.ToArray(),
                Positions = positions.ToArray(),
            };
            g.FillRectangle(lgb, bar);
        }

        g.DrawRectangle(OverlayResources.Pen15(outlineColor), bar.X, bar.Y, bar.Width, bar.Height);

        // Stop-count label sits BELOW the bar, dark brown-olive so it
        // reads against the canvas grid the same way the MD slider
        // and color-swatch labels do.
        var font = OverlayResources.F8;
        var label = $"was: {sorted.Count} stop{(sorted.Count == 1 ? "" : "s")}";
        var size = g.MeasureString(label, font);
        g.DrawString(label, font, OverlayResources.GhostLabelText,
            rect.X + Math.Max(4f, (rect.Width - size.Width) / 2f),
            rect.Bottom + 2f);
    }

    /// <summary>
    /// Renders a 2D box with a dot at the (X, Y) position so the user
    /// reads the OLD MD slider position at a glance. The slider value
    /// space is assumed to be normalized 0..1 on each axis (which is
    /// GH's default); out-of-range values are clamped for display.
    /// </summary>
    private static void PaintMdSliderBox(Graphics g, RectangleF rect, MdSliderValue md, Color outlineColor, Color knobColor)
    {
        var box = RectangleF.Inflate(rect, -4f, -4f);
        if (box.Width <= 2f || box.Height <= 2f) return;

        g.FillRectangle(OverlayResources.Fill40(outlineColor), box);
        g.DrawRectangle(OverlayResources.Pen15(outlineColor), box.X, box.Y, box.Width, box.Height);

        var nx = (float)Math.Clamp(md.X, 0d, 1d);
        var ny = (float)Math.Clamp(md.Y, 0d, 1d);
        // GH's MD slider uses (0,0) bottom-left, (1,1) top-right; screen
        // Y is top-down, so flip ny when projecting onto the box.
        var dot = new PointF(box.X + nx * box.Width, box.Y + (1f - ny) * box.Height);
        var knobBrush = OverlayResources.Brush(knobColor);
        const float r = 5f;
        g.FillEllipse(knobBrush, dot.X - r, dot.Y - r, r * 2f, r * 2f);

        var font = OverlayResources.F75;
        // Match the dark brown-olive used for color-swatch labels — the
        // light-yellow outline color is too pale to read against the
        // ghost fill and the canvas grid.
        var label = $"was: ({md.X:0.##}, {md.Y:0.##})";
        var size = g.MeasureString(label, font);
        g.DrawString(label, font, OverlayResources.GhostLabelText,
            rect.X + Math.Max(2f, (rect.Width - size.Width) / 2f),
            rect.Bottom + 2f);
    }

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
        // TryParse instead of Convert-with-catch: this runs inside gradient
        // stop loops, and malformed hex used to throw per stop per frame.
        if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return Color.FromArgb(unchecked((int)value));
        return null;
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
        Dictionary<string, (IGH_Param Param, IGH_DocumentObject Owner)> liveOutputSources,
        Dictionary<string, IGH_DocumentObject> liveById,
        Dictionary<string, RectangleF> deletionRects,
        RectangleF visible)
    {
        // Source-anchor lookups keyed by output PARAM GUID (the GUID
        // that appears in downstream input.Sources). Live anchors come
        // from per-port OutputGrip; ghost anchors are distributed
        // along the deletion rect's right edge per output index.
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

        var pen = OverlayResources.MissingWire;

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
                    if (!TryFindWireAnchor(src, liveOutputSources, ghostOutputAnchors, out var srcAnchor)) continue;
                    var consumerAnchor = LiveInputAnchor(consumer, consumerInputs, i);
                    DrawWireBezier(g, pen, srcAnchor, consumerAnchor, visible);
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
                    if (TryFindWireAnchor(src, liveOutputSources, ghostOutputAnchors, out var srcAnchor))
                        DrawWireBezier(g, pen, srcAnchor, consumerAnchor, visible);
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
        Dictionary<string, (IGH_Param Param, IGH_DocumentObject Owner)> liveOutputSources,
        Dictionary<string, IGH_DocumentObject> liveById,
        RectangleF visible)
    {
        if (liveOutputSources.Count == 0) return;

        var pen = OverlayResources.AddedWire;

        // Pass A: brand-new components - every input source is new.
        foreach (var added in diff.ObjectsAdded)
        {
            if (!liveById.TryGetValue(added.InstanceGuid, out var consumer)) continue;
            var consumerInputs = LiveInputsOf(consumer);

            for (var i = 0; i < added.Inputs.Count; i++)
            {
                foreach (var src in added.Inputs[i].Sources)
                {
                    if (!TryResolveLiveAnchor(liveOutputSources, src, out var srcAnchor)) continue;
                    var consumerAnchor = LiveInputAnchor(consumer, consumerInputs, i);
                    DrawWireBezier(g, pen, srcAnchor, consumerAnchor, visible);
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
                    if (!TryResolveLiveAnchor(liveOutputSources, src, out var srcAnchor)) continue;
                    var consumerAnchor = LiveInputAnchor(consumer, consumerInputs, i);
                    DrawWireBezier(g, pen, srcAnchor, consumerAnchor, visible);
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
    private static void DrawWireBezier(Graphics g, Pen pen, PointF source, PointF target, RectangleF visible)
    {
        var bend = Math.Max(40f, Math.Abs(target.X - source.X) * 0.5f);

        // The curve stays inside the convex hull of the four control
        // points: the endpoints' Y range, widened in X by the bend.
        var hull = RectangleF.FromLTRB(
            Math.Min(source.X, target.X) - bend,
            Math.Min(source.Y, target.Y) - 4f,
            Math.Max(source.X, target.X) + bend,
            Math.Max(source.Y, target.Y) + 4f);
        if (!visible.IntersectsWith(hull)) return;

        var c1 = new PointF(source.X + bend, source.Y);
        var c2 = new PointF(target.X - bend, target.Y);
        g.DrawBezier(pen, source, c1, c2, target);
    }

    /// <summary>
    /// Resolves a live source anchor from the per-recompute param cache.
    /// The grip is read fresh each frame so wires keep tracking drags;
    /// falls back to the owning visual's right-edge midpoint when the
    /// grip isn't available, matching the old per-frame anchor builder.
    /// </summary>
    private static bool TryResolveLiveAnchor(
        Dictionary<string, (IGH_Param Param, IGH_DocumentObject Owner)> liveOutputSources,
        string sourceGuid,
        out PointF anchor)
    {
        anchor = PointF.Empty;
        if (!liveOutputSources.TryGetValue(sourceGuid, out var entry)) return false;

        var grip = entry.Param.Attributes?.OutputGrip ?? PointF.Empty;
        if (grip == PointF.Empty)
        {
            var b = entry.Owner.Attributes?.Bounds ?? RectangleF.Empty;
            if (b.IsEmpty) return false;
            grip = new PointF(b.Right, b.Y + b.Height / 2f);
        }
        anchor = grip;
        return true;
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
        Dictionary<string, (IGH_Param Param, IGH_DocumentObject Owner)> liveOutputSources,
        Dictionary<string, PointF> ghostOutputAnchors,
        out PointF anchor)
    {
        if (TryResolveLiveAnchor(liveOutputSources, sourceGuid, out anchor)) return true;
        return ghostOutputAnchors.TryGetValue(sourceGuid, out anchor);
    }

    private static RectangleF DeletedGhostRect(CanonicalObject deleted)
    {
        if (deleted.Bounds is { } b && b.Width > 0 && b.Height > 0)
            return new RectangleF(b.X, b.Y, b.Width, b.Height);
        return new RectangleF(deleted.Pivot.X, deleted.Pivot.Y, 100f, 60f);
    }

    private static void PaintDeletedGhost(Graphics g, CanonicalObject deleted, RectangleF rect)
    {
        g.FillRectangle(OverlayResources.RedGhostFill, rect);
        g.DrawRectangle(OverlayResources.RedGhostOutline, rect.X, rect.Y, rect.Width, rect.Height);

        switch (deleted.Persistent?.Kind)
        {
            case "slider":    PaintDeletedSlider(g, rect, deleted.Persistent.Slider);          break;
            case "panel":     PaintDeletedTextBody(g, rect, deleted.Persistent.PanelText);     break;
            case "boolean":   PaintDeletedBoolean(g, rect, deleted.Persistent.BooleanState);   break;
            case "valuelist": PaintDeletedValueList(g, rect, deleted.Persistent.ValueListSelected); break;
            case "color":     PaintDeletedSwatch(g, rect, deleted.Persistent.ColorArgb);       break;
            case "gradient":
                if (deleted.Persistent.GradientStops is { Count: > 0 } gs)
                    PaintGradientBar(g, rect, gs, Color.FromArgb(220, 180, 30, 30));
                break;
            case "mdslider":
                if (deleted.Persistent.MdSlider is { } dmd)
                    PaintMdSliderBox(g, rect, dmd,
                        Color.FromArgb(220, 180, 30, 30),
                        Color.FromArgb(240, 220, 40, 40));
                break;
        }

        var name = string.IsNullOrEmpty(deleted.Nickname) ? deleted.Name : deleted.Nickname;
        if (!string.IsNullOrEmpty(name))
        {
            var font = OverlayResources.F9Bold;
            var size = g.MeasureString(name, font);
            g.DrawString(name, font, OverlayResources.RedName,
                rect.X + (rect.Width - size.Width) / 2f,
                rect.Bottom + 2f);
        }
    }

    private static void PaintDeletedSlider(Graphics g, RectangleF rect, SliderValue? sv)
    {
        if (sv is null) return;

        var rangeFont = OverlayResources.F75;
        var minText = FormatSliderValue(sv.Min, sv.Decimals);
        var maxText = FormatSliderValue(sv.Max, sv.Decimals);
        var minSize = g.MeasureString(minText, rangeFont);
        var maxSize = g.MeasureString(maxText, rangeFont);

        var trackY = rect.Top + rect.Height * 0.62f;
        var trackLeft = rect.Left + 4f + minSize.Width + 4f;
        var trackRight = rect.Right - 4f - maxSize.Width - 4f;
        if (trackRight <= trackLeft) return;

        g.DrawLine(OverlayResources.RedTrack, trackLeft, trackY, trackRight, trackY);

        var range = (float)(sv.Max - sv.Min);
        var ratio = range > 0
            ? (float)((double)(sv.Value - sv.Min) / range)
            : 0.5f;
        ratio = Math.Clamp(ratio, 0f, 1f);
        var knobX = trackLeft + ratio * (trackRight - trackLeft);

        var knobR = 4.5f;
        g.FillEllipse(OverlayResources.RedKnob, knobX - knobR, trackY - knobR, knobR * 2f, knobR * 2f);

        g.DrawString(minText, rangeFont, OverlayResources.RedRange, rect.Left + 4f, trackY - minSize.Height / 2f);
        g.DrawString(maxText, rangeFont, OverlayResources.RedRange, rect.Right - 4f - maxSize.Width, trackY - maxSize.Height / 2f);

        // Lift the value text out of the rect so it doesn't compete
        // with the track line for short slider bounds (where 'top + 2'
        // pushes the glyphs into the track at 62% height). Mirrors the
        // pattern of placing the component name below the rect.
        var valueText = FormatSliderValue(sv.Value, sv.Decimals);
        var valueFont = OverlayResources.F9Bold;
        var valSize = g.MeasureString(valueText, valueFont);
        g.DrawString(valueText, valueFont, OverlayResources.RedValue,
            rect.X + (rect.Width - valSize.Width) / 2f,
            rect.Top - valSize.Height - 2f);
    }

    private static void PaintDeletedTextBody(Graphics g, RectangleF rect, string? text)
    {
        var body = string.IsNullOrEmpty(text) ? "(empty)" : text;
        var bodyRect = new RectangleF(rect.X + 4f, rect.Y + 4f, rect.Width - 8f, rect.Height - 8f);
        g.DrawString(body, OverlayResources.F85, OverlayResources.RedValue, bodyRect);
    }

    private static void PaintDeletedBoolean(Graphics g, RectangleF rect, bool? state)
    {
        var label = state == true ? "True" : "False";
        var font = OverlayResources.F11Bold;
        var size = g.MeasureString(label, font);
        g.DrawString(label, font, OverlayResources.RedBool,
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
        g.DrawRectangle(OverlayResources.RedInnerOutline, inner.X, inner.Y, inner.Width, inner.Height);
    }
}
