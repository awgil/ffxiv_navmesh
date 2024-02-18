using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh.NavVolume;

public class PathfindQuery
{
    public struct Node
    {
        public float GScore;
        public float HScore;
        public int Voxel; // voxel map index corresponding to this node
        public int ParentIndex; // index in the node list of the node we entered from
        public int OpenHeapIndex; // -1 if in closed list, otherwise index in open list
        // TODO: add node-enter-pos
    }

    private VoxelMap _volume;
    private List<Node> _nodes = new(); // grow only (TODO: consider chunked vector)
    private Dictionary<int, int> _nodeLookup = new(); // voxel -> node index
    private List<int> _openList = new(); // heap containing node indices
    private int _goalVoxel;
    private Vector3 _goalPos;
    private bool _useRaycast;

    public VoxelMap Volume => _volume;

    public PathfindQuery(VoxelMap volume)
    {
        _volume = volume;
    }

    public int FindNearestEmptyVoxel(Vector3 center, float radius)
    {
        var (cx, cy, cz) = _volume.WorldToVoxel(center);
        if (_volume.InBounds(cx, cy, cz) && !_volume[cx, cy, cz])
            return _volume.VoxelToIndex(cx, cy, cz); // fast path: the cell is empty already

        var maxDistSq = radius * radius;

        // search plane at z = cz
        var res = cz >= 0 && cz < _volume.NumCellsZ ? FindNearestEmptyVoxelXY(center, ref maxDistSq, 0, cx, cy, cz) : -1;
        //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at plane {cz} -> {res}");

        // search nearby planes
        var distFromNeg = center.Z - (_volume.BoundsMin.Z + cz * _volume.CellSize.Z);
        var distFromPos = _volume.CellSize.Z - distFromNeg;
        for (int dz = 1; ; ++dz)
        {
            bool outOfBounds = true;

            // search plane at -dz
            var d = distFromNeg * distFromNeg;
            var z = cz - dz;
            if (d <= maxDistSq && z >= 0)
            {
                var alt = z < _volume.NumCellsZ ? FindNearestEmptyVoxelXY(center, ref maxDistSq, d, cx, cy, z) : -1;
                if (alt >= 0)
                {
                    res = alt;
                    //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at plane {z} -> {res}");
                }
                outOfBounds = false;
                distFromNeg += _volume.CellSize.Z;
            }

            // search plane at +dz
            d = distFromPos * distFromPos;
            z = cz + dz;
            if (d <= maxDistSq && z < _volume.NumCellsZ)
            {
                var alt = z >= 0 ? FindNearestEmptyVoxelXY(center, ref maxDistSq, d, cx, cy, z) : -1;
                if (alt >= 0)
                {
                    res = alt;
                    //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at plane {z} -> {res}");
                }
                outOfBounds = false;
                distFromPos += _volume.CellSize.Z;
            }

            if (outOfBounds)
                break;
        }
        return res;
    }

    public List<int> FindPath(int fromVoxel, int toVoxel, Vector3 fromPos, Vector3 toPos, bool useRaycast)
    {
        _useRaycast = useRaycast;
        Start(fromVoxel, toVoxel, fromPos, toPos);
        Execute();
        return BuildPathToVisitedNode(CurrentResultNode());
    }

    public void Start(int fromVoxel, int toVoxel, Vector3 fromPos, Vector3 toPos)
    {
        _nodes.Clear();
        _nodeLookup.Clear();
        _openList.Clear();
        if (fromVoxel < 0 || toVoxel < 0)
        {
            Service.Log.Error($"Bad input cells: {fromVoxel} -> {toVoxel}");
            return;
        }

        _goalVoxel = toVoxel;
        _goalPos = toPos;

        _nodes.Add(new() { HScore = HeuristicDistance(fromVoxel, _volume.IndexToVoxel(fromVoxel)), Voxel = fromVoxel, ParentIndex = 0, OpenHeapIndex = -1 }); // start's parent is self
        _nodeLookup[fromVoxel] = 0;
        AddToOpen(0);
        //Service.Log.Debug($"volume pathfind: {fromPos} ({fromVoxel:X}) to {toPos} ({toVoxel:X})");
    }

    // returns whether search is to be terminated; on success, first node of the open list would contain found goal
    public bool ExecuteStep()
    {
        var nodeSpan = NodeSpan;
        if (_openList.Count == 0 || nodeSpan[_openList[0]].HScore <= 0)
            return false;

        var nextNodeIndex = PopMinOpen();
        ref var nextNode = ref nodeSpan[nextNodeIndex];

        var nextNodeCoord = _volume.IndexToVoxel(nextNode.Voxel);
        //Service.Log.Debug($"volume pathfind: considering {nextNodeCoord} ({nextNodeIndex:X}), g={_nodes[nextNodeIndex].GScore:f3}, h={_nodes[nextNodeIndex].HScore:f3}");
        if (nextNodeCoord.y > 0)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel - 1, (nextNodeCoord.x, nextNodeCoord.y - 1, nextNodeCoord.z), _volume.CellSize.Y);
        if (nextNodeCoord.y < _volume.NumCellsY - 1)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel + 1, (nextNodeCoord.x, nextNodeCoord.y + 1, nextNodeCoord.z), _volume.CellSize.Y);
        if (nextNodeCoord.x > 0)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel - _volume.NumCellsY, (nextNodeCoord.x - 1, nextNodeCoord.y, nextNodeCoord.z), _volume.CellSize.X);
        if (nextNodeCoord.x < _volume.NumCellsX - 1)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel + _volume.NumCellsY, (nextNodeCoord.x + 1, nextNodeCoord.y, nextNodeCoord.z), _volume.CellSize.X);
        if (nextNodeCoord.z > 0)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel - _volume.NumCellsY * _volume.NumCellsX, (nextNodeCoord.x, nextNodeCoord.y, nextNodeCoord.z - 1), _volume.CellSize.Z);
        if (nextNodeCoord.z < _volume.NumCellsZ - 1)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel + _volume.NumCellsY * _volume.NumCellsX, (nextNodeCoord.x, nextNodeCoord.y, nextNodeCoord.z + 1), _volume.CellSize.Z);
        return true;
    }

    public void Execute(int maxSteps = int.MaxValue)
    {
        while (maxSteps-- > 0 && ExecuteStep())
            ;
    }

    // utilities for nearest voxel search
    private int FindNearestEmptyVoxelXY(Vector3 center, ref float maxDistSq, float planeDistSq, int cx, int cy, int cz)
    {
        // search column at (cx,cz)
        var res = cx >= 0 && cx < _volume.NumCellsX ? FindNearestEmptyVoxelY(center, ref maxDistSq, planeDistSq, cx, cy, cz) : -1;
        //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at column {cx}x{cz} -> {res}");

        // search nearby columns
        var distFromNeg = center.X - (_volume.BoundsMin.X + cx * _volume.CellSize.X);
        var distFromPos = _volume.CellSize.X - distFromNeg;
        for (int dx = 1; ; ++dx)
        {
            bool outOfBounds = true;

            // search column at -dx
            var d = distFromNeg * distFromNeg + planeDistSq;
            var x = cx - dx;
            if (d <= maxDistSq && x >= 0)
            {
                var alt = x < _volume.NumCellsX ? FindNearestEmptyVoxelY(center, ref maxDistSq, d, x, cy, cz) : -1;
                if (alt >= 0)
                {
                    res = alt;
                    //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at column {x}x{cz} -> {res}");
                }
                outOfBounds = false;
                distFromNeg += _volume.CellSize.X;
            }

            // search column at +dx
            d = distFromPos * distFromPos + planeDistSq;
            x = cx + dx;
            if (d <= maxDistSq && x < _volume.NumCellsX)
            {
                var alt = x >= 0 ? FindNearestEmptyVoxelY(center, ref maxDistSq, d, x, cy, cz) : -1;
                if (alt >= 0)
                {
                    res = alt;
                    //Service.Log.Debug($"Search: {cx}x{cy}x{cz} at column {x}x{cz} -> {res}");
                }
                outOfBounds = false;
                distFromPos += _volume.CellSize.X;
            }

            if (outOfBounds)
                break;
        }
        return res;
    }

    private int FindNearestEmptyVoxelY(Vector3 center, ref float maxDistSq, float columnDistSq, int cx, int cy, int cz)
    {
        // check voxel at cy
        if (cy >= 0 && cy < _volume.NumCellsY && !_volume[cx, cy, cz])
        {
            maxDistSq = columnDistSq;
            //Service.Log.Debug($"Found: {cx}x{cy}x{cz} -> {maxDistSq}");
            return _volume.VoxelToIndex(cx, cy, cz);
        }

        // search up and down
        var distFromNeg = center.Y - (_volume.BoundsMin.Y + cy * _volume.CellSize.Y);
        var distFromPos = _volume.CellSize.Y - distFromNeg;
        distFromNeg += 5 * _volume.CellSize.Y; // TODO: this is an arbitrary penalty for 'underground unblocked' voxels; improve this...
        int res = -1;
        for (int dy = 1; ; ++dy)
        {
            bool outOfBounds = true;

            // search voxel at -dy
            var d = distFromNeg * distFromNeg + columnDistSq;
            var y = cy - dy;
            if (d <= maxDistSq && y >= 0)
            {
                if (y < _volume.NumCellsY && !_volume[cx, y, cz])
                {
                    maxDistSq = d;
                    //Service.Log.Debug($"Found: {cx}x{y}x{cz} -> {maxDistSq}");
                    res = _volume.VoxelToIndex(cx, y, cz);
                }
                outOfBounds = false;
                distFromNeg += _volume.CellSize.Y;
            }

            // search voxel at +dy
            d = distFromPos * distFromPos + columnDistSq;
            y = cy + dy;
            if (d <= maxDistSq && y < _volume.NumCellsY)
            {
                if (y >= 0 && !_volume[cx, y, cz])
                {
                    maxDistSq = d;
                    //Service.Log.Debug($"Found: {cx}x{y}x{cz} -> {maxDistSq}");
                    res = _volume.VoxelToIndex(cx, y, cz);
                }
                outOfBounds = false;
                distFromPos += _volume.CellSize.Y;
            }

            if (outOfBounds)
                break;
        }
        return res;
    }

    private int CurrentResultNode() => _openList.Count > 0 && _nodes[_openList[0]].HScore <= 0 ? _openList[0] : -1;

    private List<int> BuildPathToVisitedNode(int nodeIndex)
    {
        var res = new List<int>();
        if (nodeIndex >= 0)
        {
            var nodeSpan = NodeSpan;
            res.Add(nodeSpan[nodeIndex].Voxel);
            var from = _volume.IndexToVoxel(nodeSpan[nodeIndex].Voxel);
            //Service.Log.Debug($"volume pathfind: backpath from {from} ({nodeIndex})");
            while (nodeSpan[nodeIndex].ParentIndex != nodeIndex)
            {
                var nextIndex = nodeSpan[nodeIndex].ParentIndex;
                var to = _volume.IndexToVoxel(nodeSpan[nextIndex].Voxel);
                //Service.Log.Debug($"volume pathfind: backpath next {to} ({nextIndex})");
                foreach (var v in EnumeratePixelsInLine(from.x, from.y, from.z, to.x, to.y, to.z))
                {
                    //Service.Log.Debug($"volume pathfind: intermediate {v}");
                    res.Add(_volume.VoxelToIndex(v));
                }
                nodeIndex = nextIndex;
                from = to;
            }
            res.Reverse();
        }
        return res;
    }

    private void VisitNeighbour(int parentIndex, int nodeVoxel, (int x, int y, int z) nodeCoord, float deltaG)
    {
        if (_volume.Voxels[nodeVoxel])
            return; // this voxel is occupied

        var nodeIndex = _nodeLookup.GetValueOrDefault(nodeVoxel, -1);
        if (nodeIndex < 0)
        {
            // first time we're visiting this node, calculate heuristic
            nodeIndex = _nodes.Count;
            _nodes.Add(new() { GScore = float.MaxValue, HScore = HeuristicDistance(nodeVoxel, nodeCoord), Voxel = nodeVoxel, ParentIndex = parentIndex, OpenHeapIndex = -1 });
            _nodeLookup[nodeVoxel] = nodeIndex;
        }
        else if (_nodes[nodeIndex].OpenHeapIndex < 0)
        {
            // in closed list already - TODO: is it possible to visit again with lower cost?..
            return;
        }

        var nodeSpan = NodeSpan;
        ref var parentNode = ref nodeSpan[parentIndex];
        var nodeG = parentNode.GScore + deltaG;

        if (_useRaycast)
        {
            // check LoS from grandparent
            int grandParentIndex = parentNode.ParentIndex;
            // if difference between parent's G and grandparent's G is large enough, skip raycast check?
            ref var grandParentNode = ref nodeSpan[grandParentIndex];
            var grandParentCoord = _volume.IndexToVoxel(grandParentNode.Voxel);
            if (LineOfSight(nodeCoord, grandParentCoord))
            {
                parentIndex = grandParentIndex;
                nodeG = grandParentNode.GScore + Distance(grandParentCoord, nodeCoord);
            }
        }

        ref var curNode = ref nodeSpan[nodeIndex];
        if (nodeG + 0.00001f < curNode.GScore)
        {
            // new path is better
            curNode.GScore = nodeG;
            curNode.ParentIndex = parentIndex;
            AddToOpen(nodeIndex);
            //Service.Log.Debug($"volume pathfind: adding {nodeCoord} ({nodeIndex}), parent={parentIndex}, g={nodeG:f3}, h={_nodes[nodeIndex].HScore:f3}");
        }
    }

    private bool LineOfSight((int x, int y, int z) from, (int x, int y, int z) to)
    {
        foreach (var (x, y, z) in EnumeratePixelsInLine(from.x, from.y, from.z, to.x, to.y, to.z))
            if (_volume[x, y, z])
                return false;
        return true;
    }

    private float Distance((int x, int y, int z) v1, (int x, int y, int z) v2) => (new Vector3(v2.x - v1.x, v2.y - v1.y, v2.z - v1.z) * _volume.CellSize).Length();
    private float HeuristicDistance(int nodeVoxel, (int x, int y, int z) v) => nodeVoxel != _goalVoxel ? (_volume.VoxelToWorld(v) - _goalPos).Length() * 0.999f : 0; // TODO: use cell enter pos instead of center...

    // enumerate pixels along line starting from (x1, y1, z1) to (x2, y2, z2); first is not returned, last is returned
    public static IEnumerable<(int x, int y, int z)> EnumeratePixelsInLine(int x1, int y1, int z1, int x2, int y2, int z2)
    {
        int dx = x2 - x1;
        int dy = y2 - y1;
        int dz = z2 - z1;
        int sx = dx > 0 ? 1 : -1;
        int sy = dy > 0 ? 1 : -1;
        int sz = dz > 0 ? 1 : -1;
        dx = Math.Abs(dx);
        dy = Math.Abs(dy);
        dz = Math.Abs(dz);
        if (dx >= dy && dx >= dz)
        {
            int erry = 2 * dy - dx;
            int errz = 2 * dz - dx;
            do
            {
                x1 += sx;
                //yield return (x1, y1, z1);
                if (erry > 0)
                {
                    y1 += sy;
                    erry -= 2 * dx;
                    //yield return (x1, y1, z1);
                }
                if (errz > 0)
                {
                    z1 += sz;
                    errz -= 2 * dx;
                    //yield return (x1, y1, z1);
                }
                erry += 2 * dy;
                errz += 2 * dz;
                yield return (x1, y1, z1);
            }
            while (x1 != x2);
        }
        else if (dy >= dx && dy >= dz)
        {
            int errx = 2 * dx - dy;
            int errz = 2 * dz - dy;
            do
            {
                y1 += sy;
                //yield return (x1, y1, z1);
                if (errx > 0)
                {
                    x1 += sx;
                    errx -= 2 * dy;
                    //yield return (x1, y1, z1);
                }
                if (errz > 0)
                {
                    z1 += sz;
                    errz -= 2 * dy;
                    //yield return (x1, y1, z1);
                }
                errx += 2 * dx;
                errz += 2 * dz;
                yield return (x1, y1, z1);
            }
            while (y1 != y2);
        }
        else // dz >= dx && dz >= dy
        {
            int errx = 2 * dx - dz;
            int erry = 2 * dy - dz;
            do
            {
                z1 += sz;
                //yield return (x1, y1, z1);
                if (errx > 0)
                {
                    x1 += sx;
                    errx -= 2 * dz;
                    //yield return (x1, y1, z1);
                }
                if (erry > 0)
                {
                    y1 += sy;
                    erry -= 2 * dz;
                    //yield return (x1, y1, z1);
                }
                errx += 2 * dx;
                erry += 2 * dy;
                yield return (x1, y1, z1);
            }
            while (z1 != z2);
        }
    }

    private Span<Node> NodeSpan => CollectionsMarshal.AsSpan(_nodes);

    private void AddToOpen(int nodeIndex)
    {
        ref var node = ref NodeSpan[nodeIndex];
        if (node.OpenHeapIndex < 0)
        {
            node.OpenHeapIndex = _openList.Count;
            _openList.Add(nodeIndex);
        }
        // update location
        PercolateUp(node.OpenHeapIndex);
    }

    // remove first (minimal) node from open heap and mark as closed
    private int PopMinOpen()
    {
        var nodeSpan = NodeSpan;
        int nodeIndex = _openList[0];
        _openList[0] = _openList[_openList.Count - 1];
        _openList.RemoveAt(_openList.Count - 1);
        nodeSpan[nodeIndex].OpenHeapIndex = -1;
        if (_openList.Count > 0)
        {
            nodeSpan[_openList[0]].OpenHeapIndex = 0;
            PercolateDown(0);
        }
        return nodeIndex;
    }

    private void PercolateUp(int heapIndex)
    {
        var nodeSpan = NodeSpan;
        int nodeIndex = _openList[heapIndex];
        int parent = (heapIndex - 1) >> 1;
        while (heapIndex > 0 && HeapLess(ref nodeSpan[nodeIndex], ref nodeSpan[_openList[parent]]))
        {
            _openList[heapIndex] = _openList[parent];
            nodeSpan[_openList[heapIndex]].OpenHeapIndex = heapIndex;
            heapIndex = parent;
            parent = (heapIndex - 1) >> 1;
        }
        _openList[heapIndex] = nodeIndex;
        nodeSpan[nodeIndex].OpenHeapIndex = heapIndex;
    }

    private void PercolateDown(int heapIndex)
    {
        var nodeSpan = NodeSpan;
        int nodeIndex = _openList[heapIndex];
        int maxSize = _openList.Count;
        while (true)
        {
            int child1 = (heapIndex << 1) + 1;
            if (child1 >= maxSize)
                break;
            int child2 = child1 + 1;
            if (child2 == maxSize || HeapLess(ref nodeSpan[_openList[child1]], ref nodeSpan[_openList[child2]]))
            {
                if (HeapLess(ref nodeSpan[_openList[child1]], ref nodeSpan[nodeIndex]))
                {
                    _openList[heapIndex] = _openList[child1];
                    nodeSpan[_openList[heapIndex]].OpenHeapIndex = heapIndex;
                    heapIndex = child1;
                }
                else
                {
                    break;
                }
            }
            else if (HeapLess(ref nodeSpan[_openList[child2]], ref nodeSpan[nodeIndex]))
            {
                _openList[heapIndex] = _openList[child2];
                nodeSpan[_openList[heapIndex]].OpenHeapIndex = heapIndex;
                heapIndex = child2;
            }
            else
            {
                break;
            }
        }
        _openList[heapIndex] = nodeIndex;
        nodeSpan[nodeIndex].OpenHeapIndex = heapIndex;
    }

    private bool HeapLess(ref Node nodeL, ref Node nodeR)
    {
        var fl = nodeL.GScore + nodeL.HScore;
        var fr = nodeR.GScore + nodeR.HScore;
        if (fl + 0.00001f < fr)
            return true;
        else if (fr + 0.00001f < fl)
            return false;
        else
            return nodeL.GScore > nodeR.GScore; // tie-break towards larger g-values
    }
}
