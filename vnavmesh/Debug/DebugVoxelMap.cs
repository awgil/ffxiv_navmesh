using Navmesh.NavVolume;
using Navmesh.Render;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh.Debug;

public class DebugVoxelMap : IDisposable
{
    private VoxelMap _vm;
    private VoxelPathfind? _query;
    private UITree _tree;
    private DebugDrawer _dd;
    private EffectMesh.Data? _visu;
    private Dictionary<VoxelMap.Tile, (int firstBox, int numBoxes)> _visuBoxes = new();
    private int[] _numSubdivPerLevel;
    private int[] _numLeavesPerLevel;

    public DebugVoxelMap(VoxelMap vm, VoxelPathfind? query, UITree tree, DebugDrawer dd)
    {
        _vm = vm;
        _query = query;
        _tree = tree;
        _dd = dd;

        _numSubdivPerLevel = new int[vm.Levels.Length];
        _numLeavesPerLevel = new int[vm.Levels.Length];
        InitTile(vm.RootTile);
    }

    public void Dispose()
    {
        _visu?.Dispose();
    }

    public void Draw()
    {
        using var nr = _tree.Node("Voxel map");
        if (!nr.Opened)
            return;

        var playerVoxel = _vm.FindLeafVoxel(Service.ObjectTable.LocalPlayer?.Position ?? default);
        _tree.LeafNode($"Player's voxel: {playerVoxel.voxel:X} (empty={playerVoxel.empty})");

        for (int level = 0; level < _vm.Levels.Length; ++level)
        {
            var l = _vm.Levels[level];
            _tree.LeafNode($"Level {level}: {_numSubdivPerLevel[level]} subdivided, {_numLeavesPerLevel[level]} leaves, size={l.CellSize:f3}, nc={l.NumCellsX}x{l.NumCellsY}x{l.NumCellsZ}");
        }

        DrawTile(_vm.RootTile, "Root tile", false);

        using (var nv = _tree.Node($"Query nodes ({_query?.NodeSpan.Length})###query", _query == null || _query.NodeSpan.Length == 0))
        {
            if (nv.SelectedOrHovered)
                VisualizeQuery();
            if (nv.Opened && _query != null)
            {
                var ns = _query.NodeSpan;
                for (int i = 0; i < ns.Length; ++i)
                {
                    ref var n = ref ns[i];
                    var bounds = _vm.VoxelBounds(n.Voxel, 0);
                    if (_tree.LeafNode($"[{i}] {n.Voxel:X} ({bounds.min:f3}-{bounds.max:f3}), parent={n.ParentIndex}, g={n.GScore:f4}, h={n.HScore:f4}").SelectedOrHovered)
                    {
                        VisualizeVoxel(n.Voxel);
                        ref var parent = ref ns[n.ParentIndex];
                        _dd.DrawWorldLine(parent.Position, n.Position, 0xff00ffff);
                        _dd.DrawWorldPointFilled(parent.Position, 2, 0xff00ffff);
                        _dd.DrawWorldPointFilled(n.Position, 2, 0xff0000ff);
                    }
                }
            }
        }
    }

    public void VisualizeVoxel(ulong voxel) => VisualizeCell(_vm.VoxelBounds(voxel, 0));

    private void InitTile(VoxelMap.Tile tile)
    {
        foreach (var t in tile.Contents)
            if ((t & VoxelMap.VoxelIdMask) == VoxelMap.VoxelIdMask)
                ++_numLeavesPerLevel[tile.Level];
        _numSubdivPerLevel[tile.Level] += tile.Subdivision.Count;
        foreach (var sub in tile.Subdivision)
            InitTile(sub);
    }

    private void DrawTile(VoxelMap.Tile tile, string name, bool visualize = true)
    {
        using var nr = _tree.Node($"{name}: {tile.BoundsMin:f3} - {tile.BoundsMax:f3} ({tile.Subdivision.Count} subtiles)");
        if (visualize && nr.SelectedOrHovered)
            VisualizeTile(tile);
        if (!nr.Opened)
            return;

        for (ushort i = 0; i < tile.Contents.Length; i++)
        {
            if ((tile.Contents[i] & VoxelMap.VoxelOccupiedBit) != 0)
            {
                var v = tile.LevelDesc.IndexToVoxel(i);
                var cn = $"{v.x}x{v.y}x{v.z}";
                var id = tile.Contents[i] & VoxelMap.VoxelIdMask;
                if (id == VoxelMap.VoxelIdMask)
                {
                    // fully solid
                    if (_tree.LeafNode($"{v.x}x{v.y}x{v.z}").SelectedOrHovered)
                        VisualizeCell(tile.CalculateSubdivisionBounds(v));
                }
                else
                {
                    // subdivided
                    DrawTile(tile.Subdivision[id], $"{cn} -> #{id}");
                }
            }
        }
    }

    private void InitTileVisualizer(VoxelMap.Tile tile, EffectMesh.Data.Builder builder, AnalyticMeshBox box)
    {
        var start = builder.NumInstances;
        for (ushort i = 0; i < tile.Contents.Length; i++)
        {
            if ((tile.Contents[i] & VoxelMap.VoxelOccupiedBit) != 0)
            {
                var id = tile.Contents[i] & VoxelMap.VoxelIdMask;
                if (id == VoxelMap.VoxelIdMask)
                {
                    var bounds = tile.CalculateSubdivisionBounds(tile.LevelDesc.IndexToVoxel(i));
                    box.Add(bounds.min, bounds.max, new(0.7f));
                }
                else
                {
                    InitTileVisualizer(tile.Subdivision[id], builder, box);
                }
            }
        }
        if (builder.NumInstances > start)
            _visuBoxes[tile] = (start, builder.NumInstances - start);
    }

    private EffectMesh.Data GetOrInitVisualizer()
    {
        if (_visu == null)
        {
            _visu = new(_dd.RenderContext, 8, 12, _numLeavesPerLevel.Sum(), false);
            using var builder = _visu.Map(_dd.RenderContext);
            var box = new AnalyticMeshBox(builder);

            var timer = Timer.Create();
            InitTileVisualizer(_vm.RootTile, builder, box);
            Service.Log.Debug($"voxel map visualization build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return _visu;
    }

    private void VisualizeTile(VoxelMap.Tile tile)
    {
        if (_dd.EffectMesh == null)
            return;
        var data = GetOrInitVisualizer();
        if (_visuBoxes.TryGetValue(tile, out var b))
            _dd.EffectMesh.DrawSubset(_dd.RenderContext, data, b.firstBox, b.numBoxes);
    }

    private void VisualizeQuery()
    {
        if (_query != null)
        {
            var ns = _query.NodeSpan;
            for (int i = 0; i < ns.Length; ++i)
            {
                VisualizeVoxel(ns[i].Voxel);
            }
        }
    }

    private void VisualizeCell((Vector3 min, Vector3 max) bounds)
    {
        _dd.DrawWorldAABB((bounds.min + bounds.max) * 0.5f, (bounds.max - bounds.min) * 0.5f, 0xff0080ff, 1);
        _dd.DrawWorldLine(Service.ObjectTable.LocalPlayer?.Position ?? default, (bounds.min + bounds.max) * 0.5f, 0xFF0080ff, 1);
    }
}
