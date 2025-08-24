using DotRecast.Detour;

namespace Navmesh.Customizations;

[CustomizationTerritory(1237)]
internal class Z1237CosmicExploration01 : NavmeshCustomization
{
    // empty customization to force rebuild for existing users, since SceneDefinition changed in 0.4
    public override int Version => 1;

    public override void CustomizeMesh(DtNavMesh mesh)
    {
        /*
        mesh.OffMeshConnections = [new() {
            pos = [ new(4.5f, 3, -60), new(4.5f, 40.7f, -377) ],
            rad = 1,
        }];

        Service.Log.Debug("im in ur customizer");
        var q = new DtNavMeshQuery(mesh);

        Vector3 start = new(4.5f, 3, -60);
        Vector3 end = new(4.5f, 40.7f, -377);

        q.FindNearestPoly(start.SystemToRecast(), new(5, 5, 5), new DtQueryDefaultFilter(), out var startRef, out _, out _);
        q.FindNearestPoly(end.SystemToRecast(), new(5, 5, 5), new DtQueryDefaultFilter(), out var endRef, out _, out _);
        mesh.GetTileAndPolyByRef(startRef, out var startTile, out var startPoly);
        mesh.GetTileAndPolyByRef(endRef, out var endTile, out var endPoly);

        Service.Log.Debug($"Start tile={startTile.data.header.x}x{startTile.data.header.y} poly={startPoly.index}");
        var i = startTile.polyLinks[startPoly.index];
        while (true)
        {
            if (i == DtNavMesh.DT_NULL_LINK)
            {
                Service.Log.Debug($"no links from poly {startPoly.index}, skipping");
                break;
            }

            Service.Log.Debug($"Poly link #{i} from {startPoly.index} to ref {startTile.links[i].refs:X}");

            var prevI = i;
            i = startTile.links[i].next;
            if (i == DtNavMesh.DT_NULL_LINK)
            {
                Service.Log.Debug($"{prevI} is at end of list");

                var idx = mesh.AllocLink(startTile);
                DtLink link = startTile.links[idx];
                link.refs = endRef;
                link.edge = 2;
                link.side = 0xff;
                link.bmin = link.bmax = 0;
                link.next = startTile.polyLinks[startPoly.index];
                startTile.polyLinks[startPoly.index] = idx;

                break;
            }
        }
        */
    }
}
