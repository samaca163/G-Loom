using System;
using System.Collections.Generic;
using System.Drawing;
using GLoom.Model;
using GLoom.Survey;
using GLoom.Ui;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Grasshopper.Rhinoceros.Model;
using Grasshopper.Rhinoceros.Model.Params;

namespace GLoom.Components;

/// <summary>
/// Resolves each object's category from the layer it is drawn on and materializes that
/// category's metadata container onto it - every key the schema declares, filled with
/// what is already there, then the schema default, then a placeholder.
///
/// Nothing here touches the Rhino document. The classifier runs on every solve, so it
/// has to be free of the write-loop machinery the stamping side needs; feed its Object
/// output to the native User Text component to land the result on the geometry.
/// </summary>
public sealed class ClassifyByLayerComponent : GLoomComponent
{
    public ClassifyByLayerComponent()
        : base("Classify by Layer", "Classify",
            "Reads each object's layer, resolves it to a survey category through the schema's rules, and builds " +
            "that category's full metadata container as attribute user text. Objects on a layer no rule matches " +
            "come out of Unmapped untouched, never guessed at.",
            SurveyGroup)
    {
    }

    public override Guid ComponentGuid => new("9ff807de-ed4b-46ff-b786-00931e39097f");

    public override IEnumerable<string> Keywords =>
        new[] { "gloom", "survey", "classify", "layer", "category", "usertext", "levantamiento", "capa", "revit" };

    protected override Bitmap Icon => GLoomIcons.SurveyClassify;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddParameter(new Param_ModelObject
        {
            Name = "Object",
            NickName = "O",
            Description = "Rhino objects to classify. Feed these from Query Model Objects.",
            Access = GH_ParamAccess.list,
        });
        pManager.AddGenericParameter("Schema", "S",
            "The schema from Survey Schema. Leave empty to load the project's schema, or the built-in default.",
            GH_ParamAccess.item);
        pManager.AddBooleanParameter("Strict", "X",
            "Treat unmapped layers as an error instead of a warning. Turn this on for a milestone or a " +
            "deliverable run; leave it off while the survey is still being tagged.",
            GH_ParamAccess.item, false);
        pManager[1].Optional = true;
        pManager[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddParameter(new Param_ModelObject
        {
            Name = "Object",
            NickName = "O",
            Description = "The classified objects, carrying the full metadata container as user text.",
            Access = GH_ParamAccess.tree,
        });
        pManager.AddParameter(new Param_Guid
        {
            Name = "Id",
            NickName = "I",
            Description = "The Rhino object id of each classified object.",
            Access = GH_ParamAccess.tree,
        });
        pManager.AddTextParameter("Keys", "K", "The user-text keys, one branch per object.", GH_ParamAccess.tree);
        pManager.AddTextParameter("Values", "V", "The matching values, aligned with Keys.", GH_ParamAccess.tree);
        pManager.AddTextParameter("Category", "C",
            "The Revit category each object resolved to, which is what routes it downstream.", GH_ParamAccess.tree);
        pManager.AddParameter(new Param_ModelObject
        {
            Name = "Unmapped",
            NickName = "U",
            Description = "Objects whose layer matched no rule. Excluded from every other output - these are " +
                          "exactly the rules still to be written.",
            Access = GH_ParamAccess.list,
        });
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var models = new List<ModelObject>();
        DA.GetDataList(0, models);

        GH_ObjectWrapper? wrapper = null;
        DA.GetData(1, ref wrapper);

        var strict = false;
        DA.GetData(2, ref strict);

        var loaded = wrapper?.Value as LoadedSchema ?? SurveySchemaLoader.Load(null, HostFilePath(), false);
        var unit = ModelObjectBridge.ActiveLengthUnit();

        var objects = new GH_Structure<IGH_Goo>();
        var ids = new GH_Structure<GH_Guid>();
        var keys = new GH_Structure<GH_String>();
        var values = new GH_Structure<GH_String>();
        var categories = new GH_Structure<GH_String>();
        var unmapped = new List<ModelObject>();
        var unmappedLayers = new SortedDictionary<string, int>(StringComparer.Ordinal);

        // One branch per object - {A;i} - so a downstream User Text or Element Parameter
        // expands element by field on longest-list matching with no grafting.
        var root = DA.ParameterTargetPath(0);
        var branch = 0;
        var noAttributes = 0;

        foreach (var model in models)
        {
            if (model is null) continue;

            var layer = ModelObjectBridge.LayerPathOf(model);
            if (layer is null) noAttributes++;

            var match = loaded.Matcher.Match(layer);
            if (match is null)
            {
                unmapped.Add(model);
                var where = LayerPath.Normalize(layer);
                unmappedLayers.TryGetValue(where, out var seen);
                unmappedLayers[where] = seen + 1;
                continue;
            }

            var existing = ModelObjectBridge.ReadUserText(model);
            var record = SurveyRecordBuilder.Build(loaded.Schema, match, existing, loaded.Hash, unit);
            var final = SurveyRecordBuilder.Merge(existing, record.Pairs);

            var path = root.AppendElement(branch++);
            objects.Append(ModelObjectBridge.WithUserText(model, final), path);
            ids.Append(new GH_Guid(ModelObjectBridge.IdOf(model) ?? Guid.Empty), path);
            categories.Append(new GH_String(match.Category.Revit), path);

            foreach (var pair in record.Pairs)
            {
                keys.Append(new GH_String(pair.Key), path);
                values.Append(new GH_String(pair.Value), path);
            }
        }

        Report(unmappedLayers, noAttributes, strict);
        Message = unmappedLayers.Count == 0
            ? $"{branch} classified"
            : $"{branch} classified · {unmapped.Count} unmapped";

        DA.SetDataTree(0, objects);
        DA.SetDataTree(1, ids);
        DA.SetDataTree(2, keys);
        DA.SetDataTree(3, values);
        DA.SetDataTree(4, categories);
        DA.SetDataList(5, unmapped);
    }

    private void Report(SortedDictionary<string, int> unmappedLayers, int noAttributes, bool strict)
    {
        if (noAttributes > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"{noAttributes} object(s) carried no layer. Feed this component from Query Model Objects rather " +
                "than from plain geometry, which has no attributes to read.");

        if (unmappedLayers.Count == 0) return;

        // A warning rather than an error by default, so the mapped majority still
        // delivers while the map is being written. The list is copy-pasteable because it
        // is the set of rules the architect still has to add.
        var level = strict ? GH_RuntimeMessageLevel.Error : GH_RuntimeMessageLevel.Warning;
        foreach (var pair in unmappedLayers)
        {
            var where = pair.Key.Length == 0 ? "(no layer)" : pair.Key;
            AddRuntimeMessage(level, $"No rule matches layer \"{where}\" ({pair.Value} object(s)).");
        }
    }
}
