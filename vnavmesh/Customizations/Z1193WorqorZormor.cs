namespace Navmesh.Customizations;

[CustomizationTerritory(1193)]
class Z1193WorqorZormor : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // remove large crystal blocking initial path, which is destroyed after first pack dies
        scene.Meshes.Remove("bg/ex5/02_ykt_y6/dun/y6d2/collision/y6d2_a1_cry03.pcb");
    }
}
