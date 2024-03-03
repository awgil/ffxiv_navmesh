using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Navmesh.Render;

public class AnalyticMeshQuad
{
    public int FirstVertex { get; init; }
    public int FirstPrimitive { get; init; }
    public int NumVertices { get; init; }
    public int NumPrimitives { get; init; }

    private EffectMesh.Data.Builder _builder;

    public AnalyticMeshQuad(EffectMesh.Data.Builder builder)
    {
        _builder = builder;
        FirstVertex = builder.NumVertices;
        FirstPrimitive = builder.NumPrimitives;

        builder.AddVertex(new(+1, +1, 0));
        builder.AddVertex(new(+1, -1, 0));
        builder.AddVertex(new(-1, +1, 0));
        builder.AddVertex(new(-1, -1, 0));

        builder.AddTriangle(0, 1, 2);
        builder.AddTriangle(2, 1, 3);

        NumVertices = builder.NumVertices - FirstVertex;
        NumPrimitives = builder.NumPrimitives - FirstPrimitive;
    }

    public EffectMesh.Instance BuildInstance(Vector3 center, Vector3 wx, Vector3 wy, Vector4 color)
        => new() { WorldColX = new(wx.X, wy.X, 0, center.X), WorldColY = new(wx.Y, wy.Y, 0, center.Y), WorldColZ = new(wx.Z, wy.Z, 0, center.Z), Color = color };

    public void Add(Vector3 center, Vector3 wx, Vector3 wy, Vector4 color)
    {
        var icnt = _builder.NumInstances;
        _builder.AddInstance(BuildInstance(center, wx, wy, color));
        _builder.AddMesh(FirstVertex, FirstPrimitive, NumPrimitives, icnt, 1);
    }
}
