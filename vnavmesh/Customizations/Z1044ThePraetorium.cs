namespace Navmesh.Customizations;

[CustomizationTerritory(1044)]
class Z1044ThePraetorium : NavmeshCustomization
{
    public override int Version => 3;

    public Z1044ThePraetorium()
    {
        // this allows mesh to go down the ramp from the magitek armor and its ok in dungeons because non traversable ledges will
        // have Plane colliders which will not allow the mesh to overhang anyways even if Rasterization is conservative
        // (because being conservative does not ignore Plane Colliders)
        Settings.Filtering -= NavmeshSettings.Filter.LedgeSpans;
    }
}
