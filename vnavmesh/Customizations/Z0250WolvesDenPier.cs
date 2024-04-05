namespace Navmesh.Customizations;

[CustomizationTerritory(250)]
class Z0250WolvesDenPier : NavmeshCustomization
{
    public override int Version => 1;

    public override bool IsFlyingSupported(SceneDefinition definition) => false; // this is unflyable, despite intended use being 1
}
