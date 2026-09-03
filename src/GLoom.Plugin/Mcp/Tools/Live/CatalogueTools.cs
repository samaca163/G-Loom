using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GLoom.Mcp.Protocol;

namespace GLoom.Mcp.Tools.Live;

/// <summary>
/// The installed-component catalogue and the canvas screenshot: the two live reads that are
/// about the host rather than a document's data. Registered through <see cref="LiveTools.Register"/>.
/// </summary>
public static class CatalogueTools
{
    private const int DefaultLimit = 25, MaxLimit = 100;
    private const int DefaultWidth = 1600, DefaultHeight = 1200, MinSide = 200, MaxSide = 4000;

    private static readonly string[] Regions = { "visible", "all", "objects" };

    private static readonly JsonSerializerOptions CompactJson = new(ToolJson.Options) { WriteIndented = false };

    public static void Register(McpDispatcher d, ILiveHost host)
    {
        d.Register(new McpTool(
            "gloom_catalogue",
            "The components and parameters installed in THIS Rhino, core and plug-ins alike, to find the " +
            "right one before placing it; the componentGuid is what placement tools take. Three modes: " +
            "\"query\" runs Grasshopper's own fuzzy search (the one behind the canvas search box), " +
            "\"category\" lists a whole category (e.g. \"Params\", \"Curve\") alphabetically, paged by offset and " +
            "limit (combine with query to narrow instead), and \"describe\" with a componentGuid returns that one " +
            "component in full with its inputs and outputs. With none of them, the categories and their counts " +
            "are listed. Obsolete components are hidden unless includeObsolete is true; query results are ranked " +
            "and capped at limit (default 25, max 100).",
            Schema.Object()
                .String("query", "Search terms for Grasshopper's fuzzy component search (name, nickname, keywords).")
                .String("category", "Exact category name, case-insensitive (e.g. \"Params\", \"Curve\", \"Surface\").")
                .Boolean("includeObsolete", "Include obsolete components (default false).")
                .Integer("offset", "Index of the first component of a category listing to return (default 0); " +
                                   "query results are ranked, not paged.", min: 0)
                .Integer("limit", "Maximum components to return (default 25, max 100).", min: 1, max: MaxLimit)
                .String("describe", "A componentGuid: return the full description of that one component instead of searching.")
                .Build(),
            ToolAccess.Read,
            (args, _) => Catalogue(host,
                Args.String(args, "query"), Args.String(args, "category"), Args.Bool(args, "includeObsolete", false),
                Args.Int(args, "limit", DefaultLimit), Args.String(args, "describe"), Args.Int(args, "offset", 0))));

        d.Register(new McpTool(
            "gloom_canvas_image",
            "A PNG of the ACTIVE definition's canvas (only the active tab can be captured): what the user sees " +
            "(region \"visible\"), every object framed (\"all\"), or a frame around chosen objects (\"objects\", " +
            "named by comma-separated instanceGuids in \"objects\" or matched by \"query\"). Use it to read " +
            "layout, groups, colours and annotations that the recipe does not carry. The image is capped at " +
            "maxWidth x maxHeight (default 1600 x 1200, min 200, max 4000) and comes with a text summary of its " +
            "pixel size, zoom and the canvas region it covers.",
            Schema.Object()
                .String("file", "Path of the ACTIVE definition, to assert which one is imaged; another open definition " +
                                "is refused until its tab is activated in Grasshopper. Omit for the active document.")
                .Enum("region", "\"visible\": the current view (the default when neither \"objects\" nor \"query\" is " +
                                "given); \"all\": every object; \"objects\": the objects named by \"objects\" or matched " +
                                "by \"query\" (the default when either is given).", Regions)
                .String("objects", "Comma-separated instanceGuids to frame (region \"objects\").")
                .String("query", "Case-insensitive name or nickname substring of the objects to frame (region \"objects\").")
                .Integer("maxWidth", "Maximum image width in pixels (default 1600, min 200, max 4000).", min: MinSide, max: MaxSide)
                .Integer("maxHeight", "Maximum image height in pixels (default 1200, min 200, max 4000).", min: MinSide, max: MaxSide)
                .Build(),
            ToolAccess.Read,
            (args, _) => CanvasImage(host,
                Args.String(args, "file"), Args.String(args, "region"), Args.String(args, "objects"), Args.String(args, "query"),
                Args.Int(args, "maxWidth", DefaultWidth), Args.Int(args, "maxHeight", DefaultHeight))));
    }

    public static ToolResult Catalogue(
        ILiveHost host, string? query, string? category, bool includeObsolete, int limit, string? describe, int offset = 0)
    {
        var q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var c = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);

        if (!string.IsNullOrWhiteSpace(describe))
        {
            if (!Guid.TryParse(describe.Trim(), out var guid))
                throw new ToolArgumentException($"\"describe\" must be a componentGuid (got \"{describe}\"); gloom_catalogue with \"query\" finds it.");
            return LiveTools.Guard(() =>
            {
                var d = host.Describe(guid);
                return ToolResult.Json(new
                {
                    component = d.Entry,
                    inputs = d.Inputs,
                    outputs = d.Outputs,
                    paramTypeName = d.ParamTypeName,
                    keywords = d.Keywords,
                    instantiationError = d.InstantiationError,
                    note = d.InstantiationError is not null
                        ? $"The component could not be instantiated to read its parameters ({d.InstantiationError}); " +
                          "inputs and outputs are unknown, not empty."
                        : d.ParamTypeName is null
                            ? "Inputs and outputs are the component's defaults; a placed instance can have more (variable-parameter components) or fewer."
                            : "This is a parameter, not a component: it holds data of paramTypeName and has no inputs or outputs of its own.",
                });
            });
        }

        if (q is null && c is null)
            return LiveTools.Guard(() => ToolResult.Json(new
            {
                categories = host.Categories(),
                note = "Pass \"query\" (Grasshopper's fuzzy search) or \"category\" to list components, or \"describe\" " +
                       "with a componentGuid for one component's inputs and outputs.",
            }));

        const string describeHint = "Pass a componentGuid as \"describe\" for a component's inputs and outputs before placing it.";

        if (q is null)
            return LiveTools.Guard(() =>
            {
                // A category is listed whole and paged here: the host returns proxies in load
                // order, so a capped fetch would drop an arbitrary subset behind the alphabet.
                var all = host.Search(null, c, includeObsolete, int.MaxValue)
                    .OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.SubCategory, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var page = all.Skip(offset).Take(limit).ToList();
                var hasMore = offset + page.Count < all.Count;
                var nextOffset = hasMore ? offset + page.Count : (int?)null;

                return ToolResult.Json(new
                {
                    query = q,
                    category = c,
                    includeObsolete,
                    total = all.Count,
                    returned = page.Count,
                    page = new { offset, limit, returned = page.Count, hasMore, nextOffset },
                    components = page,
                    note = all.Count == 0
                        ? "Nothing matched; check the category name with a call without arguments, or set includeObsolete."
                        : (hasMore ? $"More follow; pass offset={nextOffset} for the next page. " : "") + describeHint,
                });
            });

        return LiveTools.Guard(() =>
        {
            var found = host.Search(q, c, includeObsolete, limit + 1);
            var truncated = found.Count > limit;
            var components = found
                .OrderByDescending(e => e.Score ?? 0)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();

            return ToolResult.Json(new
            {
                query = q,
                category = c,
                includeObsolete,
                returned = components.Count,
                truncated,
                components,
                note = components.Count == 0
                    ? "Nothing matched; try fewer or different terms, check the category name with a call without arguments, or set includeObsolete."
                    : (truncated ? "More matched than limit; raise limit or narrow the terms. " : "") + describeHint,
            });
        });
    }

    public static ToolResult CanvasImage(ILiveHost host, string? file, string? region, string? objects, string? query, int maxWidth, int maxHeight)
    {
        var guids = string.IsNullOrWhiteSpace(objects) ? null
            : objects.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (guids is { Count: 0 }) guids = null;
        var q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        var r = !string.IsNullOrWhiteSpace(region) ? region.Trim().ToLowerInvariant()
            : guids is null && q is null ? "visible"
            : "objects";
        if (!Regions.Contains(r, StringComparer.Ordinal))
            throw new ToolArgumentException($"\"region\" must be \"visible\", \"all\" or \"objects\" (got \"{region}\").");
        var imageRegion = r switch { "all" => ImageRegion.All, "objects" => ImageRegion.Objects, _ => ImageRegion.Visible };

        if (imageRegion == ImageRegion.Objects && guids is null && q is null)
            throw new ToolArgumentException("region \"objects\" needs \"objects\" (comma-separated instanceGuids) or \"query\" to say which objects to frame.");
        if (imageRegion != ImageRegion.Objects && (guids is not null || q is not null))
            throw new ToolArgumentException($"region \"{r}\" ignores \"objects\" and \"query\"; use region \"objects\", or omit region, to frame them.");

        maxWidth = Math.Clamp(maxWidth, MinSide, MaxSide);
        maxHeight = Math.Clamp(maxHeight, MinSide, MaxSide);

        return LiveTools.Guard(() =>
        {
            var img = host.CanvasImage(file, imageRegion, guids, q, maxWidth, maxHeight);
            var summary = new
            {
                file,
                mode = r,
                img.PixelWidth,
                img.PixelHeight,
                img.Zoom,
                region = new { x = img.RegionX, y = img.RegionY, width = img.RegionWidth, height = img.RegionHeight },
                img.ObjectsInFrame,
                bytes = img.Png.Length,
            };
            var result = ToolResult.Image(img.Png);
            result.Content.Insert(0, new ToolContent("text", JsonSerializer.Serialize(summary, CompactJson)));
            return result;
        });
    }
}
