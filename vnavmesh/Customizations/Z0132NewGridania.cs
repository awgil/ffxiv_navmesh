namespace Navmesh.Customizations;

using System.Collections.Generic;
using System.Numerics;
using DotRecast.Detour;

[CustomizationTerritory(132)]
internal class Z0132NewGridania : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        if (scene.Meshes.TryGetValue("bg/ffxiv/fst_f1/twn/common/collision/f1t0_a0_plnt1.pcb", out var mesh))
            foreach (var inst in mesh.Instances)
                // setting the planter as unwalkable has no effect and i don't feel like figuring out why so just double the height instead
                inst.WorldTransform.M22 *= 2;
    }

    public override void CustomizeMesh(DtNavMesh mesh, List<uint> festivalLayers)
    {
        base.CustomizeMesh(mesh, festivalLayers);

        LinkPoints(mesh, new Vector3(45.03f, -0.13f, 83.1f), new Vector3(46.78f, -8.5f, 91.75f)); // drop down to the inn entrance
    }
}
