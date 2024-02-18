using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh.NavVolume;

public class VoxelPathfind
{
    public struct Node
    {
        public float GScore;
        public float HScore;
        public int Voxel; // voxel map index corresponding to this node
        public int ParentIndex; // index in the node list of the node we entered from
        public int OpenHeapIndex; // -1 if in closed list, otherwise index in open list
        public Vector3 Position;
    }

    private VoxelMap _volume;
    private List<Node> _nodes = new(); // grow only (TODO: consider chunked vector)
    private Dictionary<int, int> _nodeLookup = new(); // voxel -> node index
    private List<int> _openList = new(); // heap containing node indices
    private int _bestNodeIndex;
    private int _goalVoxel;
    private Vector3 _goalPos;
    private bool _useRaycast;
    private bool _useEnterPos = true;
    private bool _allowReopen = false;

    public VoxelMap Volume => _volume;
    public Span<Node> NodeSpan => CollectionsMarshal.AsSpan(_nodes);

    public VoxelPathfind(VoxelMap volume)
    {
        _volume = volume;
    }

    public List<(int voxel, Vector3 p)> FindPath(int fromVoxel, int toVoxel, Vector3 fromPos, Vector3 toPos, bool useRaycast, bool returnIntermediatePoints)
    {
        _useRaycast = useRaycast;
        Start(fromVoxel, toVoxel, fromPos, toPos);
        Execute();
        return BuildPathToVisitedNode(_bestNodeIndex, returnIntermediatePoints);
    }

    public void Start(int fromVoxel, int toVoxel, Vector3 fromPos, Vector3 toPos)
    {
        _nodes.Clear();
        _nodeLookup.Clear();
        _openList.Clear();
        _bestNodeIndex = 0;
        if (fromVoxel < 0 || toVoxel < 0)
        {
            Service.Log.Error($"Bad input cells: {fromVoxel} -> {toVoxel}");
            return;
        }

        _goalVoxel = toVoxel;
        _goalPos = toPos;

        var fromCoord = _volume.IndexToVoxel(fromVoxel);
        var startPos = _useEnterPos ? fromPos : _volume.VoxelToWorld(fromCoord);
        _nodes.Add(new() { HScore = HeuristicDistance(fromVoxel, startPos), Voxel = fromVoxel, ParentIndex = 0, OpenHeapIndex = -1, Position = startPos }); // start's parent is self
        _nodeLookup[fromVoxel] = 0;
        AddToOpen(0);
        //Service.Log.Debug($"volume pathfind: {fromPos} ({fromVoxel:X}) to {toPos} ({toVoxel:X})");
    }

    // returns whether search is to be terminated; on success, first node of the open list would contain found goal
    public bool ExecuteStep()
    {
        var nodeSpan = NodeSpan;
        if (_openList.Count == 0 || nodeSpan[_bestNodeIndex].HScore <= 0)
            return false;

        var nextNodeIndex = PopMinOpen();
        ref var nextNode = ref nodeSpan[nextNodeIndex];

        var nextNodeCoord = _volume.IndexToVoxel(nextNode.Voxel);
        var nextNodeCenter = _volume.VoxelToWorld(nextNodeCoord);
        var offset = (_useEnterPos ? 0.5f : 1.0f) * _volume.CellSize; // face midpoints vs voxel center
        //Service.Log.Debug($"volume pathfind: considering {nextNodeCoord} ({nextNodeIndex:X}), g={_nodes[nextNodeIndex].GScore:f3}, h={_nodes[nextNodeIndex].HScore:f3}");
        if (nextNodeCoord.y > 0)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel - 1, (nextNodeCoord.x, nextNodeCoord.y - 1, nextNodeCoord.z), new(nextNodeCenter.X, nextNodeCenter.Y - offset.Y, nextNodeCenter.Z));
        if (nextNodeCoord.y < _volume.NumCellsY - 1)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel + 1, (nextNodeCoord.x, nextNodeCoord.y + 1, nextNodeCoord.z), new(nextNodeCenter.X, nextNodeCenter.Y + offset.Y, nextNodeCenter.Z));
        if (nextNodeCoord.x > 0)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel - _volume.NumCellsY, (nextNodeCoord.x - 1, nextNodeCoord.y, nextNodeCoord.z), new(nextNodeCenter.X - offset.X, nextNodeCenter.Y, nextNodeCenter.Z));
        if (nextNodeCoord.x < _volume.NumCellsX - 1)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel + _volume.NumCellsY, (nextNodeCoord.x + 1, nextNodeCoord.y, nextNodeCoord.z), new(nextNodeCenter.X + offset.X, nextNodeCenter.Y, nextNodeCenter.Z));
        if (nextNodeCoord.z > 0)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel - _volume.NumCellsY * _volume.NumCellsX, (nextNodeCoord.x, nextNodeCoord.y, nextNodeCoord.z - 1), new(nextNodeCenter.X, nextNodeCenter.Y, nextNodeCenter.Z - offset.Z));
        if (nextNodeCoord.z < _volume.NumCellsZ - 1)
            VisitNeighbour(nextNodeIndex, nextNode.Voxel + _volume.NumCellsY * _volume.NumCellsX, (nextNodeCoord.x, nextNodeCoord.y, nextNodeCoord.z + 1), new(nextNodeCenter.X, nextNodeCenter.Y, nextNodeCenter.Z - offset.Z));
        return true;
    }

    public void Execute(int maxSteps = int.MaxValue)
    {
        while (maxSteps-- > 0 && ExecuteStep())
            ;
    }

    //private int CurrentResultNode() => _openList.Count > 0 && _nodes[_openList[0]].HScore <= 0 ? _openList[0] : -1;

    private List<(int voxel, Vector3 p)> BuildPathToVisitedNode(int nodeIndex, bool returnIntermediatePoints)
    {
        var res = new List<(int voxel, Vector3 p)>();
        if (nodeIndex < _nodes.Count)
        {
            var nodeSpan = NodeSpan;
            ref var lastNode = ref nodeSpan[nodeIndex];
            res.Add((lastNode.Voxel, lastNode.Position));
            var fromCoord = _volume.IndexToVoxel(lastNode.Voxel);
            //Service.Log.Debug($"volume pathfind: backpath from {fromCoord} ({nodeIndex})");
            while (nodeSpan[nodeIndex].ParentIndex != nodeIndex)
            {
                ref var prevNode = ref nodeSpan[nodeIndex];
                var nextIndex = prevNode.ParentIndex;
                ref var nextNode = ref nodeSpan[nextIndex];
                var toCoord = _volume.IndexToVoxel(nextNode.Voxel);
                //Service.Log.Debug($"volume pathfind: backpath next {toCoord} ({nextIndex})");
                if (returnIntermediatePoints)
                {
                    var delta = nextNode.Position - prevNode.Position;
                    foreach (var v in EnumerateVoxelsInLine(fromCoord, toCoord, prevNode.Position, nextNode.Position))
                    {
                        //Service.Log.Debug($"volume pathfind: intermediate {v}");
                        res.Add((_volume.VoxelToIndex(v.x, v.y, v.z), prevNode.Position + v.t * delta));
                    }
                }
                else
                {
                    res.Add((nextNode.Voxel, nextNode.Position));
                }
                nodeIndex = nextIndex;
                fromCoord = toCoord;
            }
            res.Reverse();
        }
        return res;
    }

    private void VisitNeighbour(int parentIndex, int nodeVoxel, (int x, int y, int z) nodeCoord, Vector3 enterPos)
    {
        if (_volume.Voxels[nodeVoxel])
            return; // this voxel is occupied

        var nodeIndex = _nodeLookup.GetValueOrDefault(nodeVoxel, -1);
        if (nodeIndex < 0)
        {
            // first time we're visiting this node, calculate heuristic
            nodeIndex = _nodes.Count;
            _nodes.Add(new() { GScore = float.MaxValue, HScore = float.MaxValue, Voxel = nodeVoxel, ParentIndex = parentIndex, OpenHeapIndex = -1, Position = enterPos });
            _nodeLookup[nodeVoxel] = nodeIndex;
        }
        else if (!_allowReopen && _nodes[nodeIndex].OpenHeapIndex < 0)
        {
            // in closed list already - TODO: is it possible to visit again with lower cost?..
            return;
        }

        var nodeSpan = NodeSpan;
        ref var parentNode = ref nodeSpan[parentIndex];

        var nodeG = CalculateGScore(ref parentNode, nodeCoord, enterPos, ref parentIndex);
        if (nodeVoxel == _goalVoxel)
            nodeG += (enterPos - _goalPos).Length();

        ref var curNode = ref nodeSpan[nodeIndex];
        if (nodeG + 0.00001f < curNode.GScore)
        {
            // new path is better
            curNode.GScore = nodeG;
            curNode.HScore = HeuristicDistance(nodeVoxel, enterPos);
            curNode.ParentIndex = parentIndex;
            curNode.Position = enterPos;
            AddToOpen(nodeIndex);
            //Service.Log.Debug($"volume pathfind: adding {nodeCoord} ({nodeIndex}), parent={parentIndex}, g={nodeG:f3}, h={_nodes[nodeIndex].HScore:f3}");

            if (curNode.HScore < _nodes[_bestNodeIndex].HScore)
                _bestNodeIndex = nodeIndex;
        }
    }

    private float CalculateGScore(ref Node parent, (int x, int y, int z) destCoord, Vector3 destPos, ref int parentIndex)
    {
        if (_useRaycast)
        {
            // check LoS from grandparent
            int grandParentIndex = parent.ParentIndex;
            // if difference between parent's G and grandparent's G is large enough, skip raycast check?
            ref var grandParentNode = ref NodeSpan[grandParentIndex];
            // TODO: invert LoS check to match path reconstruction step?
            if (LineOfSight(_volume.IndexToVoxel(grandParentNode.Voxel), destCoord, grandParentNode.Position, destPos))
            {
                parentIndex = grandParentIndex;
                return grandParentNode.GScore + (grandParentNode.Position - destPos).Length();
            }
        }
        return parent.GScore + (parent.Position - destPos).Length();
    }

    private bool LineOfSight((int x, int y, int z) fromVoxel, (int x, int y, int z) toVoxel, Vector3 fromPos, Vector3 toPos)
    {
        foreach (var (x, y, z, _) in EnumerateVoxelsInLine(fromVoxel, toVoxel, fromPos, toPos))
            if (_volume[x, y, z])
                return false;
        return true;
    }

    private float HeuristicDistance(int nodeVoxel, Vector3 v) => nodeVoxel != _goalVoxel ? (v - _goalPos).Length() * 0.999f : 0; // TODO: use cell enter pos instead of center...

    // enumerate entered voxels along line; starting voxel is not returned, ending voxel is
    public IEnumerable<(int x, int y, int z, float t)> EnumerateVoxelsInLine((int x, int y, int z) fromVoxel, (int x, int y, int z) toVoxel, Vector3 fromPos, Vector3 toPos)
    {
        var v = fromVoxel;
        var delta = toPos - fromPos;
        var center = _volume.VoxelToWorld(fromVoxel);
        var distX = toVoxel.x - fromVoxel.x;
        var distY = toVoxel.y - fromVoxel.y;
        var distZ = toVoxel.z - fromVoxel.z;
        int moveX = distX > 0 ? 1 : -1;
        int moveY = distY > 0 ? 1 : -1;
        int moveZ = distZ > 0 ? 1 : -1;
        var signedSize = _volume.CellSize * new Vector3(moveX, moveY, moveZ);
        var signedHalfSize = signedSize * 0.5f;
        var borderX = Math.Abs(delta.X) < 0.01f;
        var borderY = Math.Abs(delta.Y) < 0.01f;
        var borderZ = Math.Abs(delta.Z) < 0.01f;
        while (distX != 0 || distY != 0 || distZ != 0)
        {
            // find closest intersection among three (out of six) neighbours
            // line-plane intersection: Q = A + AB*t, PQ*n=0 => (PA + tAB)*n = 0 => t = AP*n / AB*n
            var tx = distX == 0 ? float.MaxValue : borderX ? 0 : (center.X + signedHalfSize.X - fromPos.X) / delta.X;
            var ty = distY == 0 ? float.MaxValue : borderY ? 0 : (center.Y + signedHalfSize.Y - fromPos.Y) / delta.Y;
            var tz = distZ == 0 ? float.MaxValue : borderZ ? 0 : (center.Z + signedHalfSize.Z - fromPos.Z) / delta.Z;
            if (tx <= ty && tx <= tz)
            {
                v.x += moveX;
                distX -= moveX;
                center.X += signedSize.X;
                yield return (v.x, v.y, v.z, tx);
            }
            else if (ty <= tx && ty <= tz)
            {
                v.y += moveY;
                distY -= moveY;
                center.Y += signedSize.Y;
                yield return (v.x, v.y, v.z, ty);
            }
            else if (tz <= tx && tz <= ty)
            {
                v.z += moveZ;
                distZ -= moveZ;
                center.Z += signedSize.Z;
                yield return (v.x, v.y, v.z, tz);
            }
            else
            {
                throw new Exception($"Problem enumerating path {fromVoxel} {fromPos} -> {toVoxel} {toPos}: t={tx} {ty} {tz}");
            }
        }
    }

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
