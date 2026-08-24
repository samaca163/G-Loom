using System;
using System.Collections.Generic;
using System.Drawing;
using GLoom.Survey;
using GLoom.Ui;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace GLoom.Components;

/// <summary>
/// Loads the survey vocabulary so the rest of the definition can consume it. A project's
/// own schema wins over the built-in default, which is the point: adding a field becomes
/// a commit with an author and a diff rather than a decision inside one person's Rhino.
/// </summary>
public sealed class SurveySchemaComponent : GLoomComponent
{
    public SurveySchemaComponent()
        : base("Survey Schema", "Schema",
            "Loads the survey schema - the categories, the fields each one carries, and the rules that map a " +
            "layer onto a category. Leave File empty to use the schema committed at the project root, or the " +
            "built-in default when the project declares none.",
            SurveyGroup)
    {
    }

    public override Guid ComponentGuid => new("f2fd5a57-4fbf-4794-b2c8-9f006c871751");

    public override IEnumerable<string> Keywords =>
        new[] { "gloom", "survey", "schema", "categories", "levantamiento", "bim", "revit" };

    protected override Bitmap Icon => GLoomIcons.SurveySchema;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddTextParameter("File", "F",
            "Optional path to a survey schema JSON file. Leave empty to look for one at " +
            "<project root>/.gloom/survey-schema.json, then fall back to the built-in default.",
            GH_ParamAccess.item);
        pManager.AddBooleanParameter("Reload", "R",
            "Re-read the schema from disk. The file is otherwise read once and cached until it changes, so " +
            "flip this after editing it outside Rhino.",
            GH_ParamAccess.item, false);
        pManager[0].Optional = true;
        pManager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddGenericParameter("Schema", "S",
            "The loaded schema. Feed it to Classify by Layer.", GH_ParamAccess.item);
        pManager.AddTextParameter("Categories", "C",
            "The category vocabulary this schema declares.", GH_ParamAccess.list);
        pManager.AddTextParameter("Path", "P",
            "Where the schema was loaded from, or \"built-in\".", GH_ParamAccess.item);
        pManager.AddTextParameter("Version", "V",
            "Schema id and content hash. Stamped onto every classified object, so a model can always say " +
            "which version of the vocabulary produced it.", GH_ParamAccess.item);
        pManager.AddTextParameter("Issues", "I",
            "Problems found in the schema. Reported rather than thrown, so one bad rule does not stop the rest.",
            GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string? file = null;
        DA.GetData(0, ref file);

        var reload = false;
        DA.GetData(1, ref reload);

        var loaded = SurveySchemaLoader.Load(file, HostFilePath(), reload);

        if (!string.IsNullOrWhiteSpace(file) && loaded.IsBuiltIn)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"No schema file at \"{file}\" - using the built-in default instead.");

        foreach (var issue in loaded.Issues)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, issue.ToString());

        var categories = new List<string>(loaded.Schema.Categories.Count);
        foreach (var category in loaded.Schema.Categories) categories.Add(category.Id);

        Message = loaded.IsBuiltIn ? "built-in" : $"{loaded.Schema.Categories.Count} categories";

        DA.SetData(0, new GH_ObjectWrapper(loaded));
        DA.SetDataList(1, categories);
        DA.SetData(2, loaded.Source);
        DA.SetData(3, loaded.Version);
        DA.SetDataList(4, Describe(loaded.Issues));
    }

    private static IEnumerable<string> Describe(IReadOnlyList<SchemaIssue> issues)
    {
        foreach (var issue in issues) yield return issue.ToString();
    }
}
