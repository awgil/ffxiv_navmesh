using DotRecast.Detour;

namespace Navmesh.Customizations;

[CustomizationTerritory(132)]
internal class Z0132NewGridania : NavmeshCustomization
{
    public override int Version => 2;

    public override void CustomizeScene(SceneExtractor scene)
    {
        if (scene.Meshes.TryGetValue("bg/ffxiv/fst_f1/twn/common/collision/f1t0_a0_plnt1.pcb", out var mesh))
            foreach (var inst in mesh.Instances)
                // setting the planter as unwalkable has no effect and i don't feel like figuring out why so just double the height instead
                inst.WorldTransform.M22 *= 2;
    }

    public override void CustomizeTile(TileObjects tile)
    {
        foreach (var obj in tile.ObjectsByPath("bg/ffxiv/fst_f1/twn/common/collision/f1t0_a0_plnt1.pcb"))
            obj.Instance.WorldTransform.M22 *= 2;
    }

    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        config.AddOffMeshConnection(new(45.03f, -0.13f, 83.1f), new(46.78f, -8.5f, 91.75f));
    }
}
