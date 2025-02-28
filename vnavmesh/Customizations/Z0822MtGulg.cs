namespace Navmesh.Customizations;

[CustomizationTerritory(822)]
class Z0822MtGulg : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        //remove entire mesh and all instances
        scene.Meshes.Remove("<plane one-sided>");
    }
}
