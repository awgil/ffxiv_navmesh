using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(155)]
class Z0155CoerthasCentralHighlands : NavmeshCustomization
{
    public override int Version => 3;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // add a fake stair in front of the disconnected second staircase on the second floor of the building in Whitebrim Front
        var scale = new Vector3(1.5f, 0.15f, 0.2f);
        var transform = Matrix4x3.Identity;
        transform.M11 = scale.X;
        transform.M12 = scale.Y;
        transform.M13 = scale.Z;
        transform.Row3 = new(-417.5f, 220.85f, -288.85f);
        var aabb = new AABB() { Min = transform.Row3 - scale, Max = transform.Row3 + scale };
        scene.Meshes["<box>"].Instances.Add(new(0xbaadf00d00000001ul, transform, aabb, default, default));

        // set the doorstep of Monument Tower as walkable, though you can't land on it
        if (scene.Meshes.TryGetValue("bg/ffxiv/roc_r1/fld/r1f1/collision/r1f1_b7_astr1.pcb", out var tower))
            foreach (var inst in tower.Instances)
                inst.ForceSetPrimFlags |= SceneExtractor.PrimitiveFlags.ForceWalkable;
    }
}
