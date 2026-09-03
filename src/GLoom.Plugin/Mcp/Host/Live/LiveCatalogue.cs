using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using GLoom.Mcp.Protocol;
using GLoom.Mcp.Tools.Live;

namespace GLoom.Mcp.Host.Live;

/// <summary>The installed component catalogue as plain records. UI thread only.</summary>
internal static class LiveCatalogue
{
    public static IReadOnlyList<CatalogueCategory> Categories() =>
        Instances.ComponentServer.ObjectProxies
            .Where(p => p is not null && p.Exposure != GH_Exposure.hidden && !p.Obsolete)
            .GroupBy(p => p.Desc?.Category ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CatalogueCategory(
                g.Key,
                g.Select(p => p.Desc?.SubCategory ?? string.Empty)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                g.Count()))
            .ToList();

    public static IReadOnlyList<CatalogueEntry> Search(string? query, string? category, bool includeObsolete, int maxResults)
    {
        var server = Instances.ComponentServer;
        var terms = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        IEnumerable<(IGH_ObjectProxy Proxy, double? Score)> candidates;
        if (terms.Length > 0)
        {
            // The search box's own ranking. It scores every proxy whatever the cap and only
            // trims the sorted result, so asking for all of them costs nothing and keeps the
            // category and obsolete filters below from hiding matches behind the cap.
            IGH_ObjectProxy[] results = Array.Empty<IGH_ObjectProxy>();
            double[] weights = Array.Empty<double>();
            server.FindObjects(terms, int.MaxValue, ref results, ref weights);
            results ??= Array.Empty<IGH_ObjectProxy>();
            candidates = results.Select((p, i) =>
                (p, weights is not null && i < weights.Length ? weights[i] : (double?)null));
        }
        else
        {
            // FindObjects and Categories() both skip hidden proxies; a category listing must
            // cover the same set as the count it was quoted.
            candidates = server.ObjectProxies
                .Where(p => p is not null && p.Exposure != GH_Exposure.hidden)
                .Select(p => (p, (double?)null));
        }

        return candidates
            .Where(c => c.Proxy is not null)
            .Where(c => includeObsolete || !c.Proxy.Obsolete)
            .Where(c => string.IsNullOrWhiteSpace(category)
                        || string.Equals(c.Proxy.Desc?.Category, category.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(Math.Max(0, maxResults))
            .Select(c => Entry(server, c.Proxy, c.Score))
            .ToList();
    }

    public static CatalogueDescription Describe(Guid componentGuid)
    {
        var server = Instances.ComponentServer;
        var proxy = server.EmitObjectProxy(componentGuid)
                    ?? throw new ToolArgumentException(
                        $"No installed component with guid {componentGuid}; gloom_catalogue can search by name.");

        var inputs = new List<ParamDescription>();
        var outputs = new List<ParamDescription>();
        string? paramTypeName = null;

        // A throwaway instance is the only way to read a component's parameters; it never
        // joins a document. A plug-in constructor that throws must not take the entry with it.
        IGH_DocumentObject? instance = null;
        string? error = null;
        try { instance = proxy.CreateInstance(); }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        if (instance is null) error ??= "CreateInstance returned nothing";

        try
        {
            switch (instance)
            {
                case IGH_Component component:
                    inputs.AddRange((component.Params?.Input ?? new List<IGH_Param>()).Select(Describe));
                    outputs.AddRange((component.Params?.Output ?? new List<IGH_Param>()).Select(Describe));
                    break;
                case IGH_Param param:
                    paramTypeName = param.TypeName;
                    break;
            }
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }

        var keywords = proxy.Desc?.Keywords?
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToList() ?? new List<string>();

        return new CatalogueDescription(Entry(server, proxy, null), inputs, outputs, paramTypeName, keywords, error);
    }

    private static ParamDescription Describe(IGH_Param param) => new(
        Name: param.Name ?? string.Empty,
        Nickname: param.NickName ?? string.Empty,
        Description: param.Description ?? string.Empty,
        TypeName: param.TypeName ?? string.Empty,
        Access: param.Access.ToString(),
        Optional: param.Optional);

    private static CatalogueEntry Entry(GH_ComponentServer server, IGH_ObjectProxy proxy, double? score)
    {
        var desc = proxy.Desc;
        return new CatalogueEntry(
            ComponentGuid: proxy.Guid.ToString(),
            Name: desc?.Name ?? string.Empty,
            Nickname: desc?.NickName ?? string.Empty,
            Description: desc?.Description ?? string.Empty,
            Category: desc?.Category ?? string.Empty,
            SubCategory: desc?.SubCategory ?? string.Empty,
            Exposure: proxy.Exposure.ToString(),
            Obsolete: proxy.Obsolete,
            Kind: proxy.Kind.ToString(),
            Library: Library(server, proxy.LibraryGuid),
            Score: score);
    }

    private static LibraryInfo? Library(GH_ComponentServer server, Guid libraryId)
    {
        GH_AssemblyInfo? info;
        try { info = server.FindAssembly(libraryId); }
        catch { return null; }
        if (info is null) return null;

        return new LibraryInfo(
            Name: info.Name ?? string.Empty,
            Version: info.Version ?? string.Empty,
            Author: string.IsNullOrWhiteSpace(info.AuthorName) ? null : info.AuthorName,
            IsCore: info.IsCoreLibrary);
    }
}
