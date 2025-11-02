using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using System;

namespace Navmesh.Debug;

public sealed unsafe class DebugTileManager(TileManager tiles, DebugDrawer drawer, DebugGameCollision coll) : IDisposable
{
    private readonly TileManager _tiles = tiles;
    private readonly UITree _tree = new();
    private readonly DebugDrawer _dd = drawer;
    private readonly DebugGameCollision _coll = coll;

    private SceneTracker Scene => _tiles.Scene;

    public void Dispose() { }

    public void Draw()
    {
        using (var nt = _tree.Node($"Tasks: {_tiles.NumTasks}###tasks"))
        {
            if (nt.Opened)
            {
                for (var i = 0; i < _tiles.Tasks.GetLength(0); i++)
                {
                    for (var j = 0; j < _tiles.Tasks.GetLength(1); j++)
                    {
                        var task = _tiles.Tasks[i, j];
                        if (task == null || task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                            continue;

                        _tree.LeafNode($"[{i:d2}x{j:d2}] {task.Status}");
                    }
                }
            }
        }

        using (var nk = _tree.Node($"Tiles###tiles"))
        {
            if (nk.Opened)
            {
                for (var i = 0; i < Scene.NumTilesInRow; i++)
                {
                    for (var j = 0; j < Scene.NumTilesInRow; j++)
                    {
                        var tile = Scene.Tiles[i, j];
                        if (tile?.Objects is not { } obj || obj.Count == 0)
                            continue;

                        DrawTile(tile);
                    }
                }
            }
        }
    }

    private void DrawTile(SceneTracker.Tile tile)
    {
        double minStale = 2000.0;
        double maxStale = 10000.0;

        var staleness = -(tile.Timer - Scene.DebounceMS);
        var alpha = 0xff - (byte)(Math.Max(0, Math.Min(maxStale, staleness) - minStale) / (maxStale - minStale) * 0x80);

        var color = (uint)alpha << 24 | 0xFFFFFF;

        using var node = _tree.Node($"[{tile.X}x{tile.Z}] {tile.Objects.Count} ({staleness * 0.001f:f2}s old)###tile{tile.X}_{tile.Z}", false, color);
        if (!node.Opened)
            return;

        foreach (var (key, obj) in tile.Objects)
        {
            var typestr = obj.Type.ToString();
            if (typestr == "0")
                typestr = "Terrain (no collider)";
            var n2 = _tree.LeafNode($"[{key:X16}] {typestr}");
            if (n2.SelectedOrHovered && obj.Type != default)
            {
                _dd.DrawWorldLine(Service.ClientState.LocalPlayer?.Position ?? default, obj.Instance.WorldTransform.Row3, 0xFFFF00FF);

                var coll = FindCollider(obj.Type, key);
                if (coll != null)
                    _coll.VisualizeCollider(coll, default, default);
            }
        }
    }

    private unsafe Collider* FindCollider(InstanceType type, ulong key)
    {
        var layout = LayoutWorld.Instance()->ActiveLayout;
        var insts = layout != null ? LayoutUtils.FindPtr(ref layout->InstancesByType, type) : null;
        var inst = insts != null ? LayoutUtils.FindPtr(ref *insts, key) : null;
        var coll = inst != null ? inst->GetCollider() : null;
        return coll;
    }
}
