namespace Navmesh.Customizations;

[CustomizationTerritory(957)]
internal class Z0957Thavnair : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeTile(Tile tile)
    {
        foreach (var obj in tile.ObjectsByPath("bg/ex4/02_mid_m5/fld/m5f1/collision/m5f1_a1_fnt14.pcb"))
            obj.Instance.WorldTransform.M22 *= 2;
    }
}
