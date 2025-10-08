namespace Navmesh.Customizations;

[CustomizationTerritory(1188)]
internal class Z1188Kozamauka : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // contender #3 in the most cursed mesh transformation finalists
        if (scene.Meshes.TryGetValue("bg/ex5/02_ykt_y6/fld/y6f2/collision/y6f2_x0_tst00.pcb", out var mesh))
            mesh.Instances[0].WorldTransform.M42 += 0.05f;
    }
}
