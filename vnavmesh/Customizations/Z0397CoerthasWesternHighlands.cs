using DotRecast.Detour;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(397), CustomizationTerritory(467), CustomizationTerritory(470), CustomizationTerritory(472), CustomizationTerritory(477), CustomizationTerritory(479), CustomizationTerritory(489), CustomizationTerritory(493), CustomizationTerritory(497), CustomizationTerritory(498), CustomizationTerritory(709), CustomizationTerritory(866)]
internal class Z0397CoerthasWesternHighlands : NavmeshCustomization
{
    public override int Version => 1;

    // the gorgagne mills doorway is so narrow (5 cells) that i think it might be getting deleted by walkable area erosion
    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        config.AddOffMeshConnection(new Vector3(454.68f, 164.43f, -537.03f), new Vector3(454.67f, 164.31f, -539.78f));
    }
}
