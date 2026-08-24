using System;
using System.Collections.Generic;
using Grasshopper.Rhinoceros;
using Grasshopper.Rhinoceros.Model;

namespace GLoom.Model;

/// <summary>
/// The only file in the plugin that names a <c>Grasshopper.Rhinoceros</c> type.
///
/// The containment is deliberate, the same way <c>GLoomRepository</c> is the only place
/// that shells out to git: this namespace is newer than the rest of the Grasshopper SDK
/// and has already had members removed and renamed between service releases, so if a
/// type ever fails to load it should cost the Survey components and nothing else.
///
/// Everything used here is present in the pinned Grasshopper 8.0.23304.9001 reference,
/// so the plugin compiles low and runs high. Members added after 8.0 are off limits -
/// binding is by simple name, so touching one compiles cleanly and then throws on a
/// Rhino that predates it.
/// </summary>
internal static class ModelObjectBridge
{
    /// <summary>
    /// The active document's length unit, stamped onto every record. A bare "340" means
    /// nothing without it, and a survey drawn in metres read back as millimetres is a
    /// wrong building rather than a wrong number.
    ///
    /// This reads the document; it never writes to it. The write-loop machinery the
    /// stamping side needs does not apply to a read.
    /// </summary>
    public static string ActiveLengthUnit()
    {
        try
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            return doc is null ? "UNKNOWN" : doc.ModelUnitSystem.ToString().ToUpperInvariant();
        }
        catch (Exception)
        {
            return "UNKNOWN";
        }
    }

    public static Guid? IdOf(ModelObject? model)
    {
        try { return model?.Id; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The object's layer as a full path. Null when the model object carries no layer -
    /// which is what happens when the input was fed plain geometry rather than a
    /// document object, and is worth reporting rather than silently classifying as
    /// unmapped.
    /// </summary>
    public static string? LayerPathOf(ModelObject? model)
    {
        try
        {
            var layer = model?.Layer;
            if (layer is null) return null;

            // ModelContentName converts to string implicitly and already joins nested
            // layers; LayerPath.Normalize then reconciles whatever separator it used.
            string path = layer.Path;
            if (!string.IsNullOrWhiteSpace(path)) return path;

            string name = layer.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IReadOnlyDictionary<string, string> ReadUserText(ModelObject? model)
    {
        // Rhino compares user-text keys case-insensitively, so the mirror of them here
        // has to as well or a round trip could produce two keys differing only in case.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var text = model?.UserText;
            if (text is null) return result;

            foreach (var pair in text)
                if (!string.IsNullOrEmpty(pair.Key))
                    result[pair.Key] = pair.Value;
        }
        catch (Exception)
        {
            // An unreadable attribute bag is a classification input, not a crash.
        }
        return result;
    }

    /// <summary>
    /// A copy of the model object carrying exactly <paramref name="pairs"/> as its user
    /// text. In-memory only - nothing here touches the Rhino document, which is what
    /// lets the classifier run on every solve without any of the write-loop machinery.
    /// </summary>
    public static ModelObject WithUserText(ModelObject model, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var attributes = model.ToAttributes();
        attributes.UserText = new ModelUserText(pairs);
        return attributes;
    }
}
