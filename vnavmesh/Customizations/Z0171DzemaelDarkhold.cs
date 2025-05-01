namespace Navmesh.Customizations;

[CustomizationTerritory(171)]
class Z0171DzemaelDarkhold : NavmeshCustomization
{
    public override int Version => 2;

    public override void CustomizeScene(SceneExtractor scene)
    {
        foreach (var (key, mesh) in scene.Meshes)
            if (key.StartsWith("bg/ffxiv/roc_r1/rad/r1r1/collision/r1r1_a1_dor"))
                mesh.Instances.Clear();
    }
}
