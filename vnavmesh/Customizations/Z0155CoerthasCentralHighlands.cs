using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(155)]
class Z0155CoerthasCentralHighlands : NavmeshCustomization
{
    public override int Version => 4;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // add a fake stair in front of the disconnected second staircase on the second floor of the building in Whitebrim Front
        scene.InsertAABoxCollider(new Vector3(1.5f, 0.15f, 0.2f), new(-417.5f, 221.5f, -288.8f));

        // set the doorstep of Monument Tower as walkable, though you can't land on it
        if (scene.Meshes.TryGetValue("bg/ffxiv/roc_r1/fld/r1f1/collision/r1f1_b7_astr1.pcb", out var tower))
            foreach (var inst in tower.Instances)
                inst.ForceSetPrimFlags |= SceneExtractor.PrimitiveFlags.ForceWalkable;

        // add solid box beneath Camp Dragonhead, where the floor for the entire area lies exactly on a voxel tile boundary
        scene.InsertAABoxCollider(new AABB()
        {
            Min = new(177.19f, 300, -296.24f),
            Max = new(280.15f, 302, -160.3f)
        });

        // shift the entire castle down by 0.05 units so that the bridge doesn't lie on a tile boundary either
        if (scene.Meshes.TryGetValue("bg/ffxiv/roc_r1/fld/r1f1/collision/r1f1_b3_ruin1.pcb", out var camp))
        {
            var inst = camp.Instances[0];
            inst.WorldTransform.Row3.Y -= 0.05f;
            inst.WorldBounds.Min.Y -= 0.05f;
            inst.WorldBounds.Max.Y -= 0.05f;
        }
    }
}
