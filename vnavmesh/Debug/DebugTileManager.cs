using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Navmesh.Debug;

public sealed unsafe class DebugTileManager(TileManager tiles, DebugDrawer drawer, DebugGameCollision coll) : IDisposable
{
    private readonly TileManager _tiles = tiles;
    private readonly UITree _tree = new();
    private readonly DebugDrawer _dd = drawer;
    private readonly DebugGameCollision _coll = coll;

    public static readonly Vector2 TileSize = new(40, 40);
    private readonly DebugVoxelMap?[,] _map = new DebugVoxelMap[16, 16];

    public void Dispose() { }

    public void Draw()
    {
        if (ImGui.Button("Force rebuild"))
            _tiles.Rebuild();

        ImGui.TextUnformatted($"Tasks: {_tiles.NumTasks}");

        using (var nk = _tree.Node($"Tiles ({_tiles.Scene.NumTiles})###tiles"))
        {
            if (nk.Opened)
            {
                var len = _tiles.Scene.RowLength;
                for (var i = 0; i < len; i++)
                {
                    for (var j = 0; j < len; j++)
                    {
                        var task = _tiles.Tasks[i, j];
                        uint col = task == null ? 0 : (task.Status switch
                        {
                            TaskStatus.RanToCompletion => 0xff76db3a,
                            TaskStatus.Faulted or TaskStatus.Canceled => 0xff374bcc,
                            TaskStatus.Running or TaskStatus.WaitingToRun or TaskStatus.WaitingForActivation => 0xff767676,
                            _ => 0xff00aeff
                        });

                        var pos = ImGui.GetCursorScreenPos();
                        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + TileSize, col);
                        var label = $"{i:d2} {j:d2}";
                        ImGui.SetCursorScreenPos(pos + (TileSize - ImGui.CalcTextSize(label)) * 0.5f);
                        ImGui.Text(label);
                        ImGui.SetCursorScreenPos(pos);
                        ImGui.Dummy(TileSize);
                        if (j + 1 < len)
                            ImGui.SameLine();
                    }
                }
            }
        }

        using (var nv = _tree.Node($"Voxels ({_tiles.Scene.NumTiles})###voxels"))
        {
            if (nv.Opened)
            {
                var len = _tiles.Scene.RowLength;
                for (var i = 0; i < len; i++)
                {
                    for (var j = 0; j < len; j++)
                    {
                        using (var x = _tree.Node($"[{i}x{j}]", _tiles.Submaps[i, j] == null))
                        {
                            if (x.Opened && _tiles.Submaps[i, j] != null)
                            {
                                _map[i, j] ??= new(_tiles.Submaps[i, j]!, null, _tree, _dd);
                                _map[i, j]?.Draw();
                            }
                        }
                    }
                }
            }
        }
    }

    /*
    private void DrawTile(SceneTracker.Tile tile)
    {
        double minStale = 2000.0;
        double maxStale = 10000.0;

        var staleness = -(_tiles.GetTimer(tile.X, tile.Z) - _tiles.DebounceMs);
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
    */
}
