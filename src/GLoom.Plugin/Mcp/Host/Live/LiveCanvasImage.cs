using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Live;

namespace GLoom.Mcp.Host.Live;

/// <summary>
/// Pixels of the active canvas as a PNG. UI thread only, and never from inside a paint
/// event: this runs from <c>UiThread.Run</c> between paints and refuses while one is in
/// progress.
/// </summary>
internal static class LiveCanvasImage
{
    private const float Margin = 40f;
    private const double MaxZoom = 2.0;
    private const int MaxPngBytes = 3 * 1024 * 1024;
    private const int MinPixels = 200;

    public static CanvasImage Capture(
        GH_Document doc, ImageRegion region, IReadOnlyList<string>? instanceGuids, string? query, int maxWidth, int maxHeight)
    {
        if (maxWidth < 1 || maxHeight < 1)
            throw new ToolArgumentException("maxWidth and maxHeight must be at least 1 pixel.");

        var canvas = Instances.ActiveCanvas
                     ?? throw new ToolArgumentException("No Grasshopper canvas is open.");
        if (!ReferenceEquals(canvas.Document, doc))
            throw new ToolArgumentException("Only the active document can be imaged; activate its tab in Grasshopper.");
        if (canvas.Painting)
            throw new InvalidOperationException("The canvas is painting; retry.");

        Bitmap? bitmap = null;
        try
        {
            RectangleF frame;
            if (region == ImageRegion.Visible)
            {
                // GetCanvasScreenBuffer draws into a bitmap made for the call, not into the
                // canvas's own back buffer, so it is ours to dispose.
                bitmap = canvas.GetCanvasScreenBuffer(GH_CanvasMode.Control)
                         ?? throw new InvalidOperationException(
                             "Grasshopper returned no canvas image; is the Grasshopper window visible?");
                frame = canvas.Viewport.VisibleRegion;
            }
            else
            {
                var objects = region == ImageRegion.All
                    ? doc.Objects.ToList()
                    : Match(doc, instanceGuids, query);
                var union = Union(objects)
                            ?? throw new ToolArgumentException("There are no objects with a position to frame.");
                frame = RectangleF.Inflate(union, Margin, Margin);

                var zoom = Math.Min(Math.Min(maxWidth / (double)frame.Width, maxHeight / (double)frame.Height), MaxZoom);
                var pixelWidth = Math.Clamp((int)Math.Round(frame.Width * zoom), 1, maxWidth);
                var pixelHeight = Math.Clamp((int)Math.Round(frame.Height * zoom), 1, maxHeight);
                bitmap = canvas.GenerateHiResImageTile(Framing(frame, pixelWidth, pixelHeight, zoom), Color.White)
                         ?? throw new InvalidOperationException("Grasshopper returned no canvas image.");
            }

            if (bitmap.Width > maxWidth || bitmap.Height > maxHeight)
            {
                var scale = Math.Min(maxWidth / (double)bitmap.Width, maxHeight / (double)bitmap.Height);
                bitmap = Replace(bitmap, Resize(bitmap,
                    Math.Max(1, (int)Math.Floor(bitmap.Width * scale)),
                    Math.Max(1, (int)Math.Floor(bitmap.Height * scale))));
            }

            var png = EncodePng(bitmap);
            while (png.Length > MaxPngBytes && bitmap.Width / 2 >= MinPixels && bitmap.Height / 2 >= MinPixels)
            {
                bitmap = Replace(bitmap, Resize(bitmap, bitmap.Width / 2, bitmap.Height / 2));
                png = EncodePng(bitmap);
            }

            var inFrame = doc.Objects.Count(o => o.Attributes is { } a && a.Bounds.IntersectsWith(frame));
            var effectiveZoom = frame.Width > 0 ? bitmap.Width / (double)frame.Width : 0;
            return new CanvasImage(
                png, bitmap.Width, bitmap.Height, effectiveZoom,
                frame.X, frame.Y, frame.Width, frame.Height, inFrame);
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private static GH_Viewport Framing(RectangleF frame, int pixelWidth, int pixelHeight, double zoom)
    {
        // Target is the pixel the canvas origin is drawn at (VisibleRegion = -Target / Zoom),
        // so aiming at the frame's centre is one translation; being integer it can leave the
        // centre half a pixel off, which at small zooms is more than a canvas unit.
        var centre = new PointF(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f);
        var vp = new GH_Viewport { Size = new Size(pixelWidth, pixelHeight), Zoom = (float)zoom };
        vp.Target = new Point(
            (int)Math.Round(pixelWidth / 2.0 - centre.X * zoom),
            (int)Math.Round(pixelHeight / 2.0 - centre.Y * zoom));
        vp.ComputeProjection();
        return vp;
    }

    private static List<IGH_DocumentObject> Match(GH_Document doc, IReadOnlyList<string>? instanceGuids, string? query)
    {
        var ids = new HashSet<Guid>();
        foreach (var s in instanceGuids ?? Array.Empty<string>())
            if (Guid.TryParse(s, out var id)) ids.Add(id);
        var needle = query?.Trim();
        var byName = !string.IsNullOrEmpty(needle);

        var matched = doc.Objects
            .Where(o => ids.Contains(o.InstanceGuid)
                        || (byName && ((o.Name ?? string.Empty).Contains(needle!, StringComparison.OrdinalIgnoreCase)
                                       || (o.NickName ?? string.Empty).Contains(needle!, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (matched.Count == 0)
            throw new ToolArgumentException(
                "No objects match the given instance guids or query; gloom_read_document lists the objects with their instance guids.");
        return matched;
    }

    private static RectangleF? Union(IEnumerable<IGH_DocumentObject> objects)
    {
        RectangleF? union = null;
        foreach (var o in objects)
        {
            if (o.Attributes is null) continue;
            var b = o.Attributes.Bounds;
            union = union is { } u ? RectangleF.Union(u, b) : b;
        }
        return union;
    }

    private static Bitmap Replace(Bitmap old, Bitmap fresh)
    {
        old.Dispose();
        return fresh;
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(target);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.White);
        g.DrawImage(source, new Rectangle(0, 0, width, height),
            new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        return target;
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
