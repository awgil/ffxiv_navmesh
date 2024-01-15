using DotRecast.Recast;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Navmesh.Render;
using System.Numerics;

namespace Navmesh.Debug;

public class DebugPolyMeshDetail : DebugRecast
{
    private RcPolyMeshDetail _mesh;
    private UITree _tree;
    private DebugDrawer _dd;
    private EffectMesh.Data? _visu;

    public DebugPolyMeshDetail(RcPolyMeshDetail mesh, UITree tree, DebugDrawer dd)
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
        using var nr = _tree.Node("Detail poly mesh");
        if (!nr.Opened)
            return;

        _tree.LeafNode($"Total size: {_mesh.nverts} vertices, {_mesh.ntris} triangles");
        using var nm = _tree.Node($"Meshes ({_mesh.nmeshes})###meshes");
        if (nm.SelectedOrHovered)
            Visualize();
        if (!nm.Opened)
            return;

        for (int i = 0; i < _mesh.nmeshes; ++i)
        {
            var vertexBase = _mesh.meshes[i * 4];
            var vertexCount = _mesh.meshes[i * 4 + 1];
            var triBase = _mesh.meshes[i * 4 + 2];
            var triCount = _mesh.meshes[i * 4 + 3];
            using var nmesh = _tree.Node($"Mesh {i}: {vertexCount} vertices starting at {vertexBase}, {triCount} triangles starting at {triBase}");
            if (nmesh.SelectedOrHovered)
                VisualizeMesh(i);
            if (!nmesh.Opened)
                continue;

            for (int j = 0; j < triCount; ++j)
            {
                var v1 = _mesh.tris[(triBase + j) * 4];
                var v2 = _mesh.tris[(triBase + j) * 4 + 1];
                var v3 = _mesh.tris[(triBase + j) * 4 + 2];
                var flags = _mesh.tris[(triBase + j) * 4 + 3];
                using var ntri = _tree.Node($"Triangle {j}: {v1}x{v2}x{v3} ({GetVertex(vertexBase + v1):f3}x{GetVertex(vertexBase + v2):f3}x{GetVertex(vertexBase + v3):f3}), flags={flags:X}");
                if (ntri.SelectedOrHovered)
                    VisualizeTriangle(triBase + j, vertexBase, 3, 3);
            }
        }
    }

    private EffectMesh.Data GetOrInitVisualizer()
    {
        if (_visu == null)
        {
            _visu = new EffectMesh.Data(_dd.RenderContext, _mesh.nverts, _mesh.ntris, _mesh.nmeshes, false);
            using var builder = _visu.Map(_dd.RenderContext);

            var timer = Timer.Create();
            for (int i = 0; i < _mesh.nverts; ++i)
                builder.AddVertex(GetVertex(i));
            for (int i = 0; i < _mesh.ntris; ++i)
                builder.AddTriangle(_mesh.tris[i * 4], _mesh.tris[i * 4 + 2], _mesh.tris[i * 4 + 1]); // invert winding for dx
            for (int i = 0; i < _mesh.nmeshes; ++i)
            {
                builder.AddInstance(new(Matrix4x3.Identity, IntColor(i, 0.75f)));
                builder.AddMesh(_mesh.meshes[i * 4], _mesh.meshes[i * 4 + 2], _mesh.meshes[i * 4 + 3], i, 1);
            }
            Service.Log.Debug($"detail polymesh visualization build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return _visu;
    }

    public void Visualize()
    {
        _dd.EffectMesh.Draw(_dd.RenderContext, GetOrInitVisualizer());
        for (int i = 0; i < _mesh.nmeshes; ++i)
            VisualizeMeshEdges(i);
    }

    public void VisualizeMesh(int i)
    {
        _dd.EffectMesh.DrawSingle(_dd.RenderContext, GetOrInitVisualizer(), i);
        VisualizeMeshEdges(i);
    }

    private void VisualizeMeshEdges(int i)
    {
        var vertexBase = _mesh.meshes[i * 4];
        var triBase = _mesh.meshes[i * 4 + 2];
        var triCount = _mesh.meshes[i * 4 + 3];
        for (int j = 0; j < triCount; ++j)
            VisualizeTriangle(triBase + j, vertexBase, 1, 2);
    }

    private void VisualizeTriangle(int index, int vertexBase, int thicknessInternal, int thicknessExternal)
    {
        var v1 = GetVertex(vertexBase + _mesh.tris[index * 4]);
        var v2 = GetVertex(vertexBase + _mesh.tris[index * 4 + 1]);
        var v3 = GetVertex(vertexBase + _mesh.tris[index * 4 + 2]);
        var flags = _mesh.tris[index * 4 + 3];
        bool ext1 = (flags & 3) != 0;
        bool ext2 = ((flags >> 2) & 3) != 0;
        bool ext3 = ((flags >> 4) & 3) != 0;
        _dd.DrawWorldLine(v1, v2, 0x80000000, ext1 ? thicknessExternal : thicknessInternal);
        _dd.DrawWorldLine(v2, v3, 0x80000000, ext2 ? thicknessExternal : thicknessInternal);
        _dd.DrawWorldLine(v3, v1, 0x80000000, ext3 ? thicknessExternal : thicknessInternal);
    }

    private Vector3 GetVertex(int index) => new(_mesh.verts[3 * index], _mesh.verts[3 * index + 1], _mesh.verts[3 * index + 2]);
}
