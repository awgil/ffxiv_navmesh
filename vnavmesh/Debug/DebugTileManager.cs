using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

using Navmesh.Render;
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Navmesh.Debug;

public sealed unsafe class DebugTileManager : IDisposable
{
    private readonly NavmeshManager _tiles;
    private readonly UITree _tree = new();
    private readonly DebugDrawer _dd;
    private readonly DebugGameCollision _coll;
    private (int, int) _hovered;
    private (int, int) _selected = (-1, -1);
    private ColliderSet Scene => _tiles.Scene;

    private readonly EffectMesh.Data[,] _drawMeshes = new EffectMesh.Data[16, 16];
    private readonly DebugNavmeshCustom.PerTile?[,] _perTile = new DebugNavmeshCustom.PerTile?[16, 16];

    (int X, int Z) Focused => _hovered.Item1 >= 0 && _hovered.Item2 >= 0 ? (_hovered.Item1, _hovered.Item2) : _selected;
    ColliderSet.TileInternal? FocusedTile => Focused.X >= 0 && Focused.Z >= 0 ? _tiles.Scene._tiles[Focused.X, Focused.Z] : null;

    public static readonly Vector2 TileSize = new(40, 40);
    public static readonly Vector3 BoundsMin = new(-1024);

    private bool _saveOthers;

    public DebugTileManager(NavmeshManager tiles, DebugDrawer drawer, DebugGameCollision coll)
    {
        _tiles = tiles;
        _dd = drawer;
        _coll = coll;
        Service.ClientState.ZoneInit += Clear;
    }

    public void Dispose()
    {
        Service.ClientState.ZoneInit -= Clear;
    }

    private void Clear(Dalamud.Game.ClientState.ZoneInitEventArgs obj)
    {
        _hovered = (-1, -1);

        for (var i = 0; i < _drawMeshes.GetLength(0); i++)
            for (var j = 0; j < _drawMeshes.GetLength(1); j++)
            {
                _drawMeshes[i, j] = null!;
                _perTile[i, j] = null;
            }
    }

    public void Draw()
    {
        var pos2 = Service.ClientState.LocalPlayer?.Position ?? new();
        var tx = (int)((pos2.X + 1024) / Scene.TileUnits);
        var tz = (int)((pos2.Z + 1024) / Scene.TileUnits);

        _hovered = (-1, -1);

        using (var nt = _tree.Node("Tile map"))
        {
            if (nt.Opened)
            {
                using var _ = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
                var len = _tiles.Scene.RowLength;
                for (var j = 0; j < len; j++)
                {
                    for (var i = 0; i < len; i++)
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

                        if (task != null)
                        {
                            if (ImGui.IsItemClicked())
                            {
                                var ix = (i, j);
                                _selected = _selected == ix ? (-1, -1) : ix;
                            }
                            if (ImGui.IsItemHovered())
                                _hovered = (i, j);
                        }

                        if (i + 1 < len)
                            ImGui.SameLine();
                    }
                }
            }
        }

        if (FocusedTile is { } tile)
        {
            using var nt = _tree.Node($"Tile {tile.X}x{tile.Z} ({tile.Objects.Count} objects)###focused");
            if (!nt.Opened)
                return;

            bool highlightAll = false;

            using (var no = _tree.Node($"Objects ({tile.Objects.Count})###objs"))
            {
                if (no.Opened)
                {
                    highlightAll = true;
                    foreach (var (key, obj) in tile.Objects)
                    {
                        var node = _tree.LeafNode($"[{key:X16}] {obj.Type}; flags=+{obj.Instance.ForceSetPrimFlags} -{obj.Instance.ForceClearPrimFlags}");
                        if (node.SelectedOrHovered && obj.Type > 0)
                        {
                            highlightAll = false;
                            var coll = FindCollider(obj.Type, key);
                            if (coll != null)
                                _coll.VisualizeCollider(coll, default, default);
                        }
                    }
                }
            }

            if (highlightAll)
                _dd.EffectMesh?.Draw(_dd.RenderContext, GetOrInitVisualizer(tile));

            var inter = _tiles.Intermediates[tile.X, tile.Z];
            if (inter == null)
                return;

            var debug = _perTile[tile.X, tile.Z] ??= new();
            debug.DrawSolidHeightfield ??= new(inter.GetSolidHeightfield(), _tree, _dd);
            debug.DrawSolidHeightfield.Draw();
            debug.DrawCompactHeightfield ??= new(inter.GetCompactHeightfield(), _tree, _dd);
            debug.DrawCompactHeightfield.Draw();
            debug.DrawContourSet ??= new(inter.GetContourSet(), _tree, _dd);
            debug.DrawContourSet.Draw();
            debug.DrawPolyMesh ??= new(inter.GetMesh(), _tree, _dd);
            debug.DrawPolyMesh.Draw();
        }
    }

    private EffectMesh.Data GetOrInitVisualizer(ColliderSet.TileInternal tile)
    {
        ref var visu = ref _drawMeshes[tile.X, tile.Z];

        var objsGrouped = tile.Objects.Values.GroupBy(v => v.Mesh.Path).ToDictionary(d => d.Key, d => d.ToList());

        if (visu == null)
        {
            int nv = 0, np = 0, ni = 0;
            foreach (var objs in objsGrouped.Values)
            {
                var mesh = objs[0].Mesh;
                foreach (var part in mesh.Parts)
                {
                    nv += part.Vertices.Count;
                    np += part.Primitives.Count;
                }
                ni += objs.Count;
            }

            visu = new(_dd.RenderContext, nv, np, ni, false);
            using var builder = visu.Map(_dd.RenderContext);

            var timer = Timer.Create();
            nv = np = ni = 0;
            foreach (var obj in objsGrouped.Values)
            {
                var mesh = obj[0].Mesh;
                var color = DebugExtractedCollision.MeshColor(mesh);
                int nvm = 0, npm = 0;
                foreach (var part in mesh.Parts)
                {
                    foreach (var v in part.Vertices)
                        builder.AddVertex(v);
                    foreach (var p in part.Primitives)
                        builder.AddTriangle(nvm + p.V1, nvm + p.V3, nvm + p.V2);
                    nvm += part.Vertices.Count;
                    npm += part.Primitives.Count;
                }
                foreach (var inst in obj)
                    builder.AddInstance(new(inst.Instance.WorldTransform, color));
                builder.AddMesh(nv, np, npm, ni, obj.Count);
                nv += nvm;
                np += npm;
                ni += obj.Count;
            }
        }
        return visu;
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
