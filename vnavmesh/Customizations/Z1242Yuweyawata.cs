namespace Navmesh.Customizations;

[CustomizationTerritory(1242)]
class Z1242Yuweyawata : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // avoid the hole in last boss arena
        var (transform, aabb) = GenerateTransformAABB(new(11, 1, 11), new(34, -87.9f, -710));
        scene.Meshes["<cylinder>"].Instances.Add(new(0xbaadf00d00000001ul, transform, aabb, SceneExtractor.PrimitiveFlags.ForceUnwalkable, default));
    }
}
