using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using System.Numerics;

namespace Navmesh;

// utility to rasterize various meshes into a heightfield
public class NavmeshRasterizer
{
    public RcHeightfield Heightfield;
    private RcConfig _cfg;
    private RcTelemetry _telemetry;
    private float[] _vertices = new float[3 * 256];
    private float _walkableNormalThreshold; // triangle is considered 'walkable' if it's world-space normal's Y coordinate is >= this

    public NavmeshRasterizer(RcConfig cfg, RcVec3f bbMin, RcVec3f bbMax, RcTelemetry telemetry)
    {
        RcCommons.CalcGridSize(bbMin, bbMax, cfg.Cs, out var width, out var height);
        Heightfield = new RcHeightfield(width, height, bbMin, bbMax, cfg.Cs, cfg.Ch, cfg.BorderSize);
        _cfg = cfg;
        _telemetry = telemetry;
        _walkableNormalThreshold = cfg.WalkableSlopeAngle.Degrees().Cos();
    }

    public unsafe void RasterizePCB(FFXIVClientStructs.FFXIV.Common.Component.BGCollision.MeshPCB.FileNode* node, FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3* world)
    {
        if (node == null)
            return;

        // fill vertex buffer
        for (int i = 0, iv = 0; i < node->NumVertsRaw + node->NumVertsCompressed; ++i)
        {
            var v = node->Vertex(i);
            if (world != null)
                v = world->TransformCoordinate(v);
            _vertices[iv++] = v.X;
            _vertices[iv++] = v.Y;
            _vertices[iv++] = v.Z;
        }

        foreach (ref var p in node->Primitives)
        {
            var v1 = CachedVertex(p.V1);
            var v2 = CachedVertex(p.V2);
            var v3 = CachedVertex(p.V3);
            var v12 = v2 - v1;
            var v13 = v3 - v1;
            var normal = Vector3.Normalize(Vector3.Cross(v12, v13));
            var areaId = normal.Y >= _walkableNormalThreshold ? _cfg.WalkableAreaMod.Value : 0;
            RcRasterizations.RasterizeTriangle(Heightfield, _vertices, p.V1, p.V2, p.V3, areaId, _cfg.WalkableClimb, _telemetry);
        }

        RasterizePCB(node->Child1, world);
        RasterizePCB(node->Child2, world);
    }

    private Vector3 CachedVertex(int i)
    {
        var offset = 3 * i;
        return new(_vertices[offset], _vertices[offset + 1], _vertices[offset + 2]);
    }
}
