using DotRecast.Core;
using DotRecast.Recast;
using System.Numerics;

namespace Navmesh;

// utility to rasterize various meshes into a heightfield
public class NavmeshRasterizer
{
    private RcHeightfield _heightfield;
    private RcTelemetry _telemetry;
    private float[] _vertices = new float[3 * 256];
    private int _walkableClimbThreshold; // if two spans have maximums within this number of voxels, their area is 'merged' (higher is selected)
    private float _walkableNormalThreshold; // triangle is considered 'walkable' if it's world-space normal's Y coordinate is >= this

    public NavmeshRasterizer(RcHeightfield heightfield, Angle walkableMaxSlope, int walkableMaxClimb, RcTelemetry telemetry)
    {
        _heightfield = heightfield;
        _telemetry = telemetry;
        _walkableClimbThreshold = walkableMaxClimb;
        _walkableNormalThreshold = walkableMaxSlope.Cos();
    }

    public unsafe void Rasterize(SceneExtractor geom, bool includeStreamed, bool includeLooseMeshes, bool includeAnalytic)
    {
        foreach (var (name, mesh) in geom.Meshes)
        {
            var streamed = mesh.Flags.HasFlag(SceneExtractor.Flags.FromTerrain);
            var analytic = mesh.Flags.HasFlag(SceneExtractor.Flags.FromAnalyticShape);
            bool include = streamed ? includeStreamed : analytic ? includeAnalytic : includeLooseMeshes;
            if (!include)
                continue;

            foreach (var inst in mesh.Instances)
            {
                if (inst.WorldBounds.Max.X <= _heightfield.bmin.X || inst.WorldBounds.Max.Z <= _heightfield.bmin.Z || inst.WorldBounds.Min.X >= _heightfield.bmax.X || inst.WorldBounds.Min.Z >= _heightfield.bmax.Z)
                    continue;

                foreach (var part in mesh.Parts)
                {
                    // fill vertex buffer
                    int iv = 0;
                    foreach (var v in part.Vertices)
                    {
                        var w = inst.WorldTransform.TransformCoordinate(v);
                        _vertices[iv++] = w.X;
                        _vertices[iv++] = w.Y;
                        _vertices[iv++] = w.Z;
                    }

                    // TODO: move area-id calculations to extraction step + store indices in a form that allows using RasterizeTriangles()
                    foreach (var p in part.Primitives)
                    {
                        var v1 = CachedVertex(p.v1);
                        var v2 = CachedVertex(p.v2);
                        var v3 = CachedVertex(p.v3);
                        var v12 = v2 - v1;
                        var v13 = v3 - v1;
                        var normal = Vector3.Normalize(Vector3.Cross(v12, v13));
                        var areaId = normal.Y >= _walkableNormalThreshold ? RcConstants.RC_WALKABLE_AREA : 0;
                        RcRasterizations.RasterizeTriangle(_heightfield, _vertices, p.v1, p.v2, p.v3, areaId, _walkableClimbThreshold, _telemetry);
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
