using DotRecast.Recast;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using System;
using System.Numerics;

namespace Navmesh;

public interface IMesh
{
    public int NumVertices();
    public int NumTriangles();
    public Vector3 Vertex(int index);
    public (int, int, int) Triangle(int index);
}

public unsafe class PCBMesh : IMesh
{
    private MeshPCB.FileNode* _node;

    public PCBMesh(MeshPCB.FileNode* node) => _node = node;

    public int NumVertices() => _node->NumVertsRaw + _node->NumVertsCompressed;
    public int NumTriangles() => _node->NumPrims;
    public Vector3 Vertex(int index) => _node->Vertex(index);
    public (int, int, int) Triangle(int index)
    {
        ref var p = ref _node->Primitives[index];
        return (p.V1, p.V3, p.V2); // change winding to what dx expects
    }
}

public class RcPolyMeshPrimitive : IMesh
{
    private RcPolyMesh _mesh;
    private int _offset;
    private int _nverts;

    public RcPolyMeshPrimitive(RcPolyMesh mesh, int index)
    {
        _mesh = mesh;
        _offset = index * 2 * mesh.nvp;
        _nverts = _mesh.polys.AsSpan(_offset, _mesh.nvp).IndexOf(RcConstants.RC_MESH_NULL_IDX);
        if (_nverts < 0)
            _nverts = mesh.nvp;
    }

    public int NumVertices() => _nverts;
    public int NumTriangles() => Math.Max(0, _nverts - 2);
    public Vector3 Vertex(int index) => ConvertVertex(_mesh.polys[_offset + index]);
    public (int, int, int) Triangle(int index) => (0, index + 2, index + 1); // change winding to what dx expects

    private Vector3 ConvertVertex(int i) => _mesh.bmin.RecastToSystem() + new Vector3(_mesh.cs, _mesh.ch, _mesh.cs) * new Vector3(_mesh.verts[3 * i], _mesh.verts[3 * i + 1], _mesh.verts[3 * i + 2]);
}
