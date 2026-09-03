using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GLoom.Mcp.Tools.Live;
using GLoom.Serialization;
using GLoom.Ui;
using Grasshopper.Kernel;

namespace GLoom.Mcp.Host.Live;

/// <summary>
/// Putting recorded objects back, for an agent rejecting part of a change rather than a
/// person right-clicking a ghost. The primitives are the overlay's, so both routes behave
/// identically; this only decides, per object, whether it needs resetting or recreating.
/// </summary>
internal static class LiveRestore
{
    public static IReadOnlyList<RestoredObject> Apply(
        GH_Document doc, IReadOnlyList<CanonicalObject> objects)
    {
        var results = new List<RestoredObject>(objects.Count);
        var live = doc.Objects.ToDictionary(o => o.InstanceGuid);

        doc.UndoUtil.RecordEvent("G-Loom: restore objects");

        foreach (var recorded in objects)
        {
            var target = DocumentDiff.DisplayName(recorded);
            if (!Guid.TryParse(recorded.InstanceGuid, out var id))
            {
                results.Add(new RestoredObject(target, false, Reason: "The recorded object has no readable identity."));
                continue;
            }

            try
            {
                if (live.TryGetValue(id, out var obj))
                {
                    var reason = DocumentRestore.ApplyPersistent(obj, recorded.Persistent);
                    if (reason is not null)
                    {
                        results.Add(new RestoredObject(target, false, recorded.InstanceGuid, recorded.Name, Reason: reason));
                        continue;
                    }

                    var moved = false;
                    if (obj.Attributes is not null)
                    {
                        var to = new PointF(recorded.Pivot.X, recorded.Pivot.Y);
                        moved = obj.Attributes.Pivot != to;
                        obj.Attributes.Pivot = to;
                        obj.Attributes.ExpireLayout();
                    }

                    obj.ExpireSolution(false);
                    results.Add(new RestoredObject(
                        target, true, recorded.InstanceGuid, recorded.Name,
                        recorded.Persistent is not null
                            ? (moved ? "value and position reset" : "value reset")
                            : (moved ? "position reset" : "already as recorded")));
                }
                else
                {
                    DocumentRestore.RestoreDeleted(doc, recorded);
                    // RestoreDeleted logs and returns when the component type is not installed,
                    // so confirm against the document rather than trusting the call.
                    var back = doc.Objects.Any(o => o.InstanceGuid == id);
                    results.Add(new RestoredObject(
                        target, back, recorded.InstanceGuid, recorded.Name,
                        back ? "recreated" : null,
                        back ? null : "That component type is not installed in this Rhino, so it could not be recreated."));
                }
            }
            catch (Exception ex)
            {
                results.Add(new RestoredObject(target, false, recorded.InstanceGuid, recorded.Name, Reason: ex.Message));
            }
        }

        return results;
    }
}
