namespace Navmesh.Customizations;

[CustomizationTerritory(399)]
internal class Z0399DravanianHinterlands : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // remove the pile of garbage blocking the west entrance to idyllshire
        if (scene.Meshes.TryGetValue("bg/ex1/02_dra_d2/fld/d2f2/collision/d2f2_by_gare14.pcb", out var rock1))
            rock1.Instances.RemoveRange(25, 3);
        if (scene.Meshes.TryGetValue("bg/ex1/02_dra_d2/fld/d2f2/collision/d2f2_by_gare04.pcb", out var rock2))
            rock2.Instances.RemoveAt(20);
        if (scene.Meshes.TryGetValue("bg/ex1/02_dra_d2/fld/d2f2/collision/d2f2_by_gare07.pcb", out var rock3))
            rock3.Instances.RemoveAt(10);
    }
}
