using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(146)]
internal class Z0146SouthernThanalan : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeTile(Tile tile)
    {
        List<InstanceWithMesh> slidingDoors = [];
        // sliding door is excluded from the mesh due to being in a DoorRange, but it actually remains solid when it raises and partially blocks flying, so we add a fake collider representing the raised door
        // TODO: the resulting gap is actually too small to contain an unblocked voxel, should verify that the gates can be flown through after a voxel rework
        foreach (var archway in tile.ObjectsByPath("bgcommon/world/amj/001/collision/w_amj_001_06a.pcb"))
        {
            var mtx = Matrix.CreateTransform(new Vector3(0, 8.384f, 0), Quaternion.Identity, new Vector3(3.8f, 3.884f, 0.639f))
                * archway.Instance.WorldTransform.FullMatrix();
            slidingDoors.Add(SceneTool.CreateSimpleBox(archway.Instance.Id + 0x100, mtx, archway.Instance.WorldBounds));
        }

        foreach (var s in slidingDoors)
            tile.Objects.TryAdd(s.Instance.Id, s);
    }
}
