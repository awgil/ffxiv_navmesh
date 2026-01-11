namespace Navmesh.Customizations;

[CustomizationTerritory(146)]
internal class Z0146SouthernThanalan : NavmeshCustomization
{
    public override int Version => 3;

    public override void CustomizeScene(SceneExtractor scene)
    {
        if (scene.Meshes.TryGetValue("<box>", out var mesh))
            mesh.Instances.RemoveAll(i => i.Material == 0x206406);
    }
}
