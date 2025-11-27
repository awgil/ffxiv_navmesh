namespace Navmesh.Customizations;

[CustomizationTerritory(822)]
class Z0822MtGulg : NavmeshCustomization
{
    //public override int Version => 2;

    // layout contains temporary colliders not marked with the correct flag (0x400)
    //public override bool FilterObject(InstanceWithMesh inst) => inst.Mesh.Path != "<plane one-sided>";
}
