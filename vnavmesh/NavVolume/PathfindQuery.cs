using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.Linq;
using System.Numerics;
using static Lumina.Data.Parsing.Uld.NodeData;

namespace Navmesh.NavVolume;

public class PathfindQuery
{
    public struct Node
    {
        public float GScore;
        public float HScore;
        public int ParentIndex; // raw voxel map index
        public int OpenHeapIndex; // -1 if in closed list, 0 if not in any lists, otherwise (index+1)
    }

    private VoxelMap _volume;
    private Node[] _nodes;
    private List<int> _openList = new();
    private (int x, int y, int z) _goal;

    public PathfindQuery(VoxelMap volume)
    {
        _volume = volume;
        _nodes = new Node[volume.Voxels.Length];
    }

    public List<Vector3> FindPath(Vector3 from, Vector3 to)
    {
        Start(from, to);
        return BuildPathToVisitedNode(Execute());
    }

    public void Start(Vector3 from, Vector3 to)
    {
        Array.Fill(_nodes, new());
        _openList.Clear();
        _goal = _volume.Clamp(_volume.WorldToVoxel(to));

        var start = _volume.Clamp(_volume.WorldToVoxel(from));
        var startIndex = _volume.VoxelToIndex(start);
        _nodes[startIndex].HScore = HeuristicDistance(start);
        _nodes[startIndex].ParentIndex = startIndex; // start's parent is self
        AddToOpen(startIndex);
        //Service.Log.Debug($"volume pathfind: {start} ({startIndex:X}) to {_goal}");
    }

    // returns whether search is to be terminated; on success, first node of the open list would contain found goal
    public bool ExecuteStep()
    {
        if (_openList.Count == 0 || _nodes[_openList[0]].HScore <= 0)
            return false;

        int nextNodeIndex = PopMinOpen();
        var nextNode = _volume.IndexToVoxel(nextNodeIndex);
        //Service.Log.Debug($"volume pathfind: considering {nextNode} ({nextNodeIndex:X}), g={_nodes[nextNodeIndex].GScore:f3}, h={_nodes[nextNodeIndex].HScore:f3}");
        if (nextNode.y > 0)
            VisitNeighbour(nextNodeIndex, nextNodeIndex - 1, (nextNode.x, nextNode.y - 1, nextNode.z), _volume.CellSize.Y);
        if (nextNode.y < _volume.NumCellsY - 1)
            VisitNeighbour(nextNodeIndex, nextNodeIndex + 1, (nextNode.x, nextNode.y + 1, nextNode.z), _volume.CellSize.Y);
        if (nextNode.x > 0)
            VisitNeighbour(nextNodeIndex, nextNodeIndex - _volume.NumCellsY, (nextNode.x - 1, nextNode.y, nextNode.z), _volume.CellSize.X);
        if (nextNode.x < _volume.NumCellsX - 1)
            VisitNeighbour(nextNodeIndex, nextNodeIndex + _volume.NumCellsY, (nextNode.x + 1, nextNode.y, nextNode.z), _volume.CellSize.X);
        if (nextNode.z > 0)
            VisitNeighbour(nextNodeIndex, nextNodeIndex - _volume.NumCellsY * _volume.NumCellsX, (nextNode.x, nextNode.y, nextNode.z - 1), _volume.CellSize.Z);
        if (nextNode.z < _volume.NumCellsZ - 1)
            VisitNeighbour(nextNodeIndex, nextNodeIndex + _volume.NumCellsY * _volume.NumCellsX, (nextNode.x, nextNode.y, nextNode.z + 1), _volume.CellSize.Z);
        return true;
    }

    public int CurrentResult() => _openList.Count > 0 && _nodes[_openList[0]].HScore <= 0 ? _openList[0] : -1;

    public int Execute()
    {
        while (ExecuteStep())
            ;
        return CurrentResult();
    }

    private List<Vector3> BuildPathToVisitedNode(int nodeIndex)
    {
        var res = new List<Vector3>();
        if (nodeIndex >= 0)
        {
            while (_nodes[nodeIndex].ParentIndex != nodeIndex)
            {
                res.Add(_volume.VoxelToWorld(_volume.IndexToVoxel(nodeIndex)));
                nodeIndex = _nodes[nodeIndex].ParentIndex;
            }
            res.Reverse();
        }
        return res;
    }

    private void VisitNeighbour(int parentIndex, int nodeIndex, (int x, int y, int z) nodeCoord, float deltaG)
    {
        if (_volume.Voxels[nodeIndex])
            return; // this voxel is occupied

        if (_nodes[nodeIndex].OpenHeapIndex < 0)
            return; // in closed list already - TODO: is it possible to visit again with lower cost?..

        if (_nodes[nodeIndex].OpenHeapIndex == 0)
        {
            // first time we're visiting this node, calculate heuristic
            _nodes[nodeIndex].GScore = float.MaxValue;
            _nodes[nodeIndex].HScore = HeuristicDistance(nodeCoord);
        }

        var nodeG = _nodes[parentIndex].GScore + deltaG;

        // check LoS from grandparent
        int grandParentIndex = _nodes[parentIndex].ParentIndex;
        // if difference between parent's G and grandparent's G is large enough, skip raycast check?
        var grandParentCoord = _volume.IndexToVoxel(grandParentIndex);
        if (LineOfSight(grandParentCoord, nodeCoord))
        {
            parentIndex = grandParentIndex;
            nodeG = _nodes[parentIndex].GScore + Distance(grandParentCoord, nodeCoord);
        }

        if (nodeG + 0.00001f < _nodes[nodeIndex].GScore)
        {
            // new path is better
            _nodes[nodeIndex].GScore = nodeG;
            _nodes[nodeIndex].ParentIndex = parentIndex;
            AddToOpen(nodeIndex);
            //Service.Log.Debug($"volume pathfind: adding {nodeCoord} ({nodeIndex:X}), parent={parentIndex:X}, g={nodeG:f3}, h={_nodes[nodeIndex].HScore:f3}");
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
    private float HeuristicDistance((int x, int y, int z) v) => Distance(_goal, v) * 0.999f;

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

    private void AddToOpen(int nodeIndex)
    {
        if (_nodes[nodeIndex].OpenHeapIndex <= 0)
        {
            _openList.Add(nodeIndex);
            _nodes[nodeIndex].OpenHeapIndex = _openList.Count;
        }
        // update location
        PercolateUp(_nodes[nodeIndex].OpenHeapIndex - 1);
    }

    // remove first (minimal) node from open heap and mark as closed
    private int PopMinOpen()
    {
        int nodeIndex = _openList[0];
        _openList[0] = _openList[_openList.Count - 1];
        _nodes[nodeIndex].OpenHeapIndex = -1;
        _openList.RemoveAt(_openList.Count - 1);
        if (_openList.Count > 0)
        {
            _nodes[_openList[0]].OpenHeapIndex = 1;
            PercolateDown(0);
        }
        return nodeIndex;
    }

    private void PercolateUp(int heapIndex)
    {
        int nodeIndex = _openList[heapIndex];
        int parent = (heapIndex - 1) >> 1;
        while (heapIndex > 0 && HeapLess(nodeIndex, _openList[parent]))
        {
            _openList[heapIndex] = _openList[parent];
            _nodes[_openList[heapIndex]].OpenHeapIndex = heapIndex + 1;
            heapIndex = parent;
            parent = (heapIndex - 1) >> 1;
        }
        _openList[heapIndex] = nodeIndex;
        _nodes[nodeIndex].OpenHeapIndex = heapIndex + 1;
    }

    private void PercolateDown(int heapIndex)
    {
        int nodeIndex = _openList[heapIndex];
        int maxSize = _openList.Count;
        while (true)
        {
            int child1 = (heapIndex << 1) + 1;
            if (child1 >= maxSize)
                break;
            int child2 = child1 + 1;
            if (child2 == maxSize || HeapLess(_openList[child1], _openList[child2]))
            {
                if (HeapLess(_openList[child1], nodeIndex))
                {
                    _openList[heapIndex] = _openList[child1];
                    _nodes[_openList[heapIndex]].OpenHeapIndex = heapIndex + 1;
                    heapIndex = child1;
                }
                else
                {
                    break;
                }
            }
            else if (HeapLess(_openList[child2], nodeIndex))
            {
                _openList[heapIndex] = _openList[child2];
                _nodes[_openList[heapIndex]].OpenHeapIndex = heapIndex + 1;
                heapIndex = child2;
            }
            else
            {
                break;
            }
        }
        _openList[heapIndex] = nodeIndex;
        _nodes[nodeIndex].OpenHeapIndex = heapIndex + 1;
    }

    private bool HeapLess(int nodeIndexLeft, int nodeIndexRight)
    {
        ref var nodeL = ref _nodes[nodeIndexLeft];
        ref var nodeR = ref _nodes[nodeIndexRight];
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
