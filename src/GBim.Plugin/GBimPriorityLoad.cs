using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using GBim.Canvas;
using GBim.Ui;
using GBim.Vcs;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Rhino;

namespace GBim;

public sealed class GBimPriorityLoad : GH_AssemblyPriority
{
    public override GH_LoadingInstruction PriorityLoad()
    {
        ConfigureLibGit2SharpNativePath();

        DocumentTracker.Instance.Initialize();
        GBimPanelHost.Register();

        Instances.CanvasCreated += OnCanvasCreated;
        if (Instances.ActiveCanvas != null)
            OnCanvasCreated(Instances.ActiveCanvas);

        RhinoApp.WriteLine("[G-BIM] Plugin loaded.");
        return GH_LoadingInstruction.Proceed;
    }

    private static bool _panelAutoOpened;

    private static void OnCanvasCreated(GH_Canvas canvas)
    {
        DiffOverlayPainter.Attach(canvas);

        // Auto-open the panel once on first canvas creation. After that the user
        // can close/dock/move it like any other Rhino panel, and re-open via the
        // `GBimPanel` Rhino command. Schedule on the Eto UI thread to be safe.
        if (_panelAutoOpened) return;
        _panelAutoOpened = true;
        try { Eto.Forms.Application.Instance.AsyncInvoke(GBimPanelHost.Open); }
        catch { GBimPanelHost.Open(); }
    }

    private static bool _nativeResolverInstalled;

    /// <summary>
    /// Bootstraps native resolution for LibGit2Sharp. Order is critical:
    /// install the DllImport resolver BEFORE any LibGit2Sharp managed code
    /// runs, because if NativeMethods' static ctor fires once (with the wrong
    /// search path) and throws, .NET caches that TypeInitializationException
    /// forever and replays it without consulting any later-installed resolver.
    /// We deliberately do NOT touch GlobalSettings.NativeLibraryPath - on
    /// macOS it does nothing useful, and its setter has historically been a
    /// trigger for premature NativeMethods initialization.
    /// </summary>
    private static void ConfigureLibGit2SharpNativePath()
    {
        try
        {
            var asmDir = Path.GetDirectoryName(typeof(GBimPriorityLoad).Assembly.Location);
            if (string.IsNullOrEmpty(asmDir)) return;

            var rid = ResolveRid();
            if (rid is null)
            {
                RhinoApp.WriteLine("[G-BIM] Could not resolve runtime identifier.");
                return;
            }

            var nativeDir = Path.Combine(asmDir, "runtimes", rid, "native");
            if (!Directory.Exists(nativeDir))
            {
                RhinoApp.WriteLine($"[G-BIM] Native dir missing: {nativeDir}");
                return;
            }

            var ext = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll"
                    : ".so";

            // STEP 1: Install the DllImport resolver FIRST. Use Assembly.Load
            // so we touch metadata only - no class static ctors fire.
            if (!_nativeResolverInstalled)
            {
                Assembly? lg2Asm;
                try { lg2Asm = Assembly.Load("LibGit2Sharp"); }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[G-BIM] Could not load LibGit2Sharp metadata: {ex.Message}");
                    return;
                }

                try
                {
                    NativeLibrary.SetDllImportResolver(lg2Asm,
                        (libName, _, _) =>
                        {
                            RhinoApp.WriteLine($"[G-BIM] DllImport resolver called for: '{libName}'");

                            foreach (var candidate in new[]
                                     {
                                         libName,
                                         "lib" + libName,
                                         libName + ext,
                                         "lib" + libName + ext,
                                     })
                            {
                                var full = Path.Combine(nativeDir, candidate);
                                if (File.Exists(full))
                                {
                                    try
                                    {
                                        var h = NativeLibrary.Load(full);
                                        RhinoApp.WriteLine($"[G-BIM]   resolved '{libName}' -> {candidate} (0x{h:X})");
                                        return h;
                                    }
                                    catch (Exception loadEx)
                                    {
                                        RhinoApp.WriteLine($"[G-BIM]   tried {candidate}: {loadEx.Message}");
                                    }
                                }
                            }

                            RhinoApp.WriteLine($"[G-BIM]   NO MATCH for '{libName}' under {nativeDir}");
                            return IntPtr.Zero;
                        });
                    _nativeResolverInstalled = true;
                    RhinoApp.WriteLine($"[G-BIM] DllImport resolver installed on {lg2Asm.GetName().Name}");
                }
                catch (InvalidOperationException ex)
                {
                    RhinoApp.WriteLine($"[G-BIM] DllImport resolver NOT installed (already set): {ex.Message}");
                }
            }

            // STEP 2: Pre-load the native dylib. Belt-and-suspenders - the
            // resolver alone should be enough, but pre-loading guarantees the
            // dylib is in the process even if the resolver path has a quirk.
            var dylib = Directory.EnumerateFiles(nativeDir, "*git2*" + ext)
                .Where(f => !Path.GetFileName(f).Contains(".1.", StringComparison.Ordinal))
                .FirstOrDefault();
            if (dylib is not null)
            {
                try
                {
                    var handle = NativeLibrary.Load(dylib);
                    RhinoApp.WriteLine($"[G-BIM] Pre-loaded {Path.GetFileName(dylib)} (handle 0x{handle:X})");
                }
                catch (Exception loadEx)
                {
                    RhinoApp.WriteLine($"[G-BIM] Pre-load failed: {loadEx.Message}");
                }
            }

            RhinoApp.WriteLine($"[G-BIM] Native setup complete; native dir = {nativeDir}");
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[G-BIM] LibGit2Sharp native path setup failed: {ex.Message}");
        }
    }

    private static string? ResolveRid()
    {
        // ProcessArchitecture (not OSArchitecture) - matters under Rosetta where
        // an x64 .NET process can run on an arm64 host.
        var arch = RuntimeInformation.ProcessArchitecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return arch == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return arch == Architecture.Arm64 ? "win-arm64" : "win-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return arch == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        return null;
    }
}
