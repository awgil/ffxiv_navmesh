using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Navmesh.Render;

public class AnalyticMeshBox
{
    public int FirstVertex { get; init; }
    public int FirstPrimitive { get; init; }
    public int NumVertices { get; init; }
    public int NumPrimitives { get; init; }

    private EffectMesh.Data.Builder _builder;

    public AnalyticMeshBox(EffectMesh.Data.Builder builder)
    {
        _builder = builder;
        FirstVertex = builder.NumVertices;
        FirstPrimitive = builder.NumPrimitives;

        builder.AddVertex(new(-1, -1, -1));
        builder.AddVertex(new(-1, -1, +1));
        builder.AddVertex(new(+1, -1, -1));
        builder.AddVertex(new(+1, -1, +1));
        builder.AddVertex(new(-1, +1, -1));
        builder.AddVertex(new(-1, +1, +1));
        builder.AddVertex(new(+1, +1, -1));
        builder.AddVertex(new(+1, +1, +1));

        builder.AddTriangle(0, 1, 2);
        builder.AddTriangle(2, 1, 3);
        builder.AddTriangle(5, 4, 7);
        builder.AddTriangle(7, 4, 6);
        builder.AddTriangle(0, 4, 1);
        builder.AddTriangle(1, 4, 5);
        builder.AddTriangle(2, 3, 6);
        builder.AddTriangle(6, 3, 7);
        builder.AddTriangle(0, 2, 4);
        builder.AddTriangle(4, 2, 6);
        builder.AddTriangle(1, 5, 3);
        builder.AddTriangle(3, 5, 7);

        NumVertices = builder.NumVertices - FirstVertex;
        NumPrimitives = builder.NumPrimitives - FirstPrimitive;
    }

    public EffectMesh.Instance BuildInstance(Vector3 min, Vector3 max, Vector4 color)
    {
        var center = (max + min) * 0.5f;
        var extent = (max - min) * 0.5f;
        return new(new() { M11 = extent.X, M22 = extent.Y, M33 = extent.Z, M41 = center.X, M42 = center.Y, M43 = center.Z }, color);
    }

    public void Add(Vector3 min, Vector3 max, Vector4 color)
    {
        var icnt = _builder.NumInstances;
        _builder.AddInstance(BuildInstance(min, max, color));
        _builder.AddMesh(FirstVertex, FirstPrimitive, NumPrimitives, icnt, 1);
    }
}
