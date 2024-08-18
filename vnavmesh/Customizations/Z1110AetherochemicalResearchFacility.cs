namespace Navmesh.Customizations;

[CustomizationTerritory(1110)]
class Z1110AetherochemicalResearchFacility : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        //remove entire mesh and all instances
        if (scene.Meshes.TryGetValue("<box>", out var _))
            scene.Meshes.Remove("<box>");
    }

    public Z1110AetherochemicalResearchFacility()
    {
    }
}
