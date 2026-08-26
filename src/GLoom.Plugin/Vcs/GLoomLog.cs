using System;

namespace GLoom.Vcs;

/// <summary>
/// The one place host-free code writes a log line. The plugin points <see cref="Sink"/>
/// at Rhino's command line at load; the test project, which links these sources without
/// Rhino, leaves the default. Messages carry their own "[G-Loom]" prefix so the command
/// line reads the same as every direct RhinoApp.WriteLine in the host-bound code.
/// </summary>
public static class GLoomLog
{
    public static Action<string> Sink { get; set; } = m => Console.Error.WriteLine(m);

    public static void Write(string message)
    {
        try { Sink(message); }
        catch { /* logging must never take the caller down */ }
    }
}
