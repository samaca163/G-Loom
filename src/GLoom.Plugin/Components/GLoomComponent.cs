using Grasshopper.Kernel;

namespace GLoom.Components;

/// <summary>
/// Shared base for G-Loom's canvas components. G-Loom is panel-first by design;
/// a component earns a place on the ribbon only when its value genuinely belongs
/// on the wire graph. Holding the tab and group names in one place keeps that
/// small set from fragmenting across the ribbon as it grows.
/// </summary>
public abstract class GLoomComponent : GH_Component
{
    public const string Tab = "G-Loom";
    public const string ProjectGroup = "Project";

    protected GLoomComponent(string name, string nickname, string description, string group)
        : base(name, nickname, description, Tab, group)
    {
    }

    public override GH_Exposure Exposure => GH_Exposure.primary;
}
