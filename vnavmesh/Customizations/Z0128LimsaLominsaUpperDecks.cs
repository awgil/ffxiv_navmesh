namespace Navmesh.Customizations;

[CustomizationTerritory(128)]
class Z0128LimsaLominsaUpperDecks : NavmeshCustomization
{
    public override int Version => 1;

    public Z0128LimsaLominsaUpperDecks()
    {
        Settings.AgentRadius = 0.75f;
    }
}
