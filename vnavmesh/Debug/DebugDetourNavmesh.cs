using DotRecast.Detour;
using DotRecast.Recast;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using ImGuiNET;
using Navmesh.Render;
using System;
using System.Numerics;

namespace Navmesh.Debug;

public class DebugDetourNavmesh : DebugRecast
{
    private struct PerTile
    {
        public EffectMesh.Data? VisuRough; // non-detail mesh
        public EffectMesh.Data? VisuDetail;
    }

    private DtNavMesh _navmesh;
    private DtNavMeshQuery _query;
    private UITree _tree;
    private DebugDrawer _dd;
    private PerTile[] _perTile;

    private static bool _colorByArea = true;
    private static Vector4 _colAreaNull = new(0, 0, 0, 0.25f);
    private static Vector4 _colAreaWalkable = new(0, 0.75f, 1.0f, 0.5f);
    private static Vector4 _colClosedList = new(1.0f, 0.75f, 1.0f, 0.5f);
    private enum InstanceID { Tile, AreaNull, AreaWalkable, ClosedList, Count };

    public DebugDetourNavmesh(DtNavMesh navmesh, DtNavMeshQuery query, UITree tree, DebugDrawer dd)
    {
        _navmesh = navmesh;
        _query = query;
        _tree = tree;
        _dd = dd;
        _perTile = new PerTile[navmesh.GetTileCount()];
    }

    public override void Dispose()
    {
        foreach (var perTile in _perTile)
        {
            perTile.VisuRough?.Dispose();
            perTile.VisuDetail?.Dispose();
        }
    }

    public void Draw()
    {
        using var nr = _tree.Node("Detour navmesh");
        if (!nr.Opened)
            return;

        ImGui.Checkbox("Color by area instead of by tile index", ref _colorByArea);

        ref readonly var param = ref _navmesh.GetParams();
        _tree.LeafNode($"Origin: {param.orig}");
        _tree.LeafNode($"Tile size: {param.tileWidth:f3}x{param.tileHeight:f3} (max {param.maxPolys} polys per tile)");

        using var nt = _tree.Node($"Tiles (max {param.maxTiles})###tiles");
        if (nt.SelectedOrHovered)
            VisualizeWithClosedList();
        if (nt.Opened)
        {
            for (int i = 0; i < param.maxTiles; ++i)
            {
                var tile = _navmesh.GetTile(i);
                if (tile.data == null)
                    continue;
                using var ntile = _tree.Node($"Tile {i} at {tile.data.header.x}x{tile.data.header.y}x{tile.data.header.layer}: flags={tile.flags:X}, salt={tile.salt}, base poly ref={_navmesh.GetPolyRefBase(tile):X}###{i}");
                if (ntile.SelectedOrHovered)
                    VisualizeTile(tile);
                if (!ntile.Opened)
                    continue;

                _tree.LeafNode($"Header: magic={tile.data.header.magic:X}, version={tile.data.header.version}, user-id={tile.data.header.userId}");
                _tree.LeafNode($"Bounds: [{tile.data.header.bmin}]-[{tile.data.header.bmax}] (quant={tile.data.header.bvQuantFactor})");

                using (var np = _tree.Node($"Polygons ({tile.data.header.polyCount})"))
                {
                    if (np.SelectedOrHovered)
                        VisualizeRoughPolygons(tile);
                    if (np.Opened)
                    {
                        for (int j = 0; j < tile.data.header.polyCount; ++j)
                        {
                            var p = tile.data.polys[j];
                            using var ntri = _tree.Node($"{p.index}: {p.vertCount} vertices, flags={p.flags:X}, area={p.GetArea()}, polytype={p.GetPolyType()}");
                            if (ntri.SelectedOrHovered)
                                VisualizeRoughPolygon(tile, p);
                            if (ntri.Opened)
                            {
                                for (int k = 0; k < p.vertCount; ++k)
                                    if (_tree.LeafNode($"{p.verts[k]} ({GetVertex(tile, p.verts[k])}), neighbours={p.neis[k]:X}").SelectedOrHovered)
                                        VisualizeVertex(GetVertex(tile, p.verts[k]));
                            }
                        }
                    }
                }

                using (var nv = _tree.Node($"Vertices ({tile.data.header.vertCount})"))
                {
                    if (nv.Opened)
                    {
                        for (int j = 0; j < tile.data.header.vertCount; ++j)
                            if (_tree.LeafNode($"{j}: {GetVertex(tile, j):f3}").SelectedOrHovered)
                                VisualizeVertex(GetVertex(tile, j));
                    }
                }

                using (var nd = _tree.Node($"Detail ({tile.data.header.detailMeshCount} submeshes, {tile.data.header.detailVertCount} total verts, {tile.data.header.detailTriCount} total prims)"))
                {
                    if (nd.SelectedOrHovered)
                        VisualizeDetailPolygons(tile);
                    if (nd.Opened)
                    {
                        for (int j = 0; j < tile.data.header.detailMeshCount; ++j)
                        {
                            var poly = tile.data.polys[j];
                            ref var sub = ref tile.data.detailMeshes[j];
                            using var nsub = _tree.Node($"{j}: base verts={poly.vertCount}");
                            if (nsub.SelectedOrHovered)
                                VisualizeDetailSubmesh(tile, j);
                            if (!nsub.Opened)
                                continue;

                            using (var np = _tree.Node($"Triangles ({sub.triCount} triangles starting from {sub.triBase})"))
                            {
                                if (np.Opened)
                                {
                                    for (int k = 0; k < sub.triCount; ++k)
                                    {
                                        var offset = (sub.triBase + k) * 4;
                                        var v1i = tile.data.detailTris[offset];
                                        var v2i = tile.data.detailTris[offset + 1];
                                        var v3i = tile.data.detailTris[offset + 2];
                                        var flags = tile.data.detailTris[offset + 3];
                                        var v1 = GetDetailVertex(tile, poly, v1i);
                                        var v2 = GetDetailVertex(tile, poly, v2i);
                                        var v3 = GetDetailVertex(tile, poly, v3i);
                                        if (_tree.LeafNode($"{k}: {v1i}x{v2i}x{v3i} ({v1:f3}x{v2:f3}x{v3:f3}), flags={flags:X}").SelectedOrHovered)
                                            VisualizeTriangle(v1, v2, v3, 0xff000000, 2);
                                    }
                                }
                            }

                            using (var nv = _tree.Node($"Vertices ({sub.vertCount} starting from {sub.vertBase})"))
                            {
                                if (nv.Opened)
                                {
                                    for (int k = 0; k < sub.vertCount; ++k)
                                    {
                                        var v = GetDetailVertex(tile, sub.vertBase + k);
                                        if (_tree.LeafNode($"{k}: {v:f3}").SelectedOrHovered)
                                            VisualizeVertex(v);
                                    }
                                }
                            }
                        }
                    }
                }

                _tree.LeafNode($"Links ({tile.data.header.maxLinkCount} max)");
                _tree.LeafNode($"Bounding volumes ({tile.data.header.bvNodeCount})");
                _tree.LeafNode($"Off-mesh connections ({tile.data.header.offMeshConCount} starting from primitive #{tile.data.header.offMeshBase})");
            }
        }
    }

    private EffectMesh.Data GetOrInitVisualizerRough(int tileIndex)
    {
        ref var perTile = ref _perTile[tileIndex];
        if (perTile.VisuRough == null)
        {
            var primsPerPoly = _navmesh.GetMaxVertsPerPoly() - 2;
            var tile = _navmesh.GetTile(tileIndex);
            perTile.VisuRough = new(_dd.RenderContext, tile.data.header.vertCount, tile.data.header.polyCount * primsPerPoly, (int)InstanceID.Count, false);
            using var builder = perTile.VisuRough.Map(_dd.RenderContext);

            var timer = Timer.Create();

            // instances differ only by color
            builder.AddInstance(new(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3.Identity, IntColor(tileIndex, 0.75f)));
            builder.AddInstance(new(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3.Identity, _colAreaNull));
            builder.AddInstance(new(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3.Identity, _colAreaWalkable));
            builder.AddInstance(new(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3.Identity, _colClosedList));

            for (int i = 0; i < tile.data.header.vertCount; ++i)
                builder.AddVertex(GetVertex(tile, i));

            // one 'mesh' per polygon; by default, assign color based on tile index
            int startingPrimitive = 0;
            for (int i = 0; i < tile.data.header.polyCount; ++i)
            {
                var p = tile.data.polys[i];
                if (p.GetPolyType() != DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION && p.vertCount >= 3)
                {
                    for (int j = 2; j < p.vertCount; ++j)
                        builder.AddTriangle(p.verts[0], p.verts[j], p.verts[j - 1]); // flipped for dx order
                    builder.AddMesh(0, startingPrimitive, p.vertCount - 2, 0, 1);
                    startingPrimitive += p.vertCount - 2;
                }
                else
                {
                    // add empty mesh to ensure indices are matching
                    builder.AddMesh(0, startingPrimitive, 0, 0, 0);
                }
            }
            Service.Log.Debug($"navmesh rough visualization tile #{tileIndex} build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return perTile.VisuRough;
    }

    private EffectMesh.Data GetOrInitVisualizerDetail(int tileIndex)
    {
        ref var perTile = ref _perTile[tileIndex];
        if (perTile.VisuDetail == null)
        {
            var tile = _navmesh.GetTile(tileIndex);
            perTile.VisuDetail = new(_dd.RenderContext, tile.data.header.vertCount + tile.data.header.detailVertCount, tile.data.header.detailTriCount, (int)InstanceID.Count, false);
            using var builder = perTile.VisuDetail.Map(_dd.RenderContext);

            var timer = Timer.Create();

            // instances differ only by color
            builder.AddInstance(new(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3.Identity, IntColor(tileIndex, 0.75f)));
            builder.AddInstance(new(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3.Identity, _colAreaNull));
            builder.AddInstance(new(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3.Identity, _colAreaWalkable));
            builder.AddInstance(new(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3.Identity, _colClosedList));

            for (int i = 0; i < tile.data.header.vertCount; ++i)
                builder.AddVertex(GetVertex(tile, i));
            for (int i = 0; i < tile.data.header.detailVertCount; ++i)
                builder.AddVertex(GetDetailVertex(tile, i));

            // one 'mesh' per polygon; by default, assign color based on tile index
            int startingPrimitive = 0;
            for (int i = 0; i < tile.data.header.detailMeshCount; ++i)
            {
                var poly = tile.data.polys[i];
                ref var sub = ref tile.data.detailMeshes[i];
                for (int j = 0; j < sub.triCount; ++j)
                {
                    var offset = (sub.triBase + j) * 4;
                    var v1i = tile.data.detailTris[offset];
                    var v2i = tile.data.detailTris[offset + 1];
                    var v3i = tile.data.detailTris[offset + 2];
                    var v1 = v1i < poly.vertCount ? poly.verts[v1i] : tile.data.header.vertCount + tile.data.detailMeshes[poly.index].vertBase + v1i - poly.vertCount;
                    var v2 = v2i < poly.vertCount ? poly.verts[v2i] : tile.data.header.vertCount + tile.data.detailMeshes[poly.index].vertBase + v2i - poly.vertCount;
                    var v3 = v3i < poly.vertCount ? poly.verts[v3i] : tile.data.header.vertCount + tile.data.detailMeshes[poly.index].vertBase + v3i - poly.vertCount;
                    builder.AddTriangle(v1, v3, v2); // flipped for dx order
                }
                builder.AddMesh(0, startingPrimitive, sub.triCount, 0, 1);
                startingPrimitive += sub.triCount;
            }
            Service.Log.Debug($"navmesh detail visualization tile #{tileIndex} build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return perTile.VisuDetail;
    }

    private void VisualizeWithClosedList()
    {
        for (int i = 0; i < _perTile.Length; ++i)
        {
            var tile = _navmesh.GetTile(i);
            if (tile.data != null)
                VisualizeTile(tile);
        }
    }

    private void VisualizeTile(DtMeshTile tile)
    {
        // TODO: ...
    }

    private void VisualizeRoughPolygons(DtMeshTile tile)
    {
        var visu = GetOrInitVisualizerRough(tile.index);
        _dd.EffectMesh.Bind(_dd.RenderContext, false);
        visu.Bind(_dd.RenderContext);
        for (int i = 0; i < tile.data.header.polyCount; ++i)
            VisualizeRoughPolygon(tile, visu, tile.data.polys[i], false);
    }

    private void VisualizeRoughPolygon(DtMeshTile tile, DtPoly poly)
    {
        var visu = GetOrInitVisualizerRough(tile.index);
        _dd.EffectMesh.Bind(_dd.RenderContext, false);
        visu.Bind(_dd.RenderContext);
        VisualizeRoughPolygon(tile, visu, poly, true);
    }

    // effect + data are expected to be already bound
    private void VisualizeRoughPolygon(DtMeshTile tile, EffectMesh.Data visu, DtPoly poly, bool highlight)
    {
        if (poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
        {
            if (poly.vertCount < 3)
                return;
            // triangles
            var instance = _query.IsInClosedList(DtNavMesh.EncodePolyId(tile.salt, tile.index, poly.index)) ? InstanceID.ClosedList : !_colorByArea ? InstanceID.Tile : poly.GetArea() == 0 ? InstanceID.AreaNull : InstanceID.AreaWalkable;
            var mesh = visu.Meshes[poly.index] with { FirstInstance = (int)instance };
            visu.DrawManual(_dd.RenderContext, mesh);

            // edges
            var from = GetVertex(tile, poly.verts[0]);
            for (int i = 0; i < poly.vertCount; ++i)
            {
                var to = GetVertex(tile, poly.verts[i == poly.vertCount - 1 ? 0 : i + 1]);
                var inner = poly.neis[i] != 0;
                uint color = 0xd8403000;
                if (inner)
                {
                    color = 0x20403000;
                    if ((poly.neis[i] & DtNavMesh.DT_EXT_LINK) != 0)
                    {
                        bool con = false;
                        for (int k = tile.polyLinks[poly.index]; k != DtNavMesh.DT_NULL_LINK; k = tile.links[k].next)
                        {
                            if (tile.links[k].edge == i)
                            {
                                con = true;
                                break;
                            }
                        }
                        color = con ? 0x30ffffffu : 0x30000000u;
                    }
                }
                if (highlight)
                    color |= 0xff000000;
                _dd.DrawWorldLine(from, to, color, highlight ? 3 : inner ? 1 : 2);
                from = to;
            }

            // vertices
            for (int i = 0; i < poly.vertCount; ++i)
                _dd.DrawWorldPoint(GetVertex(tile, poly.verts[i]), 3, 0xff000000);
        }
        else
        {
            // TODO: ...
        }
    }

    private void VisualizeDetailPolygons(DtMeshTile tile)
    {
        var visu = GetOrInitVisualizerDetail(tile.index);
        _dd.EffectMesh.Bind(_dd.RenderContext, false);
        visu.Bind(_dd.RenderContext);
        for (int i = 0; i < tile.data.header.detailMeshCount; ++i)
            VisualizeDetailSubmeshWithEdges(tile, visu, tile.data.polys[i], false);

        // all vertices
        for (int i = 0; i < tile.data.header.vertCount; ++i)
            _dd.DrawWorldPointFilled(GetVertex(tile, i), 3, 0xff00ff00);
        for (int i = 0; i < tile.data.header.detailVertCount; ++i)
            _dd.DrawWorldPointFilled(GetDetailVertex(tile, i), 2, 0xff0000ff);
    }

    private void VisualizeDetailSubmesh(DtMeshTile tile, int index)
    {
        var poly = tile.data.polys[index];
        ref var sub = ref tile.data.detailMeshes[poly.index];

        var visu = GetOrInitVisualizerDetail(tile.index);
        _dd.EffectMesh.Bind(_dd.RenderContext, false);
        visu.Bind(_dd.RenderContext);
        VisualizeDetailSubmeshWithEdges(tile, visu, poly, true);

        // vertices
        for (int i = 0; i < poly.vertCount; ++i)
            _dd.DrawWorldPointFilled(GetVertex(tile, poly.verts[i]), 3, 0xff00ff00);
        for (int i = 0; i < sub.vertCount; ++i)
            _dd.DrawWorldPointFilled(GetDetailVertex(tile, sub.vertBase + i), 2, 0xff0000ff);
    }

    private void VisualizeDetailSubmeshWithEdges(DtMeshTile tile, EffectMesh.Data visu, DtPoly poly, bool highlight)
    {
        // triangles
        var instance = _query.IsInClosedList(DtNavMesh.EncodePolyId(tile.salt, tile.index, poly.index)) ? InstanceID.ClosedList : !_colorByArea ? InstanceID.Tile : poly.GetArea() == 0 ? InstanceID.AreaNull : InstanceID.AreaWalkable;
        var mesh = visu.Meshes[poly.index] with { FirstInstance = (int)instance };
        visu.DrawManual(_dd.RenderContext, mesh);

        // edges
        ref var sub = ref tile.data.detailMeshes[poly.index];
        var color = highlight ? 0xff000000 : 0x80000000;
        for (int i = 0; i < sub.triCount; ++i)
        {
            var offset = (sub.triBase + i) * 4;
            var v1i = tile.data.detailTris[offset];
            var v2i = tile.data.detailTris[offset + 1];
            var v3i = tile.data.detailTris[offset + 2];
            var flags = tile.data.detailTris[offset + 3];
            var v1 = GetDetailVertex(tile, poly, v1i);
            var v2 = GetDetailVertex(tile, poly, v2i);
            var v3 = GetDetailVertex(tile, poly, v3i);
            _dd.DrawWorldLine(v1, v2, color, (DtNavMesh.GetDetailTriEdgeFlags(flags, 0) & DtDetailTriEdgeFlags.DT_DETAIL_EDGE_BOUNDARY) != 0 ? 2 : 1);
            _dd.DrawWorldLine(v2, v3, color, (DtNavMesh.GetDetailTriEdgeFlags(flags, 1) & DtDetailTriEdgeFlags.DT_DETAIL_EDGE_BOUNDARY) != 0 ? 2 : 1);
            _dd.DrawWorldLine(v3, v1, color, (DtNavMesh.GetDetailTriEdgeFlags(flags, 2) & DtDetailTriEdgeFlags.DT_DETAIL_EDGE_BOUNDARY) != 0 ? 2 : 1);
        }
    }

    private void VisualizeTriangle(Vector3 v1, Vector3 v2, Vector3 v3, uint color, int thickness)
    {
        _dd.DrawWorldLine(v1, v2, color, thickness);
        _dd.DrawWorldLine(v2, v3, color, thickness);
        _dd.DrawWorldLine(v3, v1, color, thickness);
    }

    private void VisualizeVertex(Vector3 v) => _dd.DrawWorldPoint(v, 5, 0xff0000ff, 2);

    private Vector3 GetVertex(DtMeshTile tile, int i) => new(tile.data.verts[i * 3], tile.data.verts[i * 3 + 1], tile.data.verts[i * 3 + 2]);
    private Vector3 GetDetailVertex(DtMeshTile tile, int i) => new(tile.data.detailVerts[i * 3], tile.data.detailVerts[i * 3 + 1], tile.data.detailVerts[i * 3 + 2]);
    private Vector3 GetDetailVertex(DtMeshTile tile, DtPoly poly, int localIndex) => localIndex < poly.vertCount
        ? GetVertex(tile, poly.verts[localIndex])
        : GetDetailVertex(tile, tile.data.detailMeshes[poly.index].vertBase + localIndex - poly.vertCount);
}
