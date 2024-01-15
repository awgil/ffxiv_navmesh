using DotRecast.Recast;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Navmesh.Render;
using System;
using System.Numerics;

namespace Navmesh.Debug;

public class DebugPolyMesh : DebugRecast
{
    private RcPolyMesh _mesh;
    private UITree _tree;
    private DebugDrawer _dd;
    private EffectMesh.Data? _visu;

    private static int _heightOffset = 0;
    private static Vector4 _colAreaNull = new(0, 0, 0, 0.25f);
    private static Vector4 _colAreaWalkable = new(0, 0.75f, 1.0f, 0.25f);

    public DebugPolyMesh(RcPolyMesh mesh, UITree tree, DebugDrawer dd)
    {
        _mesh = mesh;
        _tree = tree;
        _dd = dd;
    }

    public override void Dispose()
    {
        _visu?.Dispose();
    }

    public void Draw()
    {
        using var nr = _tree.Node("Poly mesh");
        if (!nr.Opened)
            return;

        DrawBaseInfo(_tree, _mesh.bmin, _mesh.bmax, _mesh.cs, _mesh.ch);
        _tree.LeafNode($"Misc: border size={_mesh.borderSize}, max edge error={_mesh.maxEdgeError}, max vertices/poly={_mesh.nvp}");

        using (var nv = _tree.Node($"Vertices ({_mesh.nverts})###verts"))
        {
            if (nv.Opened)
            {
                for (int i = 0; i < _mesh.nverts; ++i)
                {
                    if (_tree.LeafNode($"{i}: {_mesh.verts[3 * i]}x{_mesh.verts[3 * i + 1]}x{_mesh.verts[3 * i + 2]}").SelectedOrHovered)
                        VisualizeVertex(i);
                }
            }
        }

        using (var np = _tree.Node($"Polygons ({_mesh.npolys})###polys"))
        {
            if (np.SelectedOrHovered)
            {
                Visualize();
            }
            if (np.Opened)
            {
                for (int i = 0; i < _mesh.npolys; ++i)
                {
                    using var nprim = _tree.Node(i.ToString());
                    if (nprim.SelectedOrHovered)
                        VisualizePolygon(i);
                    if (!nprim.Opened)
                        continue;
                    var off = i * 2 * _mesh.nvp;
                    for (int j = 0; j < _mesh.nvp; ++j)
                    {
                        var vertex = _mesh.polys[off + j];
                        if (vertex != RcConstants.RC_MESH_NULL_IDX)
                            if (_tree.LeafNode($"Vertex {j}: #{vertex} = {_mesh.verts[3 * vertex]}x{_mesh.verts[3 * vertex + 1]}x{_mesh.verts[3 * vertex + 2]}").SelectedOrHovered && vertex != RcConstants.RC_MESH_NULL_IDX)
                                VisualizeVertex(vertex);
                    }
                    for (int j = 0; j < _mesh.nvp; ++j)
                    {
                        var adj = _mesh.polys[off + _mesh.nvp + j];
                        if (_tree.LeafNode($"Adjacency {j}: {adj}").SelectedOrHovered && adj != RcConstants.RC_MESH_NULL_IDX)
                            VisualizePolygon(adj);
                    }
                }
            }
        }
    }

    private EffectMesh.Data GetOrInitVisualizer()
    {
        if (_visu == null)
        {
            var primsPerPoly = _mesh.nvp - 2;
            _visu = new EffectMesh.Data(_dd.RenderContext, _mesh.nverts, _mesh.npolys * primsPerPoly, 2, false);
            using var builder = _visu.Map(_dd.RenderContext);

            var timer = Timer.Create();

            // one 'instance' per area
            builder.AddInstance(new(Matrix4x3.Identity, _colAreaNull));
            builder.AddInstance(new(Matrix4x3.Identity, _colAreaWalkable));

            for (int i = 0; i < _mesh.nverts; ++i)
                builder.AddVertex(GetVertex(i));

            // one 'mesh' per polygon, each polygon that has <max vertices is padded
            int startingPrimitive = 0;
            for (int i = 0; i < _mesh.npolys; ++i)
            {
                var offset = i * _mesh.nvp * 2;
                for (int j = 2; j < _mesh.nvp; ++j)
                    builder.AddTriangle(_mesh.polys[offset], _mesh.polys[offset + j], _mesh.polys[offset + j - 1]); // flipped for dx order
                var numTriangles = _mesh.polys.AsSpan(offset, _mesh.nvp).IndexOf(RcConstants.RC_MESH_NULL_IDX);
                if (numTriangles < 0)
                    numTriangles = _mesh.nvp;
                numTriangles = Math.Max(numTriangles - 2, 0);

                builder.AddMesh(0, startingPrimitive, numTriangles, _mesh.areas[i] == 0 ? 0 : 1, 1);
                startingPrimitive += primsPerPoly;
            }
            Service.Log.Debug($"polymesh visualization build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return _visu;
    }

    public void Visualize()
    {
        _dd.EffectMesh.Draw(_dd.RenderContext, GetOrInitVisualizer());
        for (int i = 0; i < _mesh.npolys; ++i)
            VisualizeEdges(i);
    }

    private void VisualizePolygon(int index)
    {
        _dd.EffectMesh.DrawSingle(_dd.RenderContext, GetOrInitVisualizer(), index);
        VisualizeEdges(index);
    }

    private void VisualizeEdges(int index)
    {
        var offset = index * _mesh.nvp * 2;
        if (_mesh.polys[offset] != RcConstants.RC_MESH_NULL_IDX)
        {
            var from = GetVertex(_mesh.polys[offset]);
            var adj = _mesh.polys[offset + _mesh.nvp];
            for (int i = 1; i < _mesh.nvp; ++i)
            {
                var v = _mesh.polys[offset + i];
                if (v == RcConstants.RC_MESH_NULL_IDX)
                    break;
                var to = GetVertex(v);
                VisualizeEdge(from, to, adj);
                from = to;
                adj = _mesh.polys[offset + _mesh.nvp + i];
            }
            VisualizeEdge(from, GetVertex(_mesh.polys[offset]), adj);
        }
    }

    private void VisualizeEdge(Vector3 from, Vector3 to, int adj) => _dd.DrawWorldLine(from, to, adj == RcConstants.RC_MESH_NULL_IDX ? 0xd8403000 : 0x80403000, adj == RcConstants.RC_MESH_NULL_IDX ? 2 : 1);
    private void VisualizeVertex(int index) => _dd.DrawWorldPoint(GetVertex(index), 5, 0xff0000ff);

    private Vector3 GetVertex(int index) => _mesh.bmin.RecastToSystem() + new Vector3(_mesh.cs, _mesh.ch, _mesh.cs) * new Vector3(_mesh.verts[3 * index], _mesh.verts[3 * index + 1] + _heightOffset, _mesh.verts[3 * index + 2]);
}
