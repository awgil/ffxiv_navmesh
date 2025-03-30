using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(155)]
class Z0155CoerthasCentralHighlands : NavmeshCustomization
{
    public override int Version => 5;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // add a fake stair in front of the disconnected second staircase on the second floor of the building in Whitebrim Front
        scene.InsertAABoxCollider(new Vector3(1.5f, 0.15f, 0.2f), new(-417.5f, 221.5f, -288.8f));

        // set the doorstep of Monument Tower as walkable, though you can't land on it
        if (scene.Meshes.TryGetValue("bg/ffxiv/roc_r1/fld/r1f1/collision/r1f1_b7_astr1.pcb", out var tower))
            foreach (var inst in tower.Instances)
                inst.ForceSetPrimFlags |= SceneExtractor.PrimitiveFlags.ForceWalkable;
    }
}
