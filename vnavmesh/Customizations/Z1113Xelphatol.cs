namespace Navmesh.Customizations;

[CustomizationTerritory(1113)]
class Z1113Xelphatol : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        //remove entire mesh and all instances
        if (scene.Meshes.TryGetValue("<box>", out var _))
            scene.Meshes.Remove("<box>");
    }

    public Z1113Xelphatol()
    {
    }
}
