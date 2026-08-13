using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Rhino;

namespace GLoom.Vcs;

public sealed record TrackedState(
    GH_Document? Document,
    string? FilePath,
    string? RepoPath,
    string? CanonicalJsonFullPath,
    bool IsTracked,
    bool HasUnsavedChanges,
    string? CurrentRestoredSha);

/// <summary>
/// Singleton that listens for Grasshopper document open / close / save-as events
/// and resolves whether the active document lives inside a Git repo.
/// Subscribes are best-effort (multiple subscriptions to the same doc are
/// guarded by a flag on the doc instance via a weak set).
/// </summary>
public sealed class DocumentTracker
{
    private static readonly Lazy<DocumentTracker> _instance = new(() => new DocumentTracker());
    public static DocumentTracker Instance => _instance.Value;

    public event EventHandler? StateChanged;

    private TrackedState _state = new(null, null, null, null, false, false, null);
    private readonly HashSet<GH_Document> _hooked = new();
    private readonly HashSet<GH_Canvas> _hookedCanvases = new();

    // Working-tree match memo. ModifiedChanged fires in a flood during editing
    // but never touches disk, so two file stats decide whether the git match
    // can be skipped entirely. All UpdateActive entry points are GH/Eto UI
    // events, so plain fields are safe without locks. A null _matchedSha is a
    // valid cached answer ("matches no commit" - the normal dirty state).
    private sealed record MatchCacheKey(
        string Repo, string GhFull, string JsonFull,
        long GhLen, long GhWriteTicks, long JsonLen, long JsonWriteTicks);
    private MatchCacheKey? _matchKey;
    private string? _matchedSha;

    private int _suspendDepth;

    private DocumentTracker() { }

    public TrackedState State => _state;

    public void Initialize()
    {
        Instances.DocumentServer.DocumentAdded += OnDocumentAdded;
        Instances.DocumentServer.DocumentRemoved += OnDocumentRemoved;

        // Tab/window switches between open .gh files don't fire DocumentServer
        // events - we have to listen on the canvas itself for the active doc
        // swap. CanvasCreated covers future canvases; the loop covers the one
        // that may already exist by the time PriorityLoad runs.
        Instances.CanvasCreated += HookCanvas;
        if (Instances.ActiveCanvas != null) HookCanvas(Instances.ActiveCanvas);

        foreach (GH_Document doc in Instances.DocumentServer)
            HookDocument(doc);

        var active = Instances.ActiveCanvas?.Document;
        if (active != null) UpdateActive(active);
    }

    private void HookCanvas(GH_Canvas canvas)
    {
        if (canvas is null || !_hookedCanvases.Add(canvas)) return;
        canvas.DocumentChanged += (s, e) => UpdateActive(canvas.Document);
    }

    private void OnDocumentAdded(GH_DocumentServer sender, GH_Document doc)
    {
        HookDocument(doc);
        UpdateActive(doc);
    }

    private void OnDocumentRemoved(GH_DocumentServer sender, GH_Document doc)
    {
        _hooked.Remove(doc);
        if (ReferenceEquals(_state.Document, doc))
            UpdateActive(null);
    }

    private void HookDocument(GH_Document doc)
    {
        if (!_hooked.Add(doc)) return;
        doc.FilePathChanged += (_, _) => UpdateActive(doc);
        doc.ModifiedChanged += (_, _) => UpdateActive(doc);
    }

    /// <summary>
    /// Batches tracker recomputes across a compound operation (commit, restore,
    /// repo-wide reload): every UpdateActive raised while the scope is open is
    /// swallowed, and the outermost dispose runs exactly one forced recompute.
    /// Always-forced because a clean-document commit fires no events inside the
    /// scope yet still rewrites files. Resolves the document from the active
    /// canvas because a reload inside the scope replaces _state.Document with a
    /// dead instance. Re-entrant via a depth counter; UI-thread only.
    /// </summary>
    public IDisposable SuspendUpdates()
    {
        _suspendDepth++;
        return new SuspendScope(this);
    }

    private sealed class SuspendScope : IDisposable
    {
        private DocumentTracker? _tracker;

        public SuspendScope(DocumentTracker tracker) => _tracker = tracker;

        public void Dispose()
        {
            var t = _tracker;
            if (t is null) return;
            _tracker = null;
            if (--t._suspendDepth == 0)
            {
                // The fallback must be validated against the DocumentServer:
                // a reload inside the scope may have removed _state.Document
                // without re-adding it (file absent on the new branch, or a
                // failed load), and its DocumentRemoved was swallowed by the
                // suspension - resurrecting the dead instance would pin the
                // tracker (and Commit) to a document that no longer exists.
                t.UpdateActive(
                    Instances.ActiveCanvas?.Document ?? IfStillOpen(t._state.Document),
                    forceMatch: true);
            }
        }

        private static GH_Document? IfStillOpen(GH_Document? doc)
        {
            if (doc is null) return null;
            foreach (GH_Document open in Instances.DocumentServer)
                if (ReferenceEquals(open, doc))
                    return doc;
            return null;
        }
    }

    private void UpdateActive(GH_Document? doc, bool forceMatch = false)
    {
        if (_suspendDepth > 0) return;

        var path = doc?.IsFilePathDefined == true ? doc.FilePath : null;
        var repo = RepoDiscovery.FindRepoRoot(path);
        var jsonFull = string.IsNullOrEmpty(path) ? null : RepoDiscovery.CanonicalJsonFullPathFor(path);
        var isTracked = doc != null && !string.IsNullOrEmpty(path) && repo != null;
        var dirty = doc?.IsModified ?? false;

        // Derive "current commit" from the filesystem - the working-tree
        // .gh + .gloom.json blob pair uniquely identifies which commit's
        // content is on disk. No persisted marker needed; survives restarts.
        // Both files are required because the JSON is structural-only and
        // alone wouldn't disambiguate slider-only edits between commits.
        string? currentSha = null;
        if (isTracked && !string.IsNullOrEmpty(jsonFull) && !string.IsNullOrEmpty(path))
        {
            currentSha = ResolveCurrentSha(repo!, path, jsonFull, forceMatch);
        }
        else
        {
            _matchKey = null;
            _matchedSha = null;
        }

        var newState = new TrackedState(
            Document: doc,
            FilePath: path,
            RepoPath: repo,
            CanonicalJsonFullPath: jsonFull,
            IsTracked: isTracked,
            HasUnsavedChanges: dirty,
            CurrentRestoredSha: currentSha);

        // Suppress no-op transitions (prevents re-render flood from ModifiedChanged on every solve).
        if (StatesEquivalent(_state, newState)) return;

        _state = newState;

        if (isTracked)
            RhinoApp.WriteLine($"[G-Loom] Tracking {Path.GetFileName(path)} in repo {repo}");

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private string? ResolveCurrentSha(string repo, string ghFull, string jsonFull, bool force)
    {
        var key = new MatchCacheKey(
            repo, ghFull, jsonFull,
            StatLen(ghFull), StatTicks(ghFull), StatLen(jsonFull), StatTicks(jsonFull));
        if (!force && key == _matchKey) return _matchedSha;

        _matchedSha = GLoomRepository.FindCommitMatchingWorkingTree(
            repo,
            Path.GetRelativePath(repo, ghFull),
            Path.GetRelativePath(repo, jsonFull));
        _matchKey = key;
        return _matchedSha;
    }

    private static long StatLen(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.Length : -1; }
        catch { return -1; }
    }

    private static long StatTicks(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// Forces a refresh from the current canvas - useful after a commit so the
    /// panel re-reads branch/last-commit info. Bypasses the match memo:
    /// a checkout can change history without changing file bytes or mtimes
    /// (content-identical branches), which the stat key cannot see.
    /// </summary>
    public void Refresh() =>
        UpdateActive(_state.Document ?? Instances.ActiveCanvas?.Document, forceMatch: true);

    /// <summary>
    /// Replaces the active Grasshopper document with the one freshly read from
    /// <paramref name="filePath"/>. Used after a Restore so the canvas reflects
    /// the new on-disk state without the user having to close + reopen the file.
    /// Returns the new document, or null if the reload failed.
    /// </summary>
    public GH_Document? ReloadFromDisk(string filePath) =>
        ReloadFromDisk(_state.Document, filePath);

    /// <summary>
    /// Reload variant that targets a specific open document instead of the
    /// active one. Used by <see cref="ReloadAllInRepo"/> when a branch
    /// switch needs to refresh several open .gh files at once.
    ///
    /// If the target file doesn't exist on disk after the swap (the .gh was
    /// deleted on the new branch), the old document is closed and null is
    /// returned. Caller should handle that.
    /// </summary>
    public GH_Document? ReloadFromDisk(GH_Document? oldDoc, string filePath)
    {
        try
        {
            var autoSaveDir = GetAutoSaveDir();

            // Snapshot the AutoSave folder so we can clean up any files that
            // appear during the reload. Grasshopper's "AutoSave:Unload"
            // setting triggers a write on RemoveDocument regardless of our
            // IsModified=false flag, leaving a stale autosave that triggers
            // the "Last Chance Recovery" dialog next time MyDef.gh opens.
            var preSnapshot = autoSaveDir is null
                ? new HashSet<string>()
                : Directory.EnumerateFiles(autoSaveDir).ToHashSet(StringComparer.Ordinal);

            if (oldDoc != null)
            {
                oldDoc.DestroyAutoSaveFiles();
                oldDoc.IsModified = false;
                Instances.DocumentServer.RemoveDocument(oldDoc);
            }

            // After a branch switch the file may have been deleted; only
            // re-add if it still exists on disk.
            var newDoc = File.Exists(filePath)
                ? Instances.DocumentServer.AddDocument(filePath, true)
                : null;
            newDoc?.DestroyAutoSaveFiles();

            // Clean autosaves that appeared during the reload, both
            // synchronously (catches files written during Remove/Add) and
            // again after a short delay (catches async writes that happen
            // shortly after AddDocument returns).
            CleanNewAutosaves(autoSaveDir, preSnapshot);
            _ = Task.Delay(750).ContinueWith(_ => CleanNewAutosaves(autoSaveDir, preSnapshot));

            return newDoc;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-Loom] Reload failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reloads every open Grasshopper document whose file path lives inside
    /// <paramref name="repoRoot"/>. The originally-active document is reloaded
    /// last so it remains the active canvas after the swap (DocumentServer
    /// .AddDocument with displayCanvas=true makes the loaded doc active).
    ///
    /// Used after `git checkout &lt;branch&gt;` to bring every affected
    /// canvas in sync with the new working tree, not just the one the panel
    /// happens to be looking at.
    /// </summary>
    public void ReloadAllInRepo(string repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot)) return;

        var active = _state.Document;
        var targets = new List<(GH_Document doc, string path)>();
        foreach (GH_Document doc in Instances.DocumentServer)
        {
            if (!doc.IsFilePathDefined) continue;
            var path = doc.FilePath;
            if (string.IsNullOrEmpty(path)) continue;
            if (!IsInsideRepo(path, repoRoot)) continue;
            targets.Add((doc, path));
        }

        // Originally-active doc reloads last so it ends up displayed.
        targets.Sort((a, b) =>
            ReferenceEquals(a.doc, active) ? 1 :
            ReferenceEquals(b.doc, active) ? -1 : 0);

        // Each reload fires DocumentRemoved + DocumentAdded; without the scope
        // that is 2N full recomputes for an N-document branch switch.
        using (SuspendUpdates())
        {
            foreach (var t in targets)
                ReloadFromDisk(t.doc, t.path);
        }
    }

    private static bool IsInsideRepo(string filePath, string repoRoot)
    {
        try
        {
            var abs = Path.GetFullPath(filePath);
            var rep = Path.GetFullPath(repoRoot);
            if (abs.Equals(rep, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = rep.EndsWith(Path.DirectorySeparatorChar)
                ? rep
                : rep + Path.DirectorySeparatorChar;
            return abs.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string? GetAutoSaveDir()
    {
        try
        {
            // Grasshopper's plugin GUID is stable across installs; the path is
            // ~/Library/Application Support/McNeel/Rhinoceros/<v>/Plug-ins/Grasshopper (<guid>)/AutoSave
            // on macOS, %APPDATA%\McNeel\Rhinoceros\<v>\plug-ins\... on Windows.
            var asmDir = Path.GetDirectoryName(typeof(DocumentTracker).Assembly.Location);
            if (string.IsNullOrEmpty(asmDir)) return null;

            // The plugin folder structure on macOS:
            // .../Plug-ins/Grasshopper (<guid>)/Libraries/G-Loom/GLoom.gha
            // We want:    .../Plug-ins/Grasshopper (<guid>)/AutoSave
            var ghPluginDir = Directory.GetParent(asmDir)?.Parent?.FullName;
            if (string.IsNullOrEmpty(ghPluginDir)) return null;

            var asDir = Path.Combine(ghPluginDir, "AutoSave");
            return Directory.Exists(asDir) ? asDir : null;
        }
        catch
        {
            return null;
        }
    }

    private static void CleanNewAutosaves(string? autoSaveDir, HashSet<string> preSnapshot)
    {
        if (autoSaveDir is null || !Directory.Exists(autoSaveDir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(autoSaveDir, "*.gh"))
            {
                if (preSnapshot.Contains(f)) continue;
                try
                {
                    File.Delete(f);
                    RhinoApp.WriteLine($"[G-Loom] Cleaned reload-autosave: {Path.GetFileName(f)}");
                }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    private static bool StatesEquivalent(TrackedState a, TrackedState b) =>
        ReferenceEquals(a.Document, b.Document) &&
        a.FilePath == b.FilePath &&
        a.RepoPath == b.RepoPath &&
        a.IsTracked == b.IsTracked &&
        a.HasUnsavedChanges == b.HasUnsavedChanges &&
        a.CurrentRestoredSha == b.CurrentRestoredSha;
}
