using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace Navmesh.Customizations;

[CustomizationTerritory(1041)]
class Z1041BrayfloxsLongstop : NavmeshCustomization
{
    public override int Version => 1;
    
    public Z1041BrayfloxsLongstop()
    {
        Settings.Partitioning = DotRecast.Recast.RcPartition.MONOTONE;
    }
}
