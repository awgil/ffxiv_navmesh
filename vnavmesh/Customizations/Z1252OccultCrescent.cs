using System.Runtime.InteropServices;

namespace Navmesh.Customizations;

[CustomizationTerritory(1252)]
internal class Z1252OccultCrescent : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        if (scene.Meshes.TryGetValue("bg/ex5/03_ocn_o6/btl/o6b1/collision/o6b1_a5_stc02.pcb", out var mesh))
        {
            // bottom stair of second-tier staircase around SW tower is too steep at 51 degrees, even if agent max-climb is set to 55, probably because of rasterization bs, extend it outward by 1y to make slope more gradual
            ref var part0 = ref CollectionsMarshal.AsSpan(mesh.Parts)[221];
            var verts = CollectionsMarshal.AsSpan(part0.Vertices);
            verts[8].X += 1;
            verts[16].X += 1;
        }
    }
}
