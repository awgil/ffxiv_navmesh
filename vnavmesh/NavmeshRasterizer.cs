using DotRecast.Core;
using DotRecast.Recast;
using System.Numerics;

namespace Navmesh;

// utility to rasterize various meshes into a heightfield
public class NavmeshRasterizer
{
    private RcHeightfield _heightfield;
    private RcContext _telemetry;
    private float[] _vertices = new float[3 * 256];
    private int _walkableClimbThreshold; // if two spans have maximums within this number of voxels, their area is 'merged' (higher is selected)
    private float _walkableNormalThreshold; // triangle is considered 'walkable' if it's world-space normal's Y coordinate is >= this

    public NavmeshRasterizer(RcHeightfield heightfield, float walkableNormalThreshold, int walkableMaxClimb, RcContext telemetry)
    {
        _heightfield = heightfield;
        _telemetry = telemetry;
        _walkableClimbThreshold = walkableMaxClimb;
        _walkableNormalThreshold = walkableNormalThreshold;
    }

    public unsafe void Rasterize(SceneExtractor geom, bool includeTerrain, bool includeMeshes, bool includeAnalytic)
    {
        foreach (var (name, mesh) in geom.Meshes)
        {
            var terrain = mesh.MeshFlags.HasFlag(SceneExtractor.MeshFlags.FromTerrain);
            var analytic = mesh.MeshFlags.HasFlag(SceneExtractor.MeshFlags.FromAnalyticShape);
            bool include = terrain ? includeTerrain : analytic ? includeAnalytic : includeMeshes;
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
                        var flags = (p.Flags & ~inst.ForceClearPrimFlags) | inst.ForceSetPrimFlags;
                        if (flags.HasFlag(SceneExtractor.PrimitiveFlags.FlyThrough))
                            continue; // TODO: rasterize to normal heightfield, can't do it right now, since we're using same heightfield for both mesh and volume

                        bool walkable = !flags.HasFlag(SceneExtractor.PrimitiveFlags.ForceUnwalkable);
                        if (walkable)
                        {
                            var v1 = CachedVertex(p.V1);
                            var v2 = CachedVertex(p.V2);
                            var v3 = CachedVertex(p.V3);
                            var v12 = v2 - v1;
                            var v13 = v3 - v1;
                            var normal = Vector3.Normalize(Vector3.Cross(v12, v13));
                            walkable = normal.Y >= _walkableNormalThreshold;
                        }

                        var areaId = !walkable ? 0 : flags.HasFlag(SceneExtractor.PrimitiveFlags.Unlandable) ? Navmesh.UnlandableAreaId : RcConstants.RC_WALKABLE_AREA;
                        RcRasterizations.RasterizeTriangle(_telemetry, _vertices, p.V1, p.V2, p.V3, areaId, _heightfield, _walkableClimbThreshold);
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
