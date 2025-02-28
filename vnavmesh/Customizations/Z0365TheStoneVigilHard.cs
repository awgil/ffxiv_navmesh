namespace Navmesh.Customizations;

[CustomizationTerritory(365)]
class Z0365TheStoneVigilHard : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        //remove entire mesh and all instances
        scene.Meshes.Remove("bg/ffxiv/roc_r1/dun/r1d2/collision/r1d2_x1_rubb1.pcb");
    }
}
