namespace Navmesh.Customizations;

[CustomizationTerritory(387)]
class Z0387SastashaHard : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        //remove entire mesh and all instances
        scene.Meshes.Remove("bg/ffxiv/sea_s1/dun/s1d7/collision/s1d7_b1_mud2.pcb");
    }
}
