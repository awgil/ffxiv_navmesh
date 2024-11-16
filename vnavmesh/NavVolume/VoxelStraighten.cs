namespace Navmesh.NavVolume;

// utility to build a 'straight' path (funnel/string-pulling) from the path returned by query
// we exploit heavily the axis-oriented nature of voxel map
// - we start with a path containing starting point (if it's inside starting voxel - otherwise we find the closest point on the boundary of the starting voxel)
// - each successive voxel touches previous by a face; if last point is inside/on the border of the same voxel, the whole face is the new funnel
// - otherwise we project the previous funnel on the plane of the new face and calculate intersection (unfortunately, this means that eventually funnel can become an arbitrary polygon; some projected points could be at infinity, but we still care about directions...)
// - if intersection is non-empty, it's our new funnel
// - otherwise - we add a new point on the edge of the previous funnel (TODO: how to determine it?) and set new face as our new funnel
//public class VoxelStraighten
//{
//    private VoxelMap _volume;
//    private List<Vector3> _path = new();
//    private (int x, int y, int z) _prevVoxel;
//    private (int dx, int dy, int dz) _funnelNormal;
//    private Vector3 _funnelCenter;
//    private Vector3 _funnelSize;
//    private (int dx, int dy, int dz) _pendingEdgeDir;
//    private List<Vector3> _pendingEdgeCenters = new();

//    public VoxelStraighten(VoxelMap volume)
//    {
//        _volume = volume;
//    }

//    public void Start(Vector3 startPoint, int startVoxel)
//    {
//        _path.Clear();
//        _prevVoxel = _volume.IndexToVoxel(startVoxel);
//        _prevPlaneNormal = default;
//        _pendingEdgeDir = default;
//        _pendingEdgeCenters.Clear();
//        if (startVoxel < 0)
//        {
//            Service.Log.Error($"Bad start: {startVoxel:X} ({startPoint})");
//            return;
//        }
//        _path.Add(ClosestPointOnVoxelBoundary(startPoint, _prevVoxel));
//    }

//    public void AddVoxel(int voxel)
//    {
//        var coord = _volume.IndexToVoxel(voxel);
//        var dx = coord.x - _prevVoxel.x;
//        var dy = coord.y - _prevVoxel.y;
//        var dz = coord.z - _prevVoxel.z;
//        var success = (dx, dy, dz) switch
//        {
//            (0, 0, _) => AddFaceXY(dz),
//            (0, _, 0) => AddFaceXZ(dy),
//            (_, 0, 0) => AddFaceYZ(dx),
//            (0, _, _) => AddEdgeX(dy, dz),
//            (_, 0, _) => AddEdgeY(dx, dz),
//            (_, _, 0) => AddEdgeZ(dx, dy),
//            _ => AddVertex(dx, dy, dz)
//        };
//        if (!success)
//            throw new Exception($"Bad transition: {_prevVoxel} -> {coord}");
//        _prevVoxel = coord;
//    }

//    private Vector3 ClosestPointOnVoxelBoundary(Vector3 p, (int x, int y, int z) v)
//    {
//        var center = _volume.VoxelToWorld(v);
//        var dist = p - center;
//        var halfSize = 0.5f * _volume.CellSize;
//        var x = Math.Abs(dist.X) <= halfSize.X ? p.X : dist.X < 0 ? center.X - halfSize.X : center.X + halfSize.X;
//        var y = Math.Abs(dist.Y) <= halfSize.Y ? p.Y : dist.Y < 0 ? center.Y - halfSize.Y : center.Y + halfSize.Y;
//        var z = Math.Abs(dist.Z) <= halfSize.Z ? p.Z : dist.Z < 0 ? center.Z - halfSize.Z : center.Z + halfSize.Z;
//        return new(x, y, z);
//    }

//    private bool AddFaceXY(int dz)
//    {
//        if (_prevPlaneNormal.dz != 0)
//        {
//            // successive transition in same direction - the funnel is strictly smaller, so just update plane center
//            if (_prevPlaneNormal.dz != dz)
//                return false;
//            _prevPlaneCenter.Z += dz * _volume.CellSize.Z;
//            return true;
//        }
//        else if (_prevPlaneNormal.dx != 0)
//        {
//            // we're rotating: YZ -> XY
//        }
//        else if (_prevPlaneNormal.dy != 0)
//        {
//            // we're rotating: XZ -> XY
//        }
//        else
//        {
//            // previous transition was not a plane - see if we need to collapse edges
//        }
//    }

//    private bool AddFaceXZ(int dy)
//    {

//    }

//    private bool AddFaceYZ(int dx)
//    {

//    }

//    private bool AddEdgeX(int dy, int dz)
//    {

//    }

//    private bool AddEdgeY(int dx, int dz)
//    {

//    }

//    private bool AddEdgeZ(int dx, int dy)
//    {

//    }

//    private bool AddVertex(int dx, int dy, int dz)
//    {
//        AddPointToPath(_volume.VoxelToWorld(_prevVoxel) + 0.5f * _volume.CellSize * new Vector3(dx, dy, dz));
//        return true;
//    }

//    private void AddPointToPath(Vector3 p)
//    {
//        if (_pendingEdgeCenters.Count > 0)
//        {
//            var from = _path.Last();
//            var lengths = PendingEdgeDeltas(from, p).Select(d => _pendingEdgeDir.dx != 0 ? d.Y * d.Y + d.Z * d.Z : _pendingEdgeDir.dy != 0 ? d.X * d.X + d.Z * d.Z : d.X * d.X + d.Y * d.Y).ToList();
//            var invTotalLen = 1 / lengths.Sum();

//            var delta = p - from;
//            float t = 0;
//            for (int i = 0; i < _pendingEdgeCenters.Count; ++i)
//            {
//                t += lengths[i] * invTotalLen;
//                var inter = _pendingEdgeCenters[i];
//                if (_pendingEdgeDir.dx != 0)
//                    inter.X = from.X + t * delta.X;
//                else if (_pendingEdgeDir.dy != 0)
//                    inter.Y = from.Y + t * delta.Y;
//                else
//                    inter.Z = from.Z + t * delta.Z;
//                _path.Add(inter);
//            }
//            _pendingEdgeCenters.Clear();
//            _pendingEdgeDir = default;
//        }
//        _prevPlaneNormal = default;
//        _path.Add(p);
//    }

//    private IEnumerable<Vector3> PendingEdgeDeltas(Vector3 from, Vector3 to)
//    {
//        foreach (var p in _pendingEdgeCenters)
//        {
//            yield return p - from;
//            from = p;
//        }
//        yield return to - from;
//    }
//}
