namespace Navmesh.Customizations;

[CustomizationTerritory(823)]
class Z0823TheQitanaRavel : NavmeshCustomization
{
    //public override int Version => 2;

    // layout contains temporary colliders not marked with the correct flag (0x400)
    //public override bool FilterObject(InstanceWithMesh inst) => inst.Mesh.Path != "<plane one-sided>";
}
