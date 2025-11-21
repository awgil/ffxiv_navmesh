using DotRecast.Detour;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(155)]
class Z0155CoerthasCentralHighlands : NavmeshCustomization
{
    public override int Version => 6;

    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        config.AddOffMeshConnection(new Vector3(-418.71f, 221.50f, -288.00f), new Vector3(-418.28f, 222.75f, -290.65f), bidirectional: true);
    }
}
