namespace Navmesh.Customizations;

[CustomizationTerritory(1110)]
class Z1110AetherochemicalResearchFacility : NavmeshCustomization
{
    public override int Version => 3;

    //public override bool FilterObject(InstanceWithMesh inst) => inst.Mesh.Path != "<box>";

    //public override void CustomizeScene(SceneExtractor scene)
    //{
    //    // the lifts in the final room move when being used, so if the client triggers a rebuild at the end of the dungeon for whatever reason, it will break mesh connectivity (for subsequent runs)
    //    scene.InsertCylinderCollider(new System.Numerics.Vector3(10, 1, 10), new(221.229f, -60, 95f));
    //    scene.InsertCylinderCollider(new System.Numerics.Vector3(10, 1, 10), new(195f, -29f, 196f));
    //}
}
