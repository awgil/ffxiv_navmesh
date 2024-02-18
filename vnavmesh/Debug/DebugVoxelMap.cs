using Navmesh.NavVolume;
using Navmesh.Render;
using System;
using System.Numerics;

namespace Navmesh.Debug;

public class DebugVoxelMap : IDisposable
{
    private VoxelMap _vm;
    private VoxelPathfind? _query;
    private UITree _tree;
    private DebugDrawer _dd;
    private EffectBox.Data? _visu;
    private int _numFilledVoxels;

    public DebugVoxelMap(VoxelMap vm, VoxelPathfind? query, UITree tree, DebugDrawer dd)
    {
        _vm = vm;
        _query = query;
        _tree = tree;
        _dd = dd;
        for (int i = 0; i < vm.Voxels.Length; ++i)
            if (vm.Voxels[i])
                ++_numFilledVoxels;
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

        _tree.LeafNode($"Bounds: {_vm.BoundsMin:f3} - {_vm.BoundsMax:f3}");
        _tree.LeafNode($"Voxel size: {_vm.CellSize:f3}");
        _tree.LeafNode($"Player's voxel: {_vm.WorldToVoxel(Service.ClientState.LocalPlayer?.Position ?? default)}");

        using (var nv = _tree.Node($"Voxels ({_vm.NumCellsX}x{_vm.NumCellsY}x{_vm.NumCellsZ})###voxels"))
        {
            if (nv.SelectedOrHovered)
                Visualize();
            if (nv.Opened)
            {
                for (int z = 0; z < _vm.NumCellsZ; ++z)
                {
                    using var nz = _tree.Node($"[*x*x{z}]");
                    if (!nz.Opened)
                        continue;
                    for (int x = 0; x < _vm.NumCellsX; ++x)
                    {
                        using var nx = _tree.Node($"[{x}x*x{z}]");
                        if (!nx.Opened)
                            continue;
                        for (int y = 0; y < _vm.NumCellsY; ++y)
                            if (_tree.LeafNode($"[{x}x{y}x{z}] = {_vm[x, y, z]}").SelectedOrHovered)
                                VisualizeCell(x, y, z);
                    }
                }
            }
        }

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
                    var coord = _vm.IndexToVoxel(n.Voxel);
                    if (_tree.LeafNode($"[{i}] {coord} ({n.Voxel:X}), parent={n.ParentIndex}, g={n.GScore:f4}, h={n.HScore:f4}").SelectedOrHovered)
                    {
                        VisualizeCell(coord.x, coord.y, coord.z);
                        ref var parent = ref ns[n.ParentIndex];
                        _dd.DrawWorldLine(parent.Position, n.Position, 0xff00ffff);
                        _dd.DrawWorldPointFilled(parent.Position, 2, 0xff00ffff);
                        _dd.DrawWorldPointFilled(n.Position, 2, 0xff0000ff);
                    }
                }
            }
        }
    }

    public void VisualizeVoxel(int voxel)
    {
        var (x, y, z) = _vm.IndexToVoxel(voxel);
        VisualizeCell(x, y, z);
    }

    private EffectBox.Data GetOrInitVisualizer()
    {
        if (_visu == null)
        {
            _visu = new(_dd.RenderContext, _numFilledVoxels, false);
            using var builder = _visu.Map(_dd.RenderContext);

            var timer = Timer.Create();
            var color = new Vector4(0.7f);
            var halfSize = _vm.CellSize * 0.5f;
            for (int z = 0; z < _vm.NumCellsZ; ++z)
            {
                for (int x = 0; x < _vm.NumCellsX; ++x)
                {
                    for (int y = 0; y < _vm.NumCellsY; ++y)
                    {
                        if (_vm[x, y, z])
                        {
                            var center = _vm.VoxelToWorld(x, y, z);
                            builder.Add(center - halfSize, center + halfSize, color, color);
                        }
                    }
                }
            }
            Service.Log.Debug($"voxel map visualization build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return _visu;
    }

    private void Visualize()
    {
        _dd.EffectBox.Draw(_dd.RenderContext, GetOrInitVisualizer());
    }

    private void VisualizeQuery()
    {
        if (_query != null)
        {
            var ns = _query.NodeSpan;
            for (int i = 0; i < ns.Length; ++i)
            {
                var coord = _vm.IndexToVoxel(ns[i].Voxel);
                VisualizeCell(coord.x, coord.y, coord.z);
            }
        }
    }

    private void VisualizeCell(int x, int y, int z)
    {
        _dd.DrawWorldAABB(_vm.VoxelToWorld(x, y, z), _vm.CellSize * 0.5f, 0xff0080ff, 1);
    }
}
