namespace Navmesh.Customizations;

[CustomizationTerritory(822)]
class Z0822MtGulg : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        //remove entire mesh and all instances
        if (scene.Meshes.TryGetValue("<plane one-sided>", out var _))
            scene.Meshes.Remove("<plane one-sided>");
    }

    public Z0822MtGulg()
    {
    }
}
