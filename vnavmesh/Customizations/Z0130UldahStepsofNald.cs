namespace Navmesh.Customizations;

[CustomizationTerritory(130)]
class Z0130UldahStepsofNald : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeTile(Tile tile)
    {
        foreach (var obj in tile.ObjectsByPath("bg/ffxiv/wil_w1/twn/common/collision/w1t0_f0_kadn1.pcb"))
            obj.Instance.ForceSetPrimFlags |= SceneExtractor.PrimitiveFlags.ForceUnwalkable;
    }
}
