using DotRecast.Core;
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

    public NavmeshRasterizer(RcConfig cfg, Vector3 bbMin, Vector3 bbMax, RcTelemetry telemetry)
    {
        var rcMin = bbMin.SystemToRecast();
        var rcMax = bbMax.SystemToRecast();
        RcCommons.CalcGridSize(rcMin, rcMax, cfg.Cs, out var width, out var height);
        Heightfield = new RcHeightfield(width, height, rcMin, rcMax, cfg.Cs, cfg.Ch, cfg.BorderSize);
        _cfg = cfg;
        _telemetry = telemetry;
        _walkableNormalThreshold = cfg.WalkableSlopeAngle.Degrees().Cos();
    }

    public unsafe void Rasterize(CollisionGeometryExtractor geom, bool includeStreamed, bool includeLooseMeshes, bool includeAnalytic)
    {
        foreach (var (name, mesh) in geom.Meshes)
        {
            var streamed = mesh.Flags.HasFlag(CollisionGeometryExtractor.Flags.FromStreamed);
            var analytic = mesh.Flags.HasFlag(CollisionGeometryExtractor.Flags.FromAnalyticShape);
            bool include = streamed ? includeStreamed : analytic ? includeAnalytic : includeLooseMeshes;
            if (!include)
                continue;

            foreach (var world in mesh.Instances)
            {
                foreach (var part in mesh.Parts)
                {
                    // fill vertex buffer
                    int iv = 0;
                    foreach (var v in part.Vertices)
                    {
                        var w = world.TransformCoordinate(v);
                        _vertices[iv++] = v.X;
                        _vertices[iv++] = v.Y;
                        _vertices[iv++] = v.Z;
                    }

                    foreach (var p in part.Primitives)
                    {
                        var v1 = CachedVertex(p.v1);
                        var v2 = CachedVertex(p.v2);
                        var v3 = CachedVertex(p.v3);
                        var v12 = v2 - v1;
                        var v13 = v3 - v1;
                        var normal = Vector3.Normalize(Vector3.Cross(v12, v13));
                        var areaId = normal.Y >= _walkableNormalThreshold ? _cfg.WalkableAreaMod.Value : 0;
                        RcRasterizations.RasterizeTriangle(Heightfield, _vertices, p.v1, p.v2, p.v3, areaId, _cfg.WalkableClimb, _telemetry);
                    }
                }
            }
        }
    }

    private Vector3 CachedVertex(int i)
    {
        var offset = 3 * i;
        return new(_vertices[offset], _vertices[offset + 1], _vertices[offset + 2]);
    }
}
