namespace Navmesh.Customizations;

[CustomizationTerritory(132)]
internal class Z0132NewGridania : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        if (scene.Meshes.TryGetValue("bg/ffxiv/fst_f1/twn/common/collision/f1t0_a0_plnt1.pcb", out var mesh))
            foreach (var inst in mesh.Instances)
                // setting the planter as unwalkable has no effect and i don't feel like figuring out why so just double the height instead
                inst.WorldTransform.M22 *= 2;
    }
}
