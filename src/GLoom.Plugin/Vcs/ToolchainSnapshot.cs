using System;
using System.Linq;
using GLoom.Serialization;

namespace GLoom.Vcs;

/// <summary>
/// Captures versions of the host runtime at tag time. Failures fall back
/// to "unknown" rather than throwing - the tag operation must succeed
/// even if a sub-component can't be introspected.
/// </summary>
public static class ToolchainSnapshot
{
    public static Toolchain Capture() => new(
        Rhino: SafeRhinoVersion(),
        Grasshopper: AssemblyVersion("Grasshopper"),
        RhinoInsideRevit: AssemblyVersionIfLoaded("RhinoInside.Revit"),
        Gloom: typeof(ToolchainSnapshot).Assembly.GetName().Version?.ToString() ?? "unknown");

    private static string SafeRhinoVersion()
    {
        try { return Rhino.RhinoApp.Version?.ToString() ?? "unknown"; }
        catch { return "unknown"; }
    }

    private static string AssemblyVersion(string simpleName) =>
        AssemblyVersionIfLoaded(simpleName) ?? "unknown";

    private static string? AssemblyVersionIfLoaded(string simpleName)
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(
                    a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
            return asm?.GetName().Version?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
