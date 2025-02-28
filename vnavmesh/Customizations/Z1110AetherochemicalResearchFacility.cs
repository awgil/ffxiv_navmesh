namespace Navmesh.Customizations;

[CustomizationTerritory(1110)]
class Z1110AetherochemicalResearchFacility : NavmeshCustomization
{
    public override int Version => 2;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // colliders blocking lifts
        scene.Meshes.Remove("<box>");

        // the final lift actually moves when being used, so if the client triggers a rebuild at the end of the dungeon for whatever reason, it will break mesh connectivity (for subsequent runs)
        var (transform, aabb) = GenerateTransformAABB(new(10, 1, 10), new(221.229f, -60, 95f));
        scene.Meshes["<cylinder>"].Instances.Add(new(0xbaadf00d00000001ul, transform, aabb, default, default));
    }
}
