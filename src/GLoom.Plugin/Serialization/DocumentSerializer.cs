using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;

namespace GLoom.Serialization;

/// <summary>
/// Walks a live <see cref="GH_Document"/> and emits a <see cref="CanonicalDocument"/>.
/// Phase 1b extended the structural pass with persistent value capture for the
/// free-floating params people actually iterate on (sliders, panels, booleans,
/// value lists, color swatches, MD sliders, gradients), plus a SHA-256 digest
/// fallback for params holding internalized data we don't structurally model.
/// Schema v3 adds bounds capture so the on-canvas overlay can render accurate
/// ghosts for deleted components.
/// </summary>
public static class DocumentSerializer
{
    private const int CurrentSchemaVersion = 6;

    public static CanonicalDocument Serialize(GH_Document document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var objects = new List<CanonicalObject>();
        var groups = new List<CanonicalGroup>();

        foreach (var obj in document.Objects)
        {
            switch (obj)
            {
                case IGH_Component component:
                    objects.Add(SerializeComponent(component));
                    break;

                case IGH_Param param when param.Attributes?.Parent is null:
                    // Free-floating param (slider, panel, value list, etc.).
                    objects.Add(SerializeFreeFloatingParam(param));
                    break;

                case GH_Group group:
                    groups.Add(SerializeGroup(group));
                    break;
            }
        }

        // Stable ordering for diff: lexicographic by InstanceGuid string.
        objects.Sort((a, b) => string.CompareOrdinal(a.InstanceGuid, b.InstanceGuid));
        groups.Sort((a, b) => string.CompareOrdinal(a.InstanceGuid, b.InstanceGuid));

        return new CanonicalDocument(
            SchemaVersion: CurrentSchemaVersion,
            Document: ExtractMeta(document),
            Objects: objects,
            Groups: groups);
    }

    private static DocumentMeta ExtractMeta(GH_Document document)
    {
        // GH_DocumentProperties only carries Description / CopyRight / Date / etc.
        // The document name lives on GH_Document.DisplayName. Author is tracked by
        // Git on commit, so we do not duplicate it here.
        return new DocumentMeta(
            Name: NullSafe(document.DisplayName),
            Description: NullSafe(document.Properties?.Description));
    }

    private static CanonicalObject SerializeComponent(IGH_Component component)
    {
        var inputs = component.Params.Input
            .Select(SerializeParam)
            .ToList();
        var outputs = component.Params.Output
            .Select(SerializeParam)
            .ToList();

        return new CanonicalObject(
            InstanceGuid: Format(component.InstanceGuid),
            ComponentGuid: Format(component.ComponentGuid),
            Kind: "component",
            Name: NullSafe(component.Name),
            Nickname: NullSafe(component.NickName),
            Pivot: ExtractPivot(component),
            Inputs: inputs,
            Outputs: outputs,
            // GH_GradientControl is a component, not a free-floating
            // param, so its persistent gradient data must be captured
            // on this path too. The probe is a no-op for everything
            // else; it pays a single type-name check per component.
            Persistent: TryCaptureGradientReflective(component),
            Bounds: ExtractBounds(component));
    }

    private static CanonicalObject SerializeFreeFloatingParam(IGH_Param param)
    {
        // A free-floating param IS its own output. Model it as a 0-output object
        // whose single synthetic "input" carries its upstream sources, so wire
        // diffing still works against its Sources list.
        var syntheticInput = SerializeParam(param);

        return new CanonicalObject(
            InstanceGuid: Format(param.InstanceGuid),
            ComponentGuid: Format(param.ComponentGuid),
            Kind: "param",
            Name: NullSafe(param.Name),
            Nickname: NullSafe(param.NickName),
            Pivot: ExtractPivot(param),
            Inputs: new[] { syntheticInput },
            Outputs: Array.Empty<CanonicalParameter>(),
            Persistent: CapturePersistent(param),
            Bounds: ExtractBounds(param));
    }

    /// <summary>
    /// Captures the user-tweakable state of a free-floating param. Returns
    /// null when the param has no persistent state worth recording (no
    /// match against the typed handlers AND no internalized data).
    ///
    /// Special-cased kinds get structured representations so the diff can
    /// say "slider went from 5 to 10" rather than "opaque blob changed".
    /// Anything we don't have a typed handler for falls back to a
    /// SHA-256 digest of the param's GH-serialized form when it carries
    /// internalized data; that's enough to detect change without trying
    /// to render it.
    /// </summary>
    private static PersistentData? CapturePersistent(IGH_Param param)
    {
        switch (param)
        {
            case GH_NumberSlider slider:
                return new PersistentData(
                    Kind: "slider",
                    Slider: new SliderValue(
                        Value: slider.CurrentValue,
                        Min: slider.Slider.Minimum,
                        Max: slider.Slider.Maximum,
                        Decimals: slider.Slider.DecimalPlaces,
                        Type: slider.Slider.Type.ToString()));

            case GH_Panel panel:
                return new PersistentData(
                    Kind: "panel",
                    PanelText: NullSafe(panel.UserText));

            case GH_BooleanToggle toggle:
                return new PersistentData(
                    Kind: "boolean",
                    BooleanState: toggle.Value);

            case GH_ValueList valueList:
                {
                    var selected = (valueList.SelectedItems ?? new List<GH_ValueListItem>())
                        .Select(i => i.Name ?? string.Empty)
                        .OrderBy(s => s, StringComparer.Ordinal)
                        .ToList();
                    var allItems = (valueList.ListItems ?? new List<GH_ValueListItem>())
                        .Select(i => new ValueListItem(i.Name ?? string.Empty, i.Expression ?? string.Empty))
                        .OrderBy(i => i.Name, StringComparer.Ordinal)
                        .ToList();
                    return new PersistentData(
                        Kind: "valuelist",
                        ValueListSelected: selected,
                        ValueListItems: allItems,
                        ValueListMode: valueList.ListMode.ToString());
                }

            case GH_ColourSwatch swatch:
                return new PersistentData(
                    Kind: "color",
                    ColorArgb: swatch.SwatchColour.ToArgb().ToString("X8"));
        }

        // Long-tail typed capture via reflection - lets us extract
        // structured data from GH_GradientControl and MD slider
        // controls without referencing uncertain SDK type names at
        // compile time. If the reflection probe doesn't find the
        // expected shape, fall through to the digest fallback below.
        var gradient = TryCaptureGradientReflective(param);
        if (gradient is not null) return gradient;

        var md = TryCaptureMdSliderReflective(param);
        if (md is not null) return md;

        // Generic fallback for any persistent-typed param holding
        // internalized data (baked geometry on a Curve param, etc.).
        // PersistentDataCount lives on the generic GH_PersistentParam<T>;
        // rather than reflect across all the typed variants, we ask
        // via dynamic and treat zero/exception as "nothing to capture".
        var count = TryGetPersistentDataCount(param);
        if (count > 0)
        {
            return new PersistentData(
                Kind: "data",
                Digest: ComputeParamDigest(param));
        }

        return null;
    }

    private static readonly HashSet<string> s_loggedGradientShapes = new();

    // Per-type reflection memo. The serializer runs on every overlay
    // recompute, and the per-param probes were the dominant cost: a
    // dynamic-dispatch call that THREW a RuntimeBinderException for every
    // param without PersistentDataCount, plus a repeated full property
    // walk for gradient-named types whose shape never matches. Benign
    // races (idempotent writes) are acceptable; reads far outnumber them.
    private sealed class TypeProbe
    {
        public bool NameHasGradient;
        public bool IsMdSliderName;
        public bool GradientShapeMiss;
        public bool PersistentDataCountResolved;
        public System.Reflection.PropertyInfo? PersistentDataCount;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, TypeProbe>
        s_typeProbes = new();

    private static TypeProbe ProbeFor(Type type) => s_typeProbes.GetOrAdd(type, static t => new TypeProbe
    {
        NameHasGradient = t.Name.IndexOf("gradient", StringComparison.OrdinalIgnoreCase) >= 0,
        IsMdSliderName = t.Name is "GH_MdSlider" or "GH_MDSlider" or "GH_MultiDimensionalSlider",
    });

    /// <summary>
    /// Structural gradient capture — gates on a fuzzy type-name match
    /// (anything containing "gradient"), then walks the param's public
    /// instance properties for any object exposing an enumerable of
    /// stop-like items (each with a numeric position and a Color).
    /// Robust to naming variants across GH versions and third-party
    /// gradient-like components. On a type-name match that still
    /// yields no stops, logs the type + property shape ONCE per type
    /// so the user can hand us actionable data to fix the probe.
    /// </summary>
    private static PersistentData? TryCaptureGradientReflective(object obj)
    {
        if (obj is null) return null;
        var probe = ProbeFor(obj.GetType());
        if (!probe.NameHasGradient || probe.GradientShapeMiss) return null;
        var typeName = obj.GetType().Name;

        try
        {
            var stops = TryExtractStops(obj);
            if (stops is null && obj.GetType().GetProperty("Gradient")?.GetValue(obj) is { } inner)
                stops = TryExtractStops(inner);

            if (stops is { Count: > 0 })
            {
                stops.Sort((a, b) => a.Position.CompareTo(b.Position));
                return new PersistentData(Kind: "gradient", GradientStops: stops);
            }

            // Deterministic structural miss: this type's shape doesn't
            // expose stops. Remember it so the expensive property walk
            // never runs again for this type (exceptions do NOT latch -
            // a transient failure keeps its retry).
            probe.GradientShapeMiss = true;

            if (s_loggedGradientShapes.Add(typeName))
            {
                Rhino.RhinoApp.WriteLine($"[G-Loom] gradient probe miss on {typeName}");
                DumpShape(obj, "  outer");

                var gradientProp = obj.GetType().GetProperty("Gradient");
                var inner2 = gradientProp?.GetValue(obj);
                if (inner2 is not null)
                {
                    Rhino.RhinoApp.WriteLine($"  inner Gradient type={inner2.GetType().Name}");
                    DumpShape(inner2, "  inner");
                }
            }
        }
        catch (Exception ex)
        {
            Rhino.RhinoApp.WriteLine($"[G-Loom] gradient probe threw: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    private static List<GradientStop>? TryExtractStops(object obj)
        => ExtractStopsViaGripIndex(obj) ?? ExtractStopsFrom(obj) ?? WalkForStops(obj);

    /// GH_Gradient exposes its stops as an indexed VB property
    /// (`Grip(i)` / `get_Grip(int)`) paired with a `GripCount` scalar.
    /// Reflection's property walk can't see indexed accessors, so we
    /// target them by exact name. Safe: it's a getter we name
    /// explicitly, not arbitrary method iteration.
    private static List<GradientStop>? ExtractStopsViaGripIndex(object obj)
    {
        var t = obj.GetType();
        var countProp = t.GetProperty("GripCount");
        if (countProp is null) return null;

        int count;
        try { count = countProp.GetValue(obj) is int n ? n : 0; }
        catch { return null; }
        if (count <= 0) return null;

        // Indexed property (preferred — VB compiles `Property Grip(i)` to
        // an indexed property in .NET metadata), then named accessor
        // methods as fallback.
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        var gripIndexProp = t.GetProperties(F).FirstOrDefault(p =>
            p.Name == "Grip" &&
            p.GetIndexParameters() is { Length: 1 } ix &&
            ix[0].ParameterType == typeof(int));
        var gripMethod = gripIndexProp is null
            ? (t.GetMethod("get_Grip", new[] { typeof(int) })
            ?? t.GetMethod("Grip",     new[] { typeof(int) })
            ?? t.GetMethod("get_Item", new[] { typeof(int) })
            ?? t.GetMethod("Item",     new[] { typeof(int) }))
            : null;
        if (gripIndexProp is null && gripMethod is null)
        {
            Rhino.RhinoApp.WriteLine($"[G-Loom] gradient probe: GripCount={count} but no grip accessor on {t.Name}");
            return null;
        }

        var stops = new List<GradientStop>(count);
        for (int i = 0; i < count; i++)
        {
            object? grip;
            try
            {
                grip = gripIndexProp is not null
                    ? gripIndexProp.GetValue(obj, new object[] { i })
                    : gripMethod!.Invoke(obj, new object[] { i });
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine($"[G-Loom] gradient probe: grip[{i}] read threw {ex.GetType().Name}");
                return null;
            }
            if (grip is null) continue;

            var gt = grip.GetType();
            var posP = gt.GetProperty("Parameter") ?? gt.GetProperty("Position")
                    ?? gt.GetProperty("T")         ?? gt.GetProperty("Anchor");
            // GH_Grip uses ColourLeft/ColourRight (for gradient interpolation
            // direction). Either side reads the stop's color; prefer Left.
            var colP = gt.GetProperty("ColourLeft") ?? gt.GetProperty("ColorLeft")
                    ?? gt.GetProperty("Colour")     ?? gt.GetProperty("Color")
                    ?? gt.GetProperty("ColourRight")?? gt.GetProperty("ColorRight");
            if (posP is null || colP is null)
            {
                if (s_loggedGradientShapes.Add(gt.Name + ".grip"))
                {
                    Rhino.RhinoApp.WriteLine($"[G-Loom] gradient probe: grip type={gt.Name} pos={(posP is null ? "null" : posP.Name)} col={(colP is null ? "null" : colP.Name)}");
                    DumpShape(grip, "  grip");
                }
                return null;
            }

            object? rawPos, rawCol;
            try { rawPos = posP.GetValue(grip); rawCol = colP.GetValue(grip); }
            catch { return null; }
            if (rawCol is not Color color)
            {
                Rhino.RhinoApp.WriteLine($"[G-Loom] gradient probe: grip colour is {rawCol?.GetType().Name ?? "null"}, not System.Drawing.Color");
                return null;
            }

            double position = rawPos switch
            {
                double d  => d,
                float f   => f,
                decimal m => (double)m,
                int ii    => ii,
                _         => double.NaN,
            };
            if (double.IsNaN(position))
            {
                Rhino.RhinoApp.WriteLine($"[G-Loom] gradient probe: grip position is {rawPos?.GetType().Name ?? "null"}, not a number");
                return null;
            }

            stops.Add(new GradientStop(position, color.ToArgb().ToString("X8")));
        }
        if (stops.Count > 0 && s_loggedGradientShapes.Add(t.Name + ".ok"))
            Rhino.RhinoApp.WriteLine($"[G-Loom] gradient probe: extracted {stops.Count} stops from {t.Name}");
        return stops.Count > 0 ? stops : null;
    }

    private static List<GradientStop>? WalkForStops(object root)
    {
        foreach (var value in EnumerateMemberValues(root))
        {
            if (value is null) continue;
            var stops = ExtractStopsFrom(value);
            if (stops is { Count: > 0 }) return stops;
        }
        return null;
    }

    // Read-only reflection walk: properties + fields only. Never
    // invokes methods — many GH SDK no-arg methods have side effects
    // (Solve / ExpireSolution / lazy icon init) and invoking them
    // during a paint-cycle diff broke GH's drawing pipeline.
    private static IEnumerable<object?> EnumerateMemberValues(object root)
    {
        var t = root.GetType();
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

        foreach (var p in t.GetProperties(F))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object? v = null; bool ok = false;
            try { v = p.GetValue(root); ok = true; } catch { }
            if (ok) yield return v;
        }
        foreach (var f in t.GetFields(F))
        {
            object? v = null; bool ok = false;
            try { v = f.GetValue(root); ok = true; } catch { }
            if (ok) yield return v;
        }
    }

    private static void DumpShape(object obj, string prefix)
    {
        var t = obj.GetType();
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        var props = string.Join(", ", t.GetProperties(F).Select(p => $"{p.Name}:{p.PropertyType.Name}"));
        var fields = string.Join(", ", t.GetFields(F).Select(f => $"{f.Name}:{f.FieldType.Name}"));
        Rhino.RhinoApp.WriteLine($"{prefix} props=[{props}]");
        if (!string.IsNullOrEmpty(fields)) Rhino.RhinoApp.WriteLine($"{prefix} fields=[{fields}]");
    }

    /// <summary>
    /// Looks for an IEnumerable property on <paramref name="obj"/> whose
    /// items each expose a numeric position and a System.Drawing.Color.
    /// Returns the first viable list, or null. Works whether obj IS the
    /// collection or merely holds one.
    /// </summary>
    private static List<GradientStop>? ExtractStopsFrom(object obj)
    {
        if (obj is System.Collections.IEnumerable directList && obj is not string)
        {
            var direct = TryReadStops(directList);
            if (direct is { Count: > 0 }) return direct;
        }

        foreach (var value in EnumerateMemberValues(obj))
        {
            if (value is null) continue;
            if (value is not System.Collections.IEnumerable items || value is string) continue;

            var stops = TryReadStops(items);
            if (stops is { Count: > 0 }) return stops;
        }
        return null;
    }

    private static List<GradientStop>? TryReadStops(System.Collections.IEnumerable items)
    {
        var stops = new List<GradientStop>();
        foreach (var item in items)
        {
            if (item is null) continue;
            var t = item.GetType();
            var posP = t.GetProperty("Parameter") ?? t.GetProperty("Position")
                    ?? t.GetProperty("T")        ?? t.GetProperty("Anchor");
            var colP = t.GetProperty("Colour") ?? t.GetProperty("Color")
                    ?? t.GetProperty("Value");
            if (posP is null || colP is null) return null;

            object? rawPos, rawCol;
            try { rawPos = posP.GetValue(item); rawCol = colP.GetValue(item); }
            catch { return null; }
            if (rawCol is not Color color) return null;

            double position = rawPos switch
            {
                double d => d,
                float f  => f,
                decimal m => (double)m,
                int i    => i,
                _        => double.NaN,
            };
            if (double.IsNaN(position)) return null;

            stops.Add(new GradientStop(position, color.ToArgb().ToString("X8")));
        }
        return stops;
    }

    /// <summary>
    /// Reflection-based MD slider capture. Matches a handful of plausible
    /// type names (GH naming varies: GH_MdSlider, GH_MDSlider,
    /// GH_MultiDimensionalSlider) and pulls a 2D position off whichever
    /// of CurrentValue / Value / Position actually exposes one. Returns
    /// null on any mismatch.
    /// </summary>
    private static PersistentData? TryCaptureMdSliderReflective(IGH_Param param)
    {
        if (!ProbeFor(param.GetType()).IsMdSliderName)
            return null;

        try
        {
            var paramType = param.GetType();
            var valueProp = paramType.GetProperty("CurrentValue")
                         ?? paramType.GetProperty("Value")
                         ?? paramType.GetProperty("Position");
            var value = valueProp?.GetValue(param);
            if (value is null) return null;

            var valueType = value.GetType();
            var xProp = valueType.GetProperty("X");
            var yProp = valueType.GetProperty("Y");
            var x = xProp?.GetValue(value);
            var y = yProp?.GetValue(value);

            var xd = x switch
            {
                double dx => dx,
                float fx => fx,
                decimal mx => (double)mx,
                _ => double.NaN,
            };
            var yd = y switch
            {
                double dy => dy,
                float fy => fy,
                decimal my => (double)my,
                _ => double.NaN,
            };
            if (double.IsNaN(xd) || double.IsNaN(yd)) return null;

            return new PersistentData(Kind: "mdslider", MdSlider: new MdSliderValue(xd, yd));
        }
        catch
        {
            return null;
        }
    }

    private static int TryGetPersistentDataCount(IGH_Param param)
    {
        // Property lookup memoized per type. The previous dynamic dispatch
        // threw (and swallowed) a RuntimeBinderException for every param
        // type without the property, on every serialize.
        var probe = ProbeFor(param.GetType());
        if (!probe.PersistentDataCountResolved)
        {
            probe.PersistentDataCount = param.GetType().GetProperty(
                "PersistentDataCount",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            probe.PersistentDataCountResolved = true;
        }

        try
        {
            return probe.PersistentDataCount?.GetValue(param) is int n ? n : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// SHA-256 of the param's full GH XML serialization. Includes more than
    /// just the persistent data (the param's own attributes ride along too)
    /// but for diff purposes that's acceptable: structural fields are
    /// captured separately, so a digest mismatch on a structurally-stable
    /// param means the persistent payload moved.
    /// </summary>
    private static string ComputeParamDigest(IGH_Param param)
    {
        try
        {
            var chunk = new GH_LooseChunk("Persistent");
            param.Write(chunk);
            var bytes = Encoding.UTF8.GetBytes(chunk.Serialize_Xml());
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "unknown";
        }
    }

    private static CanonicalParameter SerializeParam(IGH_Param param)
    {
        var sources = param.Sources?
            .Select(s => Format(s.InstanceGuid))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList() ?? new List<string>();

        return new CanonicalParameter(
            InstanceGuid: Format(param.InstanceGuid),
            Name: NullSafe(param.Name),
            Nickname: NullSafe(param.NickName),
            Access: param.Access.ToString().ToLowerInvariant(),
            Sources: sources);
    }

    private static CanonicalGroup SerializeGroup(GH_Group group)
    {
        var members = (group.ObjectIDs ?? new List<Guid>())
            .Select(Format)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return new CanonicalGroup(
            InstanceGuid: Format(group.InstanceGuid),
            Name: NullSafe(group.NickName),
            Members: members);
    }

    private static Pivot ExtractPivot(IGH_DocumentObject obj)
    {
        var p = obj.Attributes?.Pivot ?? PointF.Empty;
        return new Pivot(p.X, p.Y);
    }

    private static Bounds? ExtractBounds(IGH_DocumentObject obj)
    {
        var b = obj.Attributes?.Bounds ?? RectangleF.Empty;
        if (b.IsEmpty || (b.Width == 0 && b.Height == 0)) return null;
        return new Bounds(b.X, b.Y, b.Width, b.Height);
    }

    private static string Format(Guid g) => g.ToString("D");

    private static string NullSafe(string? value) => value ?? string.Empty;
}
