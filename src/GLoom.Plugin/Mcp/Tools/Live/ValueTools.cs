using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.State;
using GLoom.Mcp.Tools.Memory;
using GLoom.Serialization;

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

        d.Register(new McpTool(
            "gloom_restore_objects",
            "Put named objects back the way a previous version had them, leaving everything else alone - the way a " +
            "person rejects one change by right-clicking it on the canvas, rather than reverting the whole " +
            "definition. An object that still exists has its value and position reset; one that was deleted is " +
            "recreated with its original identity and its wires reconnected where the sources still exist. Name " +
            "objects by instanceGuid, name or nickname, comma-separated; omit \"objects\" to restore everything " +
            "that differs from that version. Requires an open edit envelope.",
            Schema.Object()
                .String("file", ProjectLocator.FileArgDescription)
                .String("version", VersionRef.ArgDescription + " Default: the checkpoint of the open envelope.")
                .String("objects", "Comma-separated instanceGuids, names or nicknames. Omit to restore every object that differs.")
                .Boolean("solve", "Recompute the definition once after restoring (default true).")
                .Build(),
            ToolAccess.Write,
            (args, _) => RestoreObjects(
                host, Args.String(args, "file"), Args.String(args, "version"), Args.String(args, "objects"),
                Args.Bool(args, "solve", true), live())));
    }

    public static ToolResult RestoreObjects(
        ILiveHost host, string? file, string? version, string? objects, bool solve, LiveSnapshot? live)
    {
        var f = ProjectLocator.Locate(file, live);
        if (EnvelopeTools.RequireOpen(f) is { } refusal) return ToolResult.Error(refusal);

        // Default to the envelope's own checkpoint: "put back what I changed" is the common
        // case, and it is the version the human is already seeing highlighted on canvas.
        var reference = string.IsNullOrWhiteSpace(version)
            ? EnvelopeStore.Current!.CheckpointSha
            : version;

        var resolved = VersionRef.Resolve(f, reference, VersionRef.Working);
        var recipe = VersionRef.LoadRecipe(f, resolved);

        var wanted = Split(objects);
        var chosen = wanted.Count == 0
            ? recipe.Document.Objects
            : Select(recipe.Document.Objects, wanted);

        if (chosen.Count == 0)
            throw new ToolArgumentException(
                $"None of those objects are in {resolved.Label}. gloom_read_version lists what it holds.");

        return LiveTools.Guard(() =>
        {
            var results = host.RestoreObjects(file, chosen, solve);
            var restored = results.Count(r => r.Restored);

            return ToolResult.Json(new
            {
                file,
                version = resolved.Label,
                sha = resolved.Sha,
                restored,
                failed = results.Count - restored,
                objects = results.Select(r => new { r.Target, r.Restored, r.InstanceGuid, r.Name, r.Action, r.Reason }).ToList(),
                note = "Restored on the canvas but not committed: the envelope is still open, so gloom_end_edit " +
                       "commits the result, or discards everything back to the checkpoint.",
            });
        });
    }

    private static IReadOnlyList<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? System.Array.Empty<string>()
            : csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static IReadOnlyList<CanonicalObject> Select(
        IReadOnlyList<CanonicalObject> objects, IReadOnlyList<string> wanted)
    {
        var chosen = new List<CanonicalObject>();
        foreach (var w in wanted)
        {
            var hit = objects.FirstOrDefault(o =>
                string.Equals(o.InstanceGuid, w, StringComparison.OrdinalIgnoreCase)
                || string.Equals(o.Nickname, w, StringComparison.OrdinalIgnoreCase)
                || string.Equals(o.Name, w, StringComparison.OrdinalIgnoreCase));
            if (hit is not null && !chosen.Contains(hit)) chosen.Add(hit);
        }
        return chosen;
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
