using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

        // Derive "current commit" from the filesystem - the working-tree
        // .gh + .gbim.json blob pair uniquely identifies which commit's
        // content is on disk. No persisted marker needed; survives restarts.
        // Both files are required because the JSON is structural-only and
        // alone wouldn't disambiguate slider-only edits between commits.
        string? currentSha = null;
        if (isTracked && !string.IsNullOrEmpty(jsonFull) && !string.IsNullOrEmpty(path))
        {
            var ghRel = Path.GetRelativePath(repo!, path);
            var jsonRel = Path.GetRelativePath(repo!, jsonFull);
            currentSha = GBimRepository.FindCommitMatchingWorkingTree(repo!, ghRel, jsonRel);
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
            RhinoApp.WriteLine($"[G-BIM] Tracking {Path.GetFileName(path)} in repo {repo}");

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Forces a refresh from the current canvas - useful after a commit so the
    /// panel re-reads branch/last-commit info.
    /// </summary>
    public void Refresh() => UpdateActive(_state.Document ?? Instances.ActiveCanvas?.Document);

    /// <summary>
    /// Replaces the active Grasshopper document with the one freshly read from
    /// <paramref name="filePath"/>. Used after a Restore so the canvas reflects
    /// the new on-disk state without the user having to close + reopen the file.
    /// Returns the new document, or null if the reload failed.
    /// </summary>
    public GH_Document? ReloadFromDisk(string filePath)
    {
        try
        {
            var oldDoc = _state.Document;
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

            var newDoc = Instances.DocumentServer.AddDocument(filePath, true);
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
            RhinoApp.WriteLine($"[G-BIM] Reload failed: {ex.Message}");
            return null;
        }
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
            // .../Plug-ins/Grasshopper (<guid>)/Libraries/G-BIM/GBim.gha
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
                    RhinoApp.WriteLine($"[G-BIM] Cleaned reload-autosave: {Path.GetFileName(f)}");
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
