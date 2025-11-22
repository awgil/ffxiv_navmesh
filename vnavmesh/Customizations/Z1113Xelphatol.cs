namespace Navmesh.Customizations;

[CustomizationTerritory(1113)]
class Z1113Xelphatol : NavmeshCustomization
{
    public override int Version => 2;

    public override bool FilterObject(InstanceWithMesh inst) => inst.Mesh.Path != "<box>";
}
