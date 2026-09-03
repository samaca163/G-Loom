using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GLoom.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Rhino;

namespace GLoom.Ui;

/// <summary>
/// Putting a recorded version of an object back on the canvas: its position, its value, or
/// the whole component when it was deleted. Lifted out of the canvas overlay so the same
/// primitives serve both ways of rejecting a change - a person right-clicking a ghost, and
/// an agent calling gloom_restore_objects.
///
/// This replays history rather than authoring it: every object it creates is one that was on
/// the canvas at a recorded version, restored with its original identity. Authoring a graph
/// freely stays with Rhino's own MCP server.
///
/// Callers record the undo event and trigger the solution; these methods only expire, so a
/// batch of restores is one undoable step and one recompute.
/// </summary>
public static class DocumentRestore
{
    public static void RestorePivot(GH_Document doc, ObjectChange change)
    {
        if (!Guid.TryParse(change.To.InstanceGuid, out var id)) return;
        var live = doc.Objects.FirstOrDefault(o => o.InstanceGuid == id);
        if (live?.Attributes is null) return;

        live.Attributes.Pivot = new PointF(change.From.Pivot.X, change.From.Pivot.Y);
        live.Attributes.ExpireLayout();
        live.ExpireSolution(false);
    }

    public static string? RestorePersistent(GH_Document doc, ObjectChange change)
    {
        if (!Guid.TryParse(change.To.InstanceGuid, out var id)) return "Unreadable instance guid.";
        var live = doc.Objects.FirstOrDefault(o => o.InstanceGuid == id);
        if (live is null) return "That object is no longer on the canvas.";

        var reason = ApplyPersistent(live, change.From.Persistent);
        if (reason is not null) return reason;

        live.ExpireSolution(false);
        return null;
    }

    /// <summary>
    /// Recreate a deleted component from its captured CanonicalObject.
    /// Best-effort: we set the type, instance GUID, pivot, persistent
    /// state, and reconnect input wires to source params that still
    /// exist on the live canvas. If a source was also deleted (cascade),
    /// that wire is silently dropped - the user can fix it manually or
    /// restore the source first.
    /// </summary>
    public static void RestoreDeleted(GH_Document doc, CanonicalObject deleted)
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
    /// <summary>Null when the value was written; the reason it could not be, otherwise. The
    /// overlay ignores it - its menu only offers kinds it painted - but an agent restoring by
    /// name needs to be told, rather than being let believe a silent no-op worked.</summary>
    public static string? ApplyPersistent(IGH_DocumentObject obj, PersistentData? from)
    {
        if (from is null) return null;

        switch (from.Kind)
        {
            case "valuelist" when obj is GH_ValueList list && from.ValueListSelected is { } wanted:
                var items = list.ListItems;
                var single = wanted.Count == 1
                    ? items.FindIndex(i => string.Equals(i.Name, wanted[0], StringComparison.OrdinalIgnoreCase))
                    : -1;
                if (single >= 0)
                {
                    list.SelectItem(single);
                    break;
                }

                // Multi-selection: toggle only the items whose state differs, so a checklist
                // ends up exactly as the recorded version had it.
                for (var i = 0; i < items.Count; i++)
                {
                    var shouldBeOn = wanted.Any(w => string.Equals(w, items[i].Name, StringComparison.OrdinalIgnoreCase));
                    if (items[i].Selected != shouldBeOn) list.ToggleItem(i);
                }
                break;

            case "gradient":
            case "mdslider":
                return $"{obj.NickName} is a {from.Kind}; G-Loom reads its value but has no typed way to write it " +
                       "back. Set it on the canvas.";

            case "data":
                return $"{obj.NickName} holds internalised data, which G-Loom records only as a digest - there is " +
                       "nothing to restore from. Revert the whole definition instead.";

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

        return null;
    }
}
