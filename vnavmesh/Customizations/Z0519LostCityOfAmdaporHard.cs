namespace Navmesh.Customizations;

[CustomizationTerritory(519)]
class Z0519LostCityOfAmdaporHard : NavmeshCustomization
{
    public override int Version => 1;

    public Z0519LostCityOfAmdaporHard()
    {
        Settings.AgentMaxClimb = 0.75f;  // web bridges - TODO: think about a better systemic solution
    }
}
