using GLoom.Serialization;
using Xunit;
using static GLoom.Core.Tests.Docs;

namespace GLoom.Core.Tests;

public class DocumentDiffTests
{
    [Fact]
    public void Identical_documents_diff_to_nothing()
    {
        var a = Doc("t", Slider(Guid(1), 5), Component(Guid(2), "Circle"));
        var b = Doc("t", Slider(Guid(1), 5), Component(Guid(2), "Circle"));
        var diff = DocumentDiff.Compute(a, b);
        Assert.True(diff.IsEmpty);
        Assert.Equal("Initial commit of t", DiffSummaryText.Headline(null, "t"));
        Assert.Equal("", DiffSummaryText.Body(diff));
    }

    [Fact]
    public void Added_removed_moved_and_value_changes_land_in_their_buckets()
    {
        var from = Doc("t", Slider(Guid(1), 5), Component(Guid(2), "Circle"), Component(Guid(3), "Extrude", 10, 10));
        var to = Doc("t", Slider(Guid(1), 9), Component(Guid(3), "Extrude", 200, 10), Component(Guid(4), "Loft"));

        var diff = DocumentDiff.Compute(from, to);

        Assert.Equal("Loft", Assert.Single(diff.ObjectsAdded).Name);
        Assert.Equal("Circle", Assert.Single(diff.ObjectsRemoved).Name);
        Assert.Equal(2, diff.ObjectsModified.Count);
        var slider = diff.ObjectsModified.Single(c => c.To.InstanceGuid == Guid(1));
        Assert.True(slider.Kinds.HasFlag(ObjectChangeKind.PersistentChanged));
        Assert.Contains("5", slider.Summary);
        Assert.Contains("9", slider.Summary);
        var extrude = diff.ObjectsModified.Single(c => c.To.InstanceGuid == Guid(3));
        Assert.Equal(ObjectChangeKind.Moved, extrude.Kinds);

        var body = DiffSummaryText.Body(diff);
        Assert.Contains("Added:", body);
        Assert.Contains("Loft", body);
        Assert.Contains("Removed:", body);
        Assert.Contains("Circle", body);
        Assert.StartsWith("Added Loft", DiffSummaryText.Headline(diff, "t"));
    }

    [Fact]
    public void A_rewired_input_is_a_wire_change()
    {
        var src = Slider(Guid(1), 5);
        var other = Slider(Guid(5), 7);
        var before = Component(Guid(2), "Circle", 0, 0, Input(Guid(3), "R", Guid(1)));
        var after = Component(Guid(2), "Circle", 0, 0, Input(Guid(3), "R", Guid(5)));

        var diff = DocumentDiff.Compute(Doc("t", src, other, before), Doc("t", src, other, after));

        var change = Assert.Single(diff.ObjectsModified);
        Assert.True(change.Kinds.HasFlag(ObjectChangeKind.WiresChanged));
    }

    [Fact]
    public void Canonical_json_round_trips_and_tolerates_garbage()
    {
        var doc = Doc("t", Slider(Guid(1), 5), Component(Guid(2), "Circle", 1.5f, -2f));
        var json = CanonicalJson.Write(doc);
        Assert.Contains("\"schemaVersion\": 6", json);
        // Records compare list fields by reference, so equality is asserted on the text.
        Assert.Equal(json, CanonicalJson.Write(CanonicalJson.TryParse(json)!));
        Assert.Null(CanonicalJson.TryParse("not json"));
        Assert.Null(CanonicalJson.TryParse(""));
    }
}
