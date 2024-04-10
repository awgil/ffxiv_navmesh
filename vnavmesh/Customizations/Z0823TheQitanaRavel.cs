using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace Navmesh.Customizations;

[CustomizationTerritory(823)]
class Z0823TheQitanaRavel : NavmeshCustomization
{
    public override int Version => 1;
    
    public override void CustomizeScene(SceneExtractor scene)
    {
        //remove entire mesh and all instances
        if (scene.Meshes.TryGetValue("<plane one-sided>", out var _))
            scene.Meshes.Remove("<plane one-sided>");
    }
    
    public Z0823TheQitanaRavel()
    {
    }
}
