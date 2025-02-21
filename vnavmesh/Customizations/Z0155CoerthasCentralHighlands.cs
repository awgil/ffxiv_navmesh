namespace Navmesh.Customizations;

[CustomizationTerritory(155)]
class Z0155CoerthasCentralHighlands : NavmeshCustomization
{
    public override int Version => 1;

    public Z0155CoerthasCentralHighlands()
    {
        // the second staircase inside the building in Whitebrim Front is not actually connected directly to the landing, but can be walked on regardless
        Settings.AgentMaxClimb = 0.75f;
    }
}
