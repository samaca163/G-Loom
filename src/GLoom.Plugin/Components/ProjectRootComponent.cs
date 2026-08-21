using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using GLoom.Ui;
using GLoom.Vcs;
using Grasshopper.Kernel;

namespace GLoom.Components;

/// <summary>
/// Emits the project root so definitions can build paths relative to it
/// instead of hard-coding one machine's folder layout.
/// </summary>
public sealed class ProjectRootComponent : GLoomComponent
{
    public ProjectRootComponent()
        : base("Project Root", "Root",
            "Resolves the folder at the root of the G-Loom project - the git repository - that contains this " +
            "definition. Build file paths from it so the definition keeps working on every machine and in every " +
            "clone instead of hard-coding one absolute folder.",
            ProjectGroup)
    {
    }

    public override Guid ComponentGuid => new("f4000e94-a7eb-4b36-9c1b-62fdc3297979");

    public override IEnumerable<string> Keywords =>
        new[] { "gloom", "git", "repo", "repository", "root", "project", "folder", "path" };

    protected override Bitmap Icon => GLoomIcons.ProjectRoot;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddTextParameter("Start", "S",
            "Optional file or folder to resolve from. Leave empty to resolve from this definition's own file.",
            GH_ParamAccess.item);
        pManager[0].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter("Root", "R",
            "Absolute path to the project root - the folder that holds .git.",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string? start = null;
        DA.GetData(0, ref start);

        if (string.IsNullOrWhiteSpace(start))
        {
            start = HostFilePath();
            if (string.IsNullOrWhiteSpace(start))
            {
                Message = "unsaved";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "This definition has not been saved yet, so there is no folder to resolve from. " +
                    "Save the .gh file inside your project, or plug a path into Start.");
                return;
            }
        }

        var root = RepoDiscovery.FindRepoRoot(start, allowMissingStart: true);
        if (root is null)
        {
            Message = "no project";
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"No git project found at or above \"{start}\". A definition becomes versionable once it lives " +
                "inside a repository - run `git init` at the project root.");
            return;
        }

        Message = Path.GetFileName(root);
        DA.SetData(0, root);
    }

    /// <summary>
    /// The .gh on disk that owns this component. Inside a cluster the pinged
    /// document is the cluster's own, which has no file - the real definition
    /// is further up the owner chain.
    /// </summary>
    private string? HostFilePath()
    {
        var doc = OnPingDocument();
        for (var hop = 0; doc is { IsFilePathDefined: false } && hop < 16; hop++)
            doc = doc.Owner?.OwnerDocument();
        return doc?.IsFilePathDefined == true ? doc.FilePath : null;
    }

    private GH_Document? _watched;

    public override void AddedToDocument(GH_Document document)
    {
        base.AddedToDocument(document);
        if (ReferenceEquals(_watched, document)) return;
        Unwatch();
        _watched = document;
        document.FilePathChanged += OnHostFilePathChanged;
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        Unwatch();
        base.RemovedFromDocument(document);
    }

    private void Unwatch()
    {
        if (_watched is null) return;
        _watched.FilePathChanged -= OnHostFilePathChanged;
        _watched = null;
    }

    private void OnHostFilePathChanged(object sender, GH_DocFilePathEventArgs e)
    {
        // Save As can move a definition into or out of a project, and the path
        // is not an input, so nothing else would expire this component.
        // Schedule rather than expire inline: the save is still in flight here.
        _watched?.ScheduleSolution(10, _ => ExpireSolution(false));
    }
}
