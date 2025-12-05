namespace Navmesh.Customizations;

[CustomizationTerritory(1188)]
internal class Z1188Kozamauka : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeTile(Tile tile)
    {
        // fence around the tent gets rasterized to be shorter than maxclimb :/
        foreach (var obj in tile.ObjectsByPath("bg/ex5/02_ykt_y6/fld/y6f2/collision/y6f2_x0_tst00.pcb"))
            obj.Instance.WorldTransform.M42 += 0.05f;
    }
}
