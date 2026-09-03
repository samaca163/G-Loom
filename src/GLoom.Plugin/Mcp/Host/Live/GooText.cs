using System;
using System.Collections.Generic;
using Grasshopper.Kernel.Types;
using GLoom.Mcp.Tools.Live;
using Rhino.Geometry;

namespace GLoom.Mcp.Host.Live;

/// <summary>
/// Turns one <see cref="IGH_Goo"/> into the plain text an agent reads. Goo from third-party
/// libraries can throw from ToString, IsValid and Boundingbox; one bad item must not abort a
/// read of thousands, so those calls are guarded and nothing else is.
/// </summary>
internal static class GooText
{
    public const int PreviewLength = 80;
    private const string Ellipsis = "…";

    public static DataItem Item(IGH_Goo? goo, int maxTextLength)
    {
        if (goo is null) return new DataItem("null", "null");

        var text = Cut(Describe(goo), maxTextLength);
        if (goo is IGH_GeometricGoo geometric && TryBounds(geometric, out var box))
            return new DataItem(TypeName(goo), text, Xyz(box.Min), Xyz(box.Max));

        return new DataItem(TypeName(goo), text);
    }

    public static string Preview(IGH_Goo? goo) =>
        goo is null ? "null" : Cut(Describe(goo).Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '), PreviewLength);

    private static string TypeName(IGH_Goo goo) => goo.TypeName ?? goo.GetType().Name;

    private static string Describe(IGH_Goo goo)
    {
        try
        {
            var text = (goo.ToString() ?? string.Empty).Trim();
            if (!goo.IsValid)
                text += " (invalid: " + (goo.IsValidWhyNot ?? string.Empty) + ")";
            return text;
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ">";
        }
    }

    private static string Cut(string text, int max)
    {
        if (max < 1) max = 1;
        return text.Length <= max ? text : text.Substring(0, max) + Ellipsis;
    }

    private static bool TryBounds(IGH_GeometricGoo geometric, out BoundingBox box)
    {
        try
        {
            box = geometric.Boundingbox;
            return box.IsValid;
        }
        catch
        {
            box = BoundingBox.Unset;
            return false;
        }
    }

    private static IReadOnlyList<double> Xyz(Point3d p) =>
        new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4) };
}
