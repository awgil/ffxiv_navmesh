using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(155)]
class Z0155CoerthasCentralHighlands : NavmeshCustomization
{
    public override int Version => 6;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // add a fake stair in front of the disconnected second staircase on the second floor of the building in Whitebrim Front
        scene.InsertAABoxCollider(new Vector3(1.5f, 0.15f, 0.2f), new(-417.5f, 221.5f, -288.8f));

        // set the doorstep of Monument Tower as walkable, though you can't land on it
        if (scene.Meshes.TryGetValue("bg/ffxiv/roc_r1/fld/r1f1/collision/r1f1_b7_astr1.pcb", out var tower))
            foreach (var inst in tower.Instances)
                inst.ForceSetPrimFlags |= SceneExtractor.PrimitiveFlags.ForceWalkable;

        var doorScale = new Vector3(1.566f, 2.142f, 0.159f);

        void addDoor(Vector3 rotation, Vector3 translation)
        {
            var doorRot = Matrix4x4.CreateFromYawPitchRoll(rotation.Y, rotation.X, rotation.Z);
            var doorTrans = Matrix4x4.CreateTranslation(translation);
            var mtx = new Matrix4x3(Matrix4x4.CreateScale(doorScale) * doorRot * doorTrans);

            var aabb = new AABB() { Min = mtx.Row3 - doorScale, Max = mtx.Row3 + doorScale };
            var ex = scene.Meshes["<box>"];
            var id = 0xbaadf00d00000001ul + (uint)ex.Instances.Count;
            ex.Instances.Insert(0, new(id, mtx, aabb, default, default));
        }

        addDoor(new(0, 1.484f, 0), new(228.345f, 224.363f, 298.625f));
        addDoor(new(0, -1.484f, 0), new(222.095f, 224.363f, 298.625f));

        addDoor(new(0, -0.088f, 0), new(271.982f, 224.679f, 339.786f));
        addDoor(new(0, 0.088f, 0), new(271.984f, 224.679f, 333.535f));

        addDoor(new(0, 0.088f, 0), new(155.4f, 224.359f, 353.558f));
        addDoor(new(0, -0.087f, 0), new(155.398f, 224.359f, 347.308f));
    }
}
