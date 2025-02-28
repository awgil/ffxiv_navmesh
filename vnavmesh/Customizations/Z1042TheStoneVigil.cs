namespace Navmesh.Customizations;

[CustomizationTerritory(1042)]
class Z1042TheStoneVigil : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        //Force all instances of Mesh as unwalkable
        if (scene.Meshes.TryGetValue("bg/ffxiv/roc_r1/dun/r1d1/collision/r1d1_b1_sas03.pcb", out var mesh))
            foreach (var instance in mesh.Instances)
                instance.ForceSetPrimFlags |= SceneExtractor.PrimitiveFlags.ForceUnwalkable;
    }
}
