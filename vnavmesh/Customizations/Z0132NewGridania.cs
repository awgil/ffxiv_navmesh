using DotRecast.Detour;
using System.Collections.Generic;

namespace Navmesh.Customizations;

[CustomizationTerritory(132)]
internal class Z0132NewGridania : NavmeshCustomization
{
    public override int Version => 2;

    public override void CustomizeTile(Tile tile)
    {
        // setting the planter as unwalkable has no effect because it's not actually tall enough to block the ground beneath it
        // TODO: smells a bit like a rasterizer bug to me
        foreach (var obj in tile.ObjectsByPath("bg/ffxiv/fst_f1/twn/common/collision/f1t0_a0_plnt1.pcb"))
            obj.Instance.WorldTransform.M22 *= 2;
    }

    public override void CustomizeMesh(DtNavMesh mesh, List<uint> festivalLayers)
    {
        // drop to inn
        LinkPoints(mesh, new(45.03f, -0.13f, 83.1f), new(46.78f, -8.5f, 91.75f));

        // GC entrance to Barracks
        LinkPoints(mesh, new(-65.98f, -0.5f, 3.3f), new(-75.4f, -0.5f, -3.47f), bidirectional: true);

        //bridge to past aetheryte plaza and back
        LinkPoints(mesh, new(-21.689f, -4.302f, 16.821f), new(33.436f, -1.582f, 61.284f), bidirectional: true);
        LinkPoints(mesh, new(53.482f, -0.772f, 71.466f), new(3.601f, -2.647f, 34.097f), bidirectional: true);
    }

    public override void CustomizeMesh(DtNavMesh mesh, List<uint> festivalLayers)
    {
        base.CustomizeMesh(mesh, festivalLayers);

        // GC entrance to Barracks
        LinkPoints(mesh, new(-65.98f, -0.5f, 3.3f), new(-75.4f, -0.5f, -3.47f));

        //bridge to past aetheryte plaza and back
        LinkPoints(mesh, new(-21.689f, -4.302f, 16.821f), new(33.436f, -1.582f, 61.284f));
        LinkPoints(mesh, new(53.482f, -0.772f, 71.466f),  new(3.601f, -2.647f, 34.097f));
    }
}
