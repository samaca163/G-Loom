using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Memory;

namespace GLoom.Mcp.Tools.Live;

/// <summary>
/// Setting values on the canvas - the one kind of edit G-Loom makes itself, because it is the
/// kind it can checkpoint, diff and revert exactly. Refused unless an edit envelope is open,
/// so there is always a version to undo it from.
/// </summary>
public static class ValueTools
{
    private const int MaxEdits = 100;

    public static void Register(McpDispatcher d, ILiveHost host, Func<LiveSnapshot?> live)
    {
        d.Register(new McpTool(
            "gloom_set_value",
            "Set values on the live canvas: sliders, panels, boolean toggles, value lists and colour swatches. " +
            "Give one edit or many; they are applied as a single undoable step, and by default the definition is " +
            "recomputed once afterwards. Each result reports the value before and after, so you can say in the " +
            "commit what actually changed. An edit that cannot be applied is reported on its own and the rest " +
            "still land. Requires an open edit envelope (gloom_begin_edit), so the change always has a checkpoint " +
            "to be undone from. To add, remove or rewire components, use Rhino's own MCP server - G-Loom does not " +
            "author graphs.",
            Schema.Object()
                .String("file", LiveTools.OpenFileArgDescription)
                .Raw("edits", new JsonObject
                {
                    ["type"] = "array",
                    ["description"] =
                        "The values to set. Each entry names an object and the value to give it: a number for a " +
                        "slider, text for a panel, true/false for a toggle, an item name for a value list, and " +
                        "AARRGGBB or RRGGBB hex (or a colour name) for a swatch.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["object"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] =
                                    "Which object: an instanceGuid, or its exact name or nickname. gloom_read_document lists them.",
                            },
                            ["value"] = new JsonObject
                            {
                                ["description"] = "The value to set, as text, a number or true/false.",
                            },
                        },
                        ["required"] = new JsonArray { "object", "value" },
                        ["additionalProperties"] = false,
                    },
                }, required: true)
                .Boolean("solve", "Recompute the definition once after applying (default true).")
                .Build(),
            ToolAccess.Write,
            (args, _) => SetValues(
                host, Args.String(args, "file"), Args.Array(args, "edits"),
                Args.Bool(args, "solve", true), live())));
    }

    public static ToolResult SetValues(
        ILiveHost host, string? file, JsonArray? rawEdits, bool solve, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        if (EnvelopeTools.RequireOpen(f) is { } refusal) return ToolResult.Error(refusal);

        var edits = Parse(rawEdits);

        return LiveTools.Guard(() =>
        {
            var results = host.SetValues(file, edits, solve);
            var applied = results.Count(r => r.Applied);
            var failed = results.Count - applied;

            return ToolResult.Json(new
            {
                file,
                applied,
                failed,
                solved = solve && applied > 0,
                edits = results.Select(r => new
                {
                    r.Target, r.Applied, r.InstanceGuid, r.Name, r.Nickname, r.Kind, r.Before, r.After, r.Reason,
                }).ToList(),
                note = failed == 0
                    ? "All set. The person watching Rhino sees these highlighted against the checkpoint; " +
                      "gloom_end_edit commits them with your intent."
                    : $"{applied} applied, {failed} not - see each edit's reason. The ones that landed are on the " +
                      "canvas and still inside the envelope, so gloom_end_edit with \"discard\" undoes them all.",
            });
        });
    }

    private static IReadOnlyList<ValueEdit> Parse(JsonArray? raw)
    {
        if (raw is null || raw.Count == 0)
            throw new ToolArgumentException(
                "\"edits\" is required: a list of { \"object\": ..., \"value\": ... } entries.");
        if (raw.Count > MaxEdits)
            throw new ToolArgumentException($"\"edits\" holds {raw.Count} entries; {MaxEdits} at a time is the limit.");

        var edits = new List<ValueEdit>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            if (raw[i] is not JsonObject entry)
                throw new ToolArgumentException($"edits[{i}] must be an object with \"object\" and \"value\".");

            var target = Args.String(entry, "object");
            if (string.IsNullOrWhiteSpace(target))
                throw new ToolArgumentException($"edits[{i}].object is required: an instanceGuid, name or nickname.");

            edits.Add(new ValueEdit(target.Trim(), Args.Scalar(entry["value"], $"edits[{i}].value")));
        }
        return edits;
    }
}
