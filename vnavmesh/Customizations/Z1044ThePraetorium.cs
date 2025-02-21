using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;

namespace Navmesh.Customizations;

[CustomizationTerritory(1044)]
class Z1044ThePraetorium : NavmeshCustomization
{
    public override int Version => 2;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // two colliders block a gate on the second floor (that used to be unblocked maybe? before prae rework?)
        // the content director doesn't activate them until the cutscene ends, so automatic mesh rebuild will ignore them, which then breaks pathfinding on that floor
        // to fix, we add another copy of those colliders manually
        scene.Meshes["<box>"].Instances.Add(new(0xbaadf00d00000001ul, new Matrix4x3
        {
            Row0 = new(-5.985012f, 0, 10.366346f),
            Row1 = new(0, 5.177295f, 0),
            Row2 = new(-0.30199695f, 0, -0.174358f),
            Row3 = new(210.25f, 161.17729f, -31.60993f)
        }, new AABB()
        {
            Min = new(203.963f, 156f, -42.150635f),
            Max = new(216.537f, 166.35458f, -21.069225f)
        }, default, default));
        scene.Meshes["<box>"].Instances.Add(new(0xbaadf00e00000001ul, new Matrix4x3
        {
            Row0 = new(-5.9850097f, 0, 10.366343f),
            Row1 = new(0, 4.5776777f, 0),
            Row2 = new(-0.73718256f, 0, -0.4256125f),
            Row3 = new(210.25f, 168.57767f, -31.60993f)
        }, new AABB()
        {
            Min = new(203.5278f, 163.99998f, -42.401886f),
            Max = new(216.9722f, 173.15535f, -20.817974f)
        }, default, default));
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
