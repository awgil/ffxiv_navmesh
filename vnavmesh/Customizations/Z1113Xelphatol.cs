namespace Navmesh.Customizations;

[CustomizationTerritory(1113)]
class Z1113Xelphatol : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // remove raised drawbridges
        if (scene.Meshes.TryGetValue("<box>", out var boxes))
            boxes.Instances.RemoveAll(inst => inst.Id is 0x616AB407000000 or 0x616AE407000000);
    }
}
