using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(152)]
internal class Z0152EastShroud : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeTile(Tile tile)
    {
        // cringe fallen log
        tile.AddCylinder(new Vector3(2), new(-40, -8, 225));
    }
}
