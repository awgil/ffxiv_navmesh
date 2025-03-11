namespace Navmesh.Customizations;

[CustomizationTerritory(1044)]
class Z1044ThePraetorium : NavmeshCustomization
{
    public override int Version => 3;

    public Z1044ThePraetorium()
    {
        // allow connection between higher and lower section of broken ramp right after the magitek armor
        Settings.Filtering -= NavmeshSettings.Filter.LedgeSpans;
    }
}

