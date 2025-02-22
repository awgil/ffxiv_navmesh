namespace Navmesh.Customizations;

[CustomizationTerritory(1113)]
class Z1113Xelphatol : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        scene.Meshes.Remove("<box>");
    }
}
