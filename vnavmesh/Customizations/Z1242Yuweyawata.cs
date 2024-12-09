using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(1242)]
class Z1242Yuweyawata : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // avoid the hole in last boss arena
        var scale = new Vector3(11, 1, 11);
        var transform = Matrix4x3.Identity;
        transform.M11 = scale.X;
        transform.M22 = scale.Y;
        transform.M33 = scale.Z;
        transform.Row3 = new(34, -87.9f, -710);
        var aabb = new AABB() { Min = transform.Row3 - scale, Max = transform.Row3 + scale };
        scene.Meshes["<cylinder>"].Instances.Add(new(0xbaadf00d00000001ul, transform, aabb, SceneExtractor.PrimitiveFlags.ForceUnwalkable, default));
    }
}
