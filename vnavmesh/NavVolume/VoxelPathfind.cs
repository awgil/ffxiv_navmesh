using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Navmesh.NavVolume;

public class VoxelPathfind
{
    public struct Node
    {
        public float GScore;
        public float HScore;
        public ulong Voxel; // voxel map index corresponding to this node
        public int ParentIndex; // index in the node list of the node we entered from
        public int OpenHeapIndex; // -1 if in closed list, otherwise index in open list
        public Vector3 Position;
    }

    private VoxelMap _volume;
    private List<Node> _nodes = new(); // grow only (TODO: consider chunked vector)
    private Dictionary<ulong, int> _nodeLookup = new(); // voxel -> node index
    private List<int> _openList = new(); // heap containing node indices
    private int _bestNodeIndex;
    private ulong _goalVoxel;
    private Vector3 _goalPos;
    private bool _useRaycast;
    private bool _allowReopen = false; // this is extremely expensive and doesn't seem to actually improve the result
    private float _raycastLimitSq = float.MaxValue;

    public VoxelMap Volume => _volume;
    public Span<Node> NodeSpan => CollectionsMarshal.AsSpan(_nodes);

    public VoxelPathfind(VoxelMap volume)
    {
        _volume = volume;
    }

    public List<(ulong voxel, Vector3 p)> FindPath(ulong fromVoxel, ulong toVoxel, Vector3 fromPos, Vector3 toPos, bool useRaycast, bool returnIntermediatePoints, CancellationToken cancel)
    {
        _useRaycast = useRaycast;
        Start(fromVoxel, toVoxel, fromPos, toPos);
        Execute(cancel);
        return BuildPathToVisitedNode(_bestNodeIndex, returnIntermediatePoints);
    }

    public void Start(ulong fromVoxel, ulong toVoxel, Vector3 fromPos, Vector3 toPos)
    {
        _nodes.Clear();
        _nodeLookup.Clear();
        _openList.Clear();
        _bestNodeIndex = 0;
        if (fromVoxel == VoxelMap.InvalidVoxel || toVoxel == VoxelMap.InvalidVoxel)
        {
            Service.Log.Error($"Bad input cells: {fromVoxel:X} -> {toVoxel:X}");
            return;
        }

        _goalVoxel = toVoxel;
        _goalPos = toPos;

        _nodes.Add(new() { HScore = HeuristicDistance(fromVoxel, fromPos), Voxel = fromVoxel, ParentIndex = 0, OpenHeapIndex = -1, Position = fromPos }); // start's parent is self
        _nodeLookup[fromVoxel] = 0;
        AddToOpen(0);
        //Service.Log.Debug($"volume pathfind: {fromPos} ({fromVoxel:X}) to {toPos} ({toVoxel:X})");
    }

    public void Execute(CancellationToken cancel, int maxSteps = 1000000)
    {
        for (int i = 0; i < maxSteps; ++i)
        {
            if (!ExecuteStep())
                return;
            if ((i & 0x3ff) == 0)
                cancel.ThrowIfCancellationRequested();
        }
    }

    // returns whether search is to be terminated; on success, first node of the open list would contain found goal
    public bool ExecuteStep()
    {
        var nodeSpan = NodeSpan;
        if (_openList.Count == 0 || nodeSpan[_bestNodeIndex].HScore <= 0)
            return false;

        var curNodeIndex = PopMinOpen();
        ref var curNode = ref nodeSpan[curNodeIndex];
        //Service.Log.Debug($"volume pathfind: considering {curNode.Voxel:X} (#{curNodeIndex}), g={curNode.GScore:f3}, h={curNode.HScore:f3}");

        var curVoxel = curNode.Voxel;
        foreach (var dest in EnumerateNeighbours(curVoxel, 0, -1, 0))
            VisitNeighbour(curNodeIndex, dest);
        foreach (var dest in EnumerateNeighbours(curVoxel, 0, +1, 0))
            VisitNeighbour(curNodeIndex, dest);
        foreach (var dest in EnumerateNeighbours(curVoxel, -1, 0, 0))
            VisitNeighbour(curNodeIndex, dest);
        foreach (var dest in EnumerateNeighbours(curVoxel, +1, 0, 0))
            VisitNeighbour(curNodeIndex, dest);
        foreach (var dest in EnumerateNeighbours(curVoxel, 0, 0, -1))
            VisitNeighbour(curNodeIndex, dest);
        foreach (var dest in EnumerateNeighbours(curVoxel, 0, 0, +1))
            VisitNeighbour(curNodeIndex, dest);
        return true;
    }

    private List<(ulong voxel, Vector3 p)> BuildPathToVisitedNode(int nodeIndex, bool returnIntermediatePoints)
    {
        var res = new List<(ulong voxel, Vector3 p)>();
        if (nodeIndex < _nodes.Count)
        {
            var nodeSpan = NodeSpan;
            ref var lastNode = ref nodeSpan[nodeIndex];
            res.Add((lastNode.Voxel, lastNode.Position));
            //Service.Log.Debug($"volume pathfind: backpath from {lastNode.Voxel:X} (#{nodeIndex})");
            while (nodeSpan[nodeIndex].ParentIndex != nodeIndex)
            {
                ref var prevNode = ref nodeSpan[nodeIndex];
                var nextIndex = prevNode.ParentIndex;
                ref var nextNode = ref nodeSpan[nextIndex];
                //Service.Log.Debug($"volume pathfind: backpath next {nextNode.Voxel:X} (#{nextIndex})");
                if (returnIntermediatePoints)
                {
                    var delta = nextNode.Position - prevNode.Position;
                    foreach (var v in VoxelSearch.EnumerateVoxelsInLine(_volume, prevNode.Voxel, nextNode.Voxel, prevNode.Position, nextNode.Position))
                    {
                        //Service.Log.Debug($"volume pathfind: intermediate {v}");
                        res.Add((v.voxel, prevNode.Position + v.t * delta));
                    }
                }
                else
                {
                    res.Add((nextNode.Voxel, nextNode.Position));
                }
                nodeIndex = nextIndex;
            }
            res.Reverse();
        }
        return res;
    }

    private IEnumerable<ulong> EnumerateNeighbours(ulong voxel, int dx, int dy, int dz)
    {
        var l0Desc = _volume.Levels[0];
        var l1Desc = _volume.Levels[1];
        var l2Desc = _volume.Levels[2];
        var l0Index = VoxelMap.DecodeIndex(ref voxel); // should always be valid
        var l1Index = VoxelMap.DecodeIndex(ref voxel);
        var l2Index = VoxelMap.DecodeIndex(ref voxel);
        var l0Coords = l0Desc.IndexToVoxel(l0Index);
        var l1Coords = l1Desc.IndexToVoxel(l1Index); // not valid if l1 is invalid
        var l2Coords = l2Desc.IndexToVoxel(l2Index); // not valid if l2 is invalid

        if (l2Index != VoxelMap.IndexLevelMask)
        {
            // starting from L2 node
            var l2Neighbour = (l2Coords.x + dx, l2Coords.y + dy, l2Coords.z + dz);
            if (l2Desc.InBounds(l2Neighbour))
            {
                // L2->L2 in same L1 tile
                var neighbourVoxel = VoxelMap.EncodeIndex(l2Desc.VoxelToIndex(l2Neighbour));
                neighbourVoxel = VoxelMap.EncodeIndex(l1Index, neighbourVoxel);
                neighbourVoxel = VoxelMap.EncodeIndex(l0Index, neighbourVoxel);
                //Service.Log.Debug($"L2->L2 within L1: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{neighbourVoxel:X}");
                if (_volume.IsEmpty(neighbourVoxel))
                {
                    yield return neighbourVoxel;
                }
                // else: L2 is occupied, so we can't go there
                yield break;
            }
        }

        if (l1Index != VoxelMap.IndexLevelMask)
        {
            // starting from L1 node -or- L2 node at the boundary
            var l1Neighbour = (l1Coords.x + dx, l1Coords.y + dy, l1Coords.z + dz);
            if (l1Desc.InBounds(l1Neighbour))
            {
                // L1/L2->L1 in same L0 tile
                var neighbourVoxel = VoxelMap.EncodeIndex(l1Desc.VoxelToIndex(l1Neighbour));
                neighbourVoxel = VoxelMap.EncodeIndex(l0Index, neighbourVoxel);
                //Service.Log.Debug($"L1/L2->L1 within L0: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{neighbourVoxel:X}");
                if (_volume.IsEmpty(neighbourVoxel))
                {
                    // destination L1 is fully empty
                    yield return neighbourVoxel;
                }
                else if (l2Index != VoxelMap.IndexLevelMask)
                {
                    // L2->L2 across L1 border (but in same L0)
                    int l2X = dx == 0 ? l2Coords.x : dx > 0 ? 0 : l2Desc.NumCellsX - 1;
                    int l2Y = dy == 0 ? l2Coords.y : dy > 0 ? 0 : l2Desc.NumCellsY - 1;
                    int l2Z = dz == 0 ? l2Coords.z : dz > 0 ? 0 : l2Desc.NumCellsZ - 1;
                    var l2NeighbourVoxel = VoxelMap.EncodeSubIndex(neighbourVoxel, l2Desc.VoxelToIndex(l2X, l2Y, l2Z), 2);
                    //Service.Log.Debug($"- L2->L1 within L0: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{l2NeighbourVoxel:X}");
                    if (_volume.IsEmpty(l2NeighbourVoxel))
                    {
                        yield return l2NeighbourVoxel;
                    }
                }
                else
                {
                    // L1->L2 is same L0, enumerate all empty border voxels
                    foreach (var v in EnumerateBorder(neighbourVoxel, 2, dx, dy, dz))
                    {
                        //Service.Log.Debug($"- L1->L2 within L0: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{v:X}");
                        if (_volume.IsEmpty(v))
                        {
                            yield return v;
                        }
                    }
                }
                yield break;
            }
        }

        //if (l0Index != VoxelMap.IndexLevelMask) - this is always true
        {
            // starting from L0 node -or- L1/L2 node at the boundary
            var l0Neighbour = (l0Coords.x + dx, l0Coords.y + dy, l0Coords.z + dz);
            if (l0Desc.InBounds(l0Neighbour))
            {
                var neighbourVoxel = VoxelMap.EncodeIndex(l0Desc.VoxelToIndex(l0Neighbour));
                //Service.Log.Debug($"L0/L1/L2->L0: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{neighbourVoxel:X}");
                if (_volume.IsEmpty(neighbourVoxel))
                {
                    // destination L0 is fully empty
                    yield return neighbourVoxel;
                }
                else if (l1Index != VoxelMap.IndexLevelMask)
                {
                    // L1/L2 across L0 border
                    int l1X = dx == 0 ? l1Coords.x : dx > 0 ? 0 : l1Desc.NumCellsX - 1;
                    int l1Y = dy == 0 ? l1Coords.y : dy > 0 ? 0 : l1Desc.NumCellsY - 1;
                    int l1Z = dz == 0 ? l1Coords.z : dz > 0 ? 0 : l1Desc.NumCellsZ - 1;
                    var l1NeighbourVoxel = VoxelMap.EncodeSubIndex(neighbourVoxel, l1Desc.VoxelToIndex(l1X, l1Y, l1Z), 1);
                    //Service.Log.Debug($"- L1/L2->L1: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{l1NeighbourVoxel:X}");
                    if (_volume.IsEmpty(l1NeighbourVoxel))
                    {
                        // L1/L2 -> L1
                        yield return l1NeighbourVoxel;
                    }
                    else if (l2Index != VoxelMap.IndexLevelMask)
                    {
                        // L2->L2 across L0 border
                        int l2X = dx == 0 ? l2Coords.x : dx > 0 ? 0 : l2Desc.NumCellsX - 1;
                        int l2Y = dy == 0 ? l2Coords.y : dy > 0 ? 0 : l2Desc.NumCellsY - 1;
                        int l2Z = dz == 0 ? l2Coords.z : dz > 0 ? 0 : l2Desc.NumCellsZ - 1;
                        var l2NeighbourVoxel = VoxelMap.EncodeSubIndex(l1NeighbourVoxel, l2Desc.VoxelToIndex(l2X, l2Y, l2Z), 2);
                        //Service.Log.Debug($"- L2->L2: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{l2NeighbourVoxel:X}");
                        if (_volume.IsEmpty(l2NeighbourVoxel))
                        {
                            yield return l2NeighbourVoxel;
                        }
                    }
                    else
                    {
                        // L1->L2 across L0 border
                        foreach (var v in EnumerateBorder(l1NeighbourVoxel, 2, dx, dy, dz))
                        {
                            //Service.Log.Debug($"- L1->L2: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{v:X}");
                            if (_volume.IsEmpty(v))
                            {
                                yield return v;
                            }
                        }
                    }
                }
                else
                {
                    // L0->L1/L2
                    foreach (var v1 in EnumerateBorder(neighbourVoxel, 1, dx, dy, dz))
                    {
                        //Service.Log.Debug($"- L0->L1: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{v1:X}");
                        if (_volume.IsEmpty(v1))
                        {
                            // L0->L1
                            yield return v1;
                        }
                        else
                        {
                            foreach (var v2 in EnumerateBorder(v1, 2, dx, dy, dz))
                            {
                                //Service.Log.Debug($"-- L0->L2: {voxel:X4}{l2Index:X4}{l1Index:X4}{l0Index:X4}->{v2:X}");
                                if (_volume.IsEmpty(v2))
                                {
                                    // L0->L2
                                    yield return v2;
                                }
                            }
                        }
                    }
                }
                yield break;
            }
        }
    }

    private IEnumerable<ulong> EnumerateBorder(ulong voxel, int level, int dx, int dy, int dz)
    {
        var ld = _volume.Levels[level];
        var (xmin, xmax) = dx == 0 ? (0, ld.NumCellsX - 1) : dx > 0 ? (0, 0) : (ld.NumCellsX - 1, ld.NumCellsX - 1);
        var (ymin, ymax) = dy == 0 ? (0, ld.NumCellsY - 1) : dy > 0 ? (0, 0) : (ld.NumCellsY - 1, ld.NumCellsY - 1);
        var (zmin, zmax) = dz == 0 ? (0, ld.NumCellsZ - 1) : dz > 0 ? (0, 0) : (ld.NumCellsZ - 1, ld.NumCellsZ - 1);
        //Service.Log.Debug($"enum border: {voxel:X} @ {level} + ({dx}, {dy}, {dz}): {xmin}-{xmax}, {ymin}-{ymax}, {zmin}-{zmax}");
        for (int z = zmin; z <= zmax; ++z)
        {
            for (int x = xmin; x <= xmax; ++x)
            {
                for (int y = ymin; y <= ymax; ++y)
                {
                    yield return VoxelMap.EncodeSubIndex(voxel, ld.VoxelToIndex(x, y, z), level);
                }
            }
        }
    }

    private void VisitNeighbour(int parentIndex, ulong nodeVoxel)
    {
        var nodeIndex = _nodeLookup.GetValueOrDefault(nodeVoxel, -1);
        if (nodeIndex < 0)
        {
            // first time we're visiting this node, calculate heuristic
            nodeIndex = _nodes.Count;
            _nodes.Add(new() { GScore = float.MaxValue, HScore = float.MaxValue, Voxel = nodeVoxel, ParentIndex = parentIndex, OpenHeapIndex = -1 });
            _nodeLookup[nodeVoxel] = nodeIndex;
        }
        else if (!_allowReopen && _nodes[nodeIndex].OpenHeapIndex < 0)
        {
            // in closed list already - TODO: is it possible to visit again with lower cost?..
            return;
        }

        var nodeSpan = NodeSpan;
        ref var parentNode = ref nodeSpan[parentIndex];
        var enterPos = nodeVoxel == _goalVoxel ? _goalPos : VoxelSearch.FindClosestVoxelPoint(_volume, nodeVoxel, parentNode.Position);
        var nodeG = CalculateGScore(ref parentNode, nodeVoxel, enterPos, ref parentIndex);
        ref var curNode = ref nodeSpan[nodeIndex];
        if (nodeG + 0.00001f < curNode.GScore)
        {
            // new path is better
            curNode.GScore = nodeG;
            curNode.HScore = HeuristicDistance(nodeVoxel, enterPos);
            curNode.ParentIndex = parentIndex;
            curNode.Position = enterPos;
            AddToOpen(nodeIndex);
            //Service.Log.Debug($"volume pathfind: adding {nodeVoxel:X} (#{nodeIndex}), parent={parentIndex}, g={nodeG:f3}, h={_nodes[nodeIndex].HScore:f3}");

            if (curNode.HScore < _nodes[_bestNodeIndex].HScore)
                _bestNodeIndex = nodeIndex;
        }
    }

    private Random _rng = new();
    private float CalculateGScore(ref Node parent, ulong destVoxel, Vector3 destPos, ref int parentIndex)
    {
        float randomFactor = (float)_rng.NextDouble() * Service.Config.RandomnessMultiplier;

        float baseDistance;
        float parentBaseG;
        Vector3 fromPos;

        if (_useRaycast)
        {
            // check LoS from grandparent
            int grandParentIndex = parent.ParentIndex;
            ref var grandParentNode = ref NodeSpan[grandParentIndex];
            // TODO: invert LoS check to match path reconstruction step?
            var distanceSquared = (grandParentNode.Position - destPos).LengthSquared();
            if (distanceSquared <= _raycastLimitSq && VoxelSearch.LineOfSight(_volume, grandParentNode.Voxel, destVoxel, grandParentNode.Position, destPos))
            {
                parentIndex = grandParentIndex;
                baseDistance = MathF.Sqrt(distanceSquared);
                parentBaseG = grandParentNode.GScore;
                fromPos = grandParentNode.Position;
            }
            else
            {
                baseDistance = (parent.Position - destPos).Length();
                parentBaseG = parent.GScore;
                fromPos = parent.Position;
            }
        }
        else
        {
            baseDistance = (parent.Position - destPos).Length();
            parentBaseG = parent.GScore;
            fromPos = parent.Position;
        }

        float verticalDifference = MathF.Abs(fromPos.Y - destPos.Y);
        float verticalPenalty = 0.2f * verticalDifference;

        return parentBaseG + baseDistance + randomFactor + verticalPenalty;
    }

    private float HeuristicDistance(ulong nodeVoxel, Vector3 v) => nodeVoxel != _goalVoxel ? (v - _goalPos).Length() * 0.999f : 0;

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
