using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.Interop;

namespace Navmesh.Customizations;

[CustomizationTerritory(1055)]
internal class Z1055IslandSanctuary : NavmeshCustomization
{
    public override int Version => 1;

    public override bool FilterObject(ulong key, Pointer<ILayoutInstance> inst)
    {
        // gathering points actually do have collision and are actually toggled by the content director. very cool
        return (key >> 32) < 0xFFFA0275;
    }
}
