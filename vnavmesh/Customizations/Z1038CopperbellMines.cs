namespace Navmesh.Customizations;

[CustomizationTerritory(1038)]
class Z1038CopperbellMines : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        //remove entire mesh and all instances
        scene.Meshes.Remove("bg/ffxiv/wil_w1/dun/w1d1/collision/w1d1_a2_wall1.pcb");
    }
}
