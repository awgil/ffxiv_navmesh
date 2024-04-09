namespace Navmesh.Customizations;

[CustomizationTerritory(1142)]
class Z1142TheSirensongSea : NavmeshCustomization
{
    public override int Version => 1;

    public Z1142TheSirensongSea()
    {
        Settings.Filtering -= NavmeshSettings.Filter.LedgeSpans; // this allows mesh to go down the bowsprit to the land from the boat
                                                                 // and its ok in dungeons because non traversable ledges will
                                                                 // have Plane colliders which will not allow the mesh to overhang
                                                                 // anyways even if Rasterization is conservative
                                                                 // (because being conservative does not ignore Plane Colliders)
    }
}