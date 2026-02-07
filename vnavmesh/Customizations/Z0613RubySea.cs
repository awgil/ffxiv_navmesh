using DotRecast.Detour;
using Navmesh;
using System.Collections.Generic;

namespace vnavmesh.Customizations;

[CustomizationTerritory(613)]
internal class Z0613RubySea : NavmeshCustomization
{
    public override void CustomizeMesh(DtNavMesh mesh, List<uint> festivalLayers)
    {
        // the tunnel into the island containing tamamizu has some floor that is unlandable
        LinkPoints(mesh, new(643.7f, 3.4f, -58.9f), new(636.6f, 3.9f, -63.3f));
    }
}
