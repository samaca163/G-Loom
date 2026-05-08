using System;
using System.IO;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino;

namespace GBim.Vcs;

public sealed record TrackedState(
    GH_Document? Document,
    string? FilePath,
    string? RepoPath,
    string? CanonicalJsonFullPath,
    bool IsTracked,
    bool HasUnsavedChanges);

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

    private TrackedState _state = new(null, null, null, null, false, false);
    private readonly System.Collections.Generic.HashSet<GH_Document> _hooked = new();

    private DocumentTracker() { }

    public TrackedState State => _state;

    public void Initialize()
    {
        Instances.DocumentServer.DocumentAdded += OnDocumentAdded;
        Instances.DocumentServer.DocumentRemoved += OnDocumentRemoved;

        foreach (GH_Document doc in Instances.DocumentServer)
            HookDocument(doc);

        var active = Instances.ActiveCanvas?.Document;
        if (active != null) UpdateActive(active);
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

    private void UpdateActive(GH_Document? doc)
    {
        var path = doc?.IsFilePathDefined == true ? doc.FilePath : null;
        var repo = RepoDiscovery.FindRepoRoot(path);
        var jsonFull = string.IsNullOrEmpty(path) ? null : RepoDiscovery.CanonicalJsonFullPathFor(path);
        var isTracked = doc != null && !string.IsNullOrEmpty(path) && repo != null;
        var dirty = doc?.IsModified ?? false;

        var newState = new TrackedState(
            Document: doc,
            FilePath: path,
            RepoPath: repo,
            CanonicalJsonFullPath: jsonFull,
            IsTracked: isTracked,
            HasUnsavedChanges: dirty);

        // Suppress no-op transitions (prevents re-render flood from ModifiedChanged on every solve).
        if (StatesEquivalent(_state, newState)) return;

        _state = newState;

        if (isTracked)
            RhinoApp.WriteLine($"[G-BIM] Tracking {Path.GetFileName(path)} in repo {repo}");

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Forces a refresh from the current canvas - useful after a commit so the
    /// panel re-reads branch/last-commit info.
    /// </summary>
    public void Refresh() => UpdateActive(_state.Document ?? Instances.ActiveCanvas?.Document);

    private static bool StatesEquivalent(TrackedState a, TrackedState b) =>
        ReferenceEquals(a.Document, b.Document) &&
        a.FilePath == b.FilePath &&
        a.RepoPath == b.RepoPath &&
        a.IsTracked == b.IsTracked &&
        a.HasUnsavedChanges == b.HasUnsavedChanges;
}
