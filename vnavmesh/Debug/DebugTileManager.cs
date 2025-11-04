using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
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
    private (int, int) _hovered;
    private (int, int) _selected = (-1, -1);
    private SceneTracker Scene => _tiles.Scene;

    (int X, int Z) Focused => _hovered.Item1 >= 0 && _hovered.Item2 >= 0 ? (_hovered.Item1, _hovered.Item2) : _selected;
    SceneTracker.TileChangeset? FocusedTile => Focused.X >= 0 && Focused.Z >= 0 ? _tiles.Scene._tiles[Focused.X, Focused.Z] : null;

    public static readonly Vector2 TileSize = new(40, 40);
    public static readonly Vector3 BoundsMin = new(-1024);

    public void Dispose() { }

    public void Draw()
    {
        if (ImGui.Button("Force rebuild"))
            _tiles.Rebuild();

        ImGui.TextUnformatted($"Tasks: {_tiles.NumTasks}");

        _hovered = (-1, -1);

        using (var nt = _tree.Node("Tile map"))
        {
            if (nt.Opened)
            {
                using var _ = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
                var len = _tiles.Scene.RowLength;
                for (var i = 0; i < len; i++)
                {
                    for (var j = 0; j < len; j++)
                    {
                        var task = _tiles.Tasks[i, j];
                        uint col = task == null ? 0 : _selected == (i, j) ? 0xffba7917 : (task.Status switch
                        {
                            TaskStatus.RanToCompletion => 0xff76db3a,
                            TaskStatus.Faulted or TaskStatus.Canceled => 0xff374bcc,
                            TaskStatus.Running or TaskStatus.WaitingToRun or TaskStatus.WaitingForActivation => 0xff767676,
                            _ => 0xff00aeff
                        });

                        var pos = ImGui.GetCursorScreenPos();
                        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + TileSize, col);
                        var label = $"{i:X}{j:X}";
                        ImGui.SetCursorScreenPos(pos + (TileSize - ImGui.CalcTextSize(label)) * 0.5f);
                        ImGui.Text(label);
                        ImGui.SetCursorScreenPos(pos);
                        ImGui.Dummy(TileSize);

                        if (ImGui.IsItemClicked())
                        {
                            var ix = (i, j);
                            _selected = _selected == ix ? (-1, -1) : ix;
                        }
                        if (ImGui.IsItemHovered())
                            _hovered = (i, j);

                        if (j + 1 < len)
                            ImGui.SameLine();
                    }
                }
            }
        }

        if (FocusedTile is { } tile)
        {
            var tileMin = BoundsMin + new Vector3(tile.X * Scene.TileUnits, 0, tile.Z * Scene.TileUnits);
            var tileMax = tileMin + new Vector3(Scene.TileUnits, 2048, Scene.TileUnits);

            _dd.DrawWorldAABB(new AABB() { Min = tileMin, Max = tileMax }, 0xFF0080FF);

            using var nt = _tree.Node($"Tile {tile.X}x{tile.Z} ({tile.Objects.Count} objects)###focused");
            if (!nt.Opened)
                return;

            foreach (var (key, obj) in tile.Objects)
            {
                var node = _tree.LeafNode($"[{key:X16}] {obj.Type}");
                if (node.SelectedOrHovered && obj.Type > 0)
                {
                    var coll = FindCollider(obj.Type, key);
                    if (coll != null)
                        _coll.VisualizeCollider(coll, default, default);
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
    */

    private unsafe Collider* FindCollider(InstanceType type, ulong key)
    {
        var layout = LayoutWorld.Instance()->ActiveLayout;
        var insts = layout != null ? LayoutUtils.FindPtr(ref layout->InstancesByType, type) : null;
        var inst = insts != null ? LayoutUtils.FindPtr(ref *insts, key) : null;
        var coll = inst != null ? inst->GetCollider() : null;
        return coll;
    }
}
