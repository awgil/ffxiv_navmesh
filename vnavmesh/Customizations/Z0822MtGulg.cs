namespace Navmesh.Customizations;

[CustomizationTerritory(822)]
class Z0822MtGulg : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // the plane collider blocking b3 is toggled by the director, like most other temporary objects, but doesn't have 0x400 for some reason
        scene.Meshes.Remove("<plane one-sided>");
    }

    public override void CustomizeTile(Tile tile)
    {
        tile.RemoveObjects(o => o.Mesh.Path == "<plane one-sided>");
    }
}
