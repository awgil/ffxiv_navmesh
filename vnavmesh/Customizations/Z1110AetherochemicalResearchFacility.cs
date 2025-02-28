using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(1110)]
class Z1110AetherochemicalResearchFacility : NavmeshCustomization
{
    public override int Version => 2;

    public override void CustomizeScene(SceneExtractor scene)
    {
        // colliders blocking lifts
        scene.Meshes.Remove("<box>");

        // the final lift actually moves when being used, so if the client triggers a rebuild at the end of the dungeon for whatever reason, it will break mesh connectivity
        var scale = new Vector3(10, 1, 10);
        var transform = Matrix4x3.Identity;
        transform.M11 = scale.X;
        transform.M22 = scale.Y;
        transform.M33 = scale.Z;
        transform.Row3 = new(221.229f, -60, 95f);
        var aabb = new AABB() { Min = transform.Row3 - scale, Max = transform.Row3 + scale };
        scene.Meshes["<cylinder>"].Instances.Add(new(0xbaadf00d00000001ul, transform, aabb, default, default));
    }
}
