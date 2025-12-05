using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.Interop;

namespace Navmesh.Customizations;

[CustomizationTerritory(975)]
internal class Z0975Zadnor : NavmeshCustomization
{
    public override int Version => 1;

    public override unsafe bool FilterObject(ulong key, Pointer<ILayoutInstance> inst)
    {
        // LG F8E1 'LVD_BOMBING' contains airships that periodically fly around the zone dropping bombs
        // there is no timeline or actioncontroller, they are manipulated by the contentdirector (i assume)
        return !(inst.Value != null && inst.Value->Layer->Id != 0xF8E1);
    }
}
