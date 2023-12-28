using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
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
