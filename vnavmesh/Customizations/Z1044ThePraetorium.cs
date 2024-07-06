namespace Navmesh.Customizations;

[CustomizationTerritory(1044)]
class Z1044ThePraetorium : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        Service.Log.Info("Customize Scene");
    }

    public Z1044ThePraetorium()
    {
        Settings.Filtering -= NavmeshSettings.Filter.LedgeSpans; // this allows mesh to go down the ramp from the magitek armor
                                                                 // and its ok in dungeons because non traversable ledges will
                                                                 // have Plane colliders which will not allow the mesh to overhang
                                                                 // anyways even if Rasterization is conservative
                                                                 // (because being conservative does not ignore Plane Colliders)
    }
}
