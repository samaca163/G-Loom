using System;
using System.Collections.Generic;
using System.Linq;

namespace GLoom.Serialization;

[Flags]
public enum ObjectChangeKind
{
    None = 0,
    Renamed = 1 << 0,
    Moved = 1 << 1,
    WiresChanged = 1 << 2,
    PersistentChanged = 1 << 3,
}

public sealed record ObjectChange(
    CanonicalObject From,
    CanonicalObject To,
    ObjectChangeKind Kinds,
    string Summary);

public sealed record GroupChange(
    CanonicalGroup From,
    CanonicalGroup To,
    string Summary);

/// <summary>
/// A computed diff between two <see cref="CanonicalDocument"/> snapshots.
/// Same data feeds both the text inspector (in the per-commit drawer)
/// and the on-canvas overlay (Phase 3.3): consumers iterate the
/// Added / Removed / Modified buckets, look up InstanceGuids, and
/// either render a label or paint a halo. The full From/To objects
/// ride along on each entry so the overlay can read the OLD pivot of
/// a removed node (it isn't on the live canvas anymore) and so future
/// expanded inspectors can drill into specific field deltas without a
/// second round-trip to the source documents.
/// </summary>
public sealed record DocumentDiff(
    bool MetaChanged,
    IReadOnlyList<CanonicalObject> ObjectsAdded,
    IReadOnlyList<CanonicalObject> ObjectsRemoved,
    IReadOnlyList<ObjectChange> ObjectsModified,
    IReadOnlyList<CanonicalGroup> GroupsAdded,
    IReadOnlyList<CanonicalGroup> GroupsRemoved,
    IReadOnlyList<GroupChange> GroupsModified)
{
    public bool IsEmpty =>
        !MetaChanged
        && ObjectsAdded.Count == 0
        && ObjectsRemoved.Count == 0
        && ObjectsModified.Count == 0
        && GroupsAdded.Count == 0
        && GroupsRemoved.Count == 0
        && GroupsModified.Count == 0;

    public int TotalChanges =>
        (MetaChanged ? 1 : 0)
        + ObjectsAdded.Count + ObjectsRemoved.Count + ObjectsModified.Count
        + GroupsAdded.Count + GroupsRemoved.Count + GroupsModified.Count;

    public static DocumentDiff Compute(CanonicalDocument from, CanonicalDocument to)
    {
        var fromObjects = from.Objects.ToDictionary(o => o.InstanceGuid, StringComparer.Ordinal);
        var toObjects = to.Objects.ToDictionary(o => o.InstanceGuid, StringComparer.Ordinal);

        var added = new List<CanonicalObject>();
        var removed = new List<CanonicalObject>();
        var modified = new List<ObjectChange>();

        foreach (var kv in toObjects)
            if (!fromObjects.ContainsKey(kv.Key)) added.Add(kv.Value);
        foreach (var kv in fromObjects)
            if (!toObjects.ContainsKey(kv.Key)) removed.Add(kv.Value);

        foreach (var id in fromObjects.Keys.Intersect(toObjects.Keys, StringComparer.Ordinal))
        {
            var change = DiffObject(fromObjects[id], toObjects[id]);
            if (change != null) modified.Add(change);
        }

        var fromGroups = from.Groups.ToDictionary(g => g.InstanceGuid, StringComparer.Ordinal);
        var toGroups = to.Groups.ToDictionary(g => g.InstanceGuid, StringComparer.Ordinal);

        var groupsAdded = new List<CanonicalGroup>();
        var groupsRemoved = new List<CanonicalGroup>();
        var groupsModified = new List<GroupChange>();

        foreach (var kv in toGroups)
            if (!fromGroups.ContainsKey(kv.Key)) groupsAdded.Add(kv.Value);
        foreach (var kv in fromGroups)
            if (!toGroups.ContainsKey(kv.Key)) groupsRemoved.Add(kv.Value);

        foreach (var id in fromGroups.Keys.Intersect(toGroups.Keys, StringComparer.Ordinal))
        {
            var change = DiffGroup(fromGroups[id], toGroups[id]);
            if (change != null) groupsModified.Add(change);
        }

        var metaChanged = !Equals(from.Document, to.Document);

        added.Sort(ByDisplayName);
        removed.Sort(ByDisplayName);
        modified.Sort((a, b) => string.CompareOrdinal(DisplayName(a.To), DisplayName(b.To)));
        groupsAdded.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        groupsRemoved.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        groupsModified.Sort((a, b) => string.CompareOrdinal(a.To.Name, b.To.Name));

        return new DocumentDiff(
            metaChanged,
            added, removed, modified,
            groupsAdded, groupsRemoved, groupsModified);
    }

    public static string DisplayName(CanonicalObject o) =>
        string.IsNullOrEmpty(o.Nickname) ? o.Name : o.Nickname;

    private static int ByDisplayName(CanonicalObject a, CanonicalObject b) =>
        string.CompareOrdinal(DisplayName(a), DisplayName(b));

    private static ObjectChange? DiffObject(CanonicalObject from, CanonicalObject to)
    {
        var kinds = ObjectChangeKind.None;
        var parts = new List<string>();

        if (from.Name != to.Name || from.Nickname != to.Nickname)
        {
            kinds |= ObjectChangeKind.Renamed;
            parts.Add($"renamed {DisplayName(from)} → {DisplayName(to)}");
        }

        if (!Equals(from.Pivot, to.Pivot))
        {
            kinds |= ObjectChangeKind.Moved;
            parts.Add("moved");
        }

        if (ParamsDiffer(from.Inputs, to.Inputs) || ParamsDiffer(from.Outputs, to.Outputs))
        {
            kinds |= ObjectChangeKind.WiresChanged;
            parts.Add("wires changed");
        }

        if (!Equals(from.Persistent, to.Persistent))
        {
            kinds |= ObjectChangeKind.PersistentChanged;
            parts.Add(SummarizePersistent(from.Persistent, to.Persistent) ?? "value changed");
        }

        return kinds == ObjectChangeKind.None
            ? null
            : new ObjectChange(from, to, kinds, string.Join(", ", parts));
    }

    private static bool ParamsDiffer(
        IReadOnlyList<CanonicalParameter> a,
        IReadOnlyList<CanonicalParameter> b)
    {
        if (a.Count != b.Count) return true;
        for (var i = 0; i < a.Count; i++)
        {
            var ap = a[i];
            var bp = b[i];
            if (ap.InstanceGuid != bp.InstanceGuid) return true;
            if (ap.Sources.Count != bp.Sources.Count) return true;
            for (var j = 0; j < ap.Sources.Count; j++)
                if (ap.Sources[j] != bp.Sources[j]) return true;
        }
        return false;
    }

    private static string? SummarizePersistent(PersistentData? from, PersistentData? to)
    {
        if (from is null && to is null) return null;
        if (from is null) return "value added";
        if (to is null) return "value removed";
        if (from.Kind != to.Kind) return $"kind changed: {from.Kind} → {to.Kind}";

        return to.Kind switch
        {
            "slider" when from.Slider is { } fs && to.Slider is { } ts && fs.Value != ts.Value
                => $"slider {fs.Value} → {ts.Value}",
            "slider" => "slider range changed",
            "panel" => "panel text changed",
            "boolean" => $"toggle {from.BooleanState} → {to.BooleanState}",
            "valuelist" => "selection changed",
            "color" => $"color {from.ColorArgb} → {to.ColorArgb}",
            "data" => "data changed",
            _ => null,
        };
    }

    private static GroupChange? DiffGroup(CanonicalGroup from, CanonicalGroup to)
    {
        var parts = new List<string>();
        if (from.Name != to.Name)
            parts.Add($"renamed {from.Name} → {to.Name}");

        var addedMembers = to.Members.Except(from.Members, StringComparer.Ordinal).Count();
        var removedMembers = from.Members.Except(to.Members, StringComparer.Ordinal).Count();
        if (addedMembers > 0 || removedMembers > 0)
        {
            var bits = new List<string>();
            if (addedMembers > 0) bits.Add($"+{addedMembers} member{Plural(addedMembers)}");
            if (removedMembers > 0) bits.Add($"-{removedMembers} member{Plural(removedMembers)}");
            parts.Add(string.Join(", ", bits));
        }

        return parts.Count == 0
            ? null
            : new GroupChange(from, to, string.Join(", ", parts));
    }

    private static string Plural(int n) => n == 1 ? "" : "s";
}
