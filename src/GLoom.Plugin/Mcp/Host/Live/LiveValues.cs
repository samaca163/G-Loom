using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Live;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GLoom.Mcp.Host.Live;

/// <summary>
/// Writing persistent values back onto the canvas: the one kind of edit G-Loom performs
/// itself, because it is the kind it can also checkpoint, diff and revert exactly. Graph
/// topology is deliberately left to Rhino's own MCP server.
///
/// Only the five kinds with a typed SDK setter are supported. Gradients and MD sliders are
/// read through reflection over third-party types, so writing them back would mean invoking
/// methods found by reflection - the one thing that has already broken Grasshopper's drawing
/// pipeline for a whole session - and internalised data is stored as a digest, with nothing
/// to write back at all. Those three refuse out loud rather than silently doing nothing.
/// </summary>
internal static class LiveValues
{
    /// <summary>Applies every edit as one undoable step. A failure is reported against its own
    /// target: an agent asking for six changes should learn which five landed.</summary>
    public static IReadOnlyList<ValueEditResult> Apply(
        GH_Document doc, IReadOnlyList<ValueEdit> edits, Func<GH_Document, string, IGH_DocumentObject> find)
    {
        var results = new List<ValueEditResult>(edits.Count);
        var touched = new List<IGH_DocumentObject>();

        doc.UndoUtil.RecordEvent("G-Loom: set values");

        foreach (var edit in edits)
        {
            IGH_DocumentObject obj;
            try
            {
                obj = find(doc, edit.Target);
            }
            catch (ToolArgumentException ex)
            {
                results.Add(new ValueEditResult(edit.Target, false, Reason: ex.Message));
                continue;
            }

            var before = Read(obj);
            string? reason;
            try
            {
                reason = Write(obj, edit.Value);
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }

            if (reason is not null)
            {
                results.Add(new ValueEditResult(
                    edit.Target, false, obj.InstanceGuid.ToString(), obj.Name, obj.NickName,
                    KindOf(obj), before, Reason: reason));
                continue;
            }

            touched.Add(obj);
            results.Add(new ValueEditResult(
                edit.Target, true, obj.InstanceGuid.ToString(), obj.Name, obj.NickName,
                KindOf(obj), before, Read(obj)));
        }

        foreach (var obj in touched) obj.ExpireSolution(false);
        return results;
    }

    /// <summary>Null when applied; the reason it could not be, otherwise.</summary>
    private static string? Write(IGH_DocumentObject obj, string value) => obj switch
    {
        GH_NumberSlider slider => WriteSlider(slider, value),
        GH_Panel panel => Ok(() => panel.UserText = value),
        GH_BooleanToggle toggle => WriteToggle(toggle, value),
        GH_ColourSwatch swatch => WriteColour(swatch, value),
        GH_ValueList list => WriteValueList(list, value),
        GH_GradientControl => "A gradient's stops cannot be set through G-Loom: they are read by reflection and have " +
                              "no typed setter. Edit it on the canvas, or place a new one with Rhino's MCP server.",
        _ => $"{obj.Name} holds no persistent value G-Loom can set. Sliders, panels, toggles, value lists and colour " +
             "swatches are settable; wire a value into the input instead, or use Rhino's MCP server to edit the graph.",
    };

    private static string? WriteSlider(GH_NumberSlider slider, string value)
    {
        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return $"\"{value}\" is not a number, and {slider.NickName} is a slider.";

        // Widening rather than refusing: an agent asking for 12 on a 0-10 slider means the
        // design moved, and clamping silently to 10 would be a lie in the commit that follows.
        if (number < slider.Slider.Minimum) slider.Slider.Minimum = number;
        if (number > slider.Slider.Maximum) slider.Slider.Maximum = number;
        slider.SetSliderValue(number);
        return null;
    }

    private static string? WriteToggle(GH_BooleanToggle toggle, string value)
    {
        if (!bool.TryParse(value, out var flag))
        {
            if (value is "1") flag = true;
            else if (value is "0") flag = false;
            else return $"\"{value}\" is not true or false, and {toggle.NickName} is a boolean toggle.";
        }
        toggle.Value = flag;
        return null;
    }

    private static string? WriteColour(GH_ColourSwatch swatch, string value)
    {
        var text = value.Trim().TrimStart('#');
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            var named = Color.FromName(value.Trim());
            if (!named.IsKnownColor)
                return $"\"{value}\" is not a colour; pass AARRGGBB or RRGGBB hex, or a colour name like \"Tomato\".";
            swatch.SwatchColour = named;
            return null;
        }

        // Six digits is RRGGBB, which carries no alpha; without this it would come out invisible.
        if (text.Length <= 6) hex |= 0xFF000000;
        swatch.SwatchColour = Color.FromArgb(unchecked((int)hex));
        return null;
    }

    private static string? WriteValueList(GH_ValueList list, string value)
    {
        var wanted = value.Trim();
        var items = list.ListItems;
        var index = items.FindIndex(i => string.Equals(i.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = items.FindIndex(i => string.Equals(i.Expression, wanted, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return $"\"{wanted}\" is not one of {list.NickName}'s items: {string.Join(", ", items.Select(i => i.Name))}.";

        list.SelectItem(index);
        return null;
    }

    private static string? Read(IGH_DocumentObject obj) => obj switch
    {
        GH_NumberSlider slider => slider.CurrentValue.ToString(CultureInfo.InvariantCulture),
        GH_Panel panel => panel.UserText,
        GH_BooleanToggle toggle => toggle.Value ? "true" : "false",
        GH_ColourSwatch swatch => swatch.SwatchColour.ToArgb().ToString("X8", CultureInfo.InvariantCulture),
        GH_ValueList list => string.Join(", ", list.ListItems.Where(i => i.Selected).Select(i => i.Name)),
        _ => null,
    };

    private static string KindOf(IGH_DocumentObject obj) => obj switch
    {
        GH_NumberSlider => "slider",
        GH_Panel => "panel",
        GH_BooleanToggle => "boolean",
        GH_ColourSwatch => "color",
        GH_ValueList => "valuelist",
        _ => obj.GetType().Name,
    };

    private static string? Ok(Action write)
    {
        write();
        return null;
    }
}
