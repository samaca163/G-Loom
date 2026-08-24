using Grasshopper.Kernel;

namespace GLoom.Components;

public abstract class GLoomComponent : GH_Component
{
    public const string Tab = "G-Loom";
    public const string ProjectGroup = "Project";

    protected GLoomComponent(string name, string nickname, string description, string group)
        : base(name, nickname, description, Tab, group) { }

    public override GH_Exposure Exposure => GH_Exposure.primary;

    /// <summary>
    /// The .gh on disk that owns this component, or null if it has never been saved.
    /// Inside a cluster the pinged document is the cluster's own, which has no file -
    /// the real definition is further up the owner chain.
    /// </summary>
    protected string? HostFilePath()
    {
        var doc = OnPingDocument();
        for (var hop = 0; doc is { IsFilePathDefined: false } && hop < 16; hop++)
            doc = doc.Owner?.OwnerDocument();
        return doc?.IsFilePathDefined == true ? doc.FilePath : null;
    }
}
