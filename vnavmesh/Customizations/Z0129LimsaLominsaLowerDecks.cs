using System.Collections.Generic;

namespace Navmesh.Customizations;

[CustomizationTerritory(129)]
class Z0129LimsaLominsaLowerDecks : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
    {
        // ship interior 1
        LinkPoints(mesh, new(-274.10587f, 11.32725f, 188.9568f), new(-272.5555f, 11.780226f, 188.65962f), Navmesh.AreaId.Default);
        LinkPoints(mesh, new(-272.5555f, 11.780226f, 188.65962f), new(-274.10587f, 11.32725f, 188.9568f), Navmesh.AreaId.Default);
    }
}
