using DotRecast.Detour;
using System.Numerics;

namespace Navmesh.Customizations;

[CustomizationTerritory(1237)]
internal class Z1237CosmicExploration01 : NavmeshCustomization
{
    // empty customization to force rebuild for existing users, since SceneDefinition changed in 0.4
    public override int Version => 2;

    public override void CustomizeScene(SceneExtractor scene)
    {
        scene.InsertCylinderCollider(new Vector3(2, 10, 2), new Vector3(-206.5f, 29, 301.5f));
    }

    public override void CustomizeMesh(DtNavMesh mesh)
    {
        /*
        // TODO: Real off-mesh connections involve adding a line (aka a two-point polygon) to the mesh and connecting each endpoint of the line to the polygon it lies inside
        // this is why this dumb hack of adding an extra fake edge connection doesn't really work, since it results in undesirable connections (i.e. from the front left corner of polygon on the teleporter's surface, instead of the middle of the edge, since there is no vertex there)
        // it should be possible to add *two* points (single-point polys), one to each tile, connecting them to the adjacent polygons, and then adding a link between them, assuming recast can even handle single-point polys

        var q = new DtNavMeshQuery(mesh);

        Vector3 start = new(4.5f, 3, -60);
        Vector3 end = new(4.5f, 40.7f, -377);

        q.FindNearestPoly(start.SystemToRecast(), new(5, 5, 5), new DtQueryDefaultFilter(), out var startRef, out _, out _);
        q.FindNearestPoly(end.SystemToRecast(), new(5, 5, 5), new DtQueryDefaultFilter(), out var endRef, out _, out _);
        mesh.GetTileAndPolyByRef(startRef, out var startTile, out var startPoly);
        mesh.GetTileAndPolyByRef(endRef, out var endTile, out var endPoly);

        Service.Log.Debug($"Start tile={startTile.data.header.x}x{startTile.data.header.y} poly={startPoly.index}");

        var dist = float.MaxValue;
        int bestEdge = -1;
        for (var i = 0; i < startPoly.vertCount; i++)
        {
            var vA = startPoly.verts[i];
            var vB = i + 1 == startPoly.vertCount ? startPoly.verts[0] : startPoly.verts[i + 1];
            var edgeA = RcVecUtils.Create(startTile.data.verts, vA);
            var edgeB = RcVecUtils.Create(startTile.data.verts, vB);
            var thisDist = RcVec3f.DistanceSquared(edgeA, end.SystemToRecast()) + RcVec3f.DistanceSquared(edgeB, end.SystemToRecast());
            if (thisDist < dist)
            {
                dist = thisDist;
                bestEdge = i;
            }
        }

        var idx = mesh.AllocLink(startTile);
        DtLink link = startTile.links[idx];
        link.refs = endRef;
        link.edge = bestEdge;
        link.side = 0xff;
        link.bmin = link.bmax = 0;
        link.next = startTile.polyLinks[startPoly.index];
        startTile.polyLinks[startPoly.index] = idx;
        */
    }
}
