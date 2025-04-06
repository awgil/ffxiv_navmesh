namespace Navmesh.Customizations;

[CustomizationTerritory(152)]
internal class Z0152EastShroud : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        scene.InsertCylinderCollider(new System.Numerics.Vector3(2, 2, 2), new(-40, -8, 225), SceneExtractor.PrimitiveFlags.Unlandable);
    }
}
