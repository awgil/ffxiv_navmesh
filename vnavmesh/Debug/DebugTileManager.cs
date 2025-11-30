using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Navmesh.Render;
using System;
using System.Collections.Generic;
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
    private LayoutObjectSet Scene => _tiles.Scene;
    private NavmeshDebug DebugStuff => _tiles.DebugData;

    private readonly EffectMesh.Data[,] _drawMeshes = new EffectMesh.Data[16, 16];
    private readonly DebugNavmeshCustom.PerTile?[,] _perTile = new DebugNavmeshCustom.PerTile?[16, 16];

    (int X, int Z) Focused => _hovered.Item1 >= 0 && _hovered.Item2 >= 0 ? (_hovered.Item1, _hovered.Item2) : _selected;
    NavmeshDebug.IntermediatesData? FocusedTile => Focused.X >= 0 && Focused.Z >= 0 ? DebugStuff.Intermediates[Focused.X, Focused.Z] : null;

    public static readonly Vector2 UITileSize = new(40, 40);
    public static readonly Vector3 BoundsMin = new(-1024);

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
        var pos2 = Service.ObjectTable.LocalPlayer?.Position ?? new();

        var playerPosAdj = pos2 - BoundsMin;
        var playerX = (int)playerPosAdj.X / 128;
        var playerZ = (int)playerPosAdj.Z / 128;

        ImGui.Checkbox("Record debug data", ref _tiles.DebugData.Enabled);
        var pa = Scene.PauseActions;
        if (ImGui.Checkbox("Pause (some) animations", ref pa))
            Scene.PauseActions = pa;
        var timeSinceUpd = (DateTime.Now - Scene.LastUpdate).TotalSeconds;
        if (ImGui.Button("Restart tile watch"))
            _tiles.InitGrid();
        ImGui.SameLine();
        ImGui.TextUnformatted($"Last update: {timeSinceUpd:f3}s ago");
        if (ImGui.Button($"Rebuild tile {playerX}x{playerZ}"))
        {
            _selected = (playerX, playerZ);
            _tiles.RebuildTile(playerX, playerZ);
        }

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
                        var task = DebugStuff.BuildTasks[i, j];
                        uint col = task == null ? 0 : _selected == (i, j) ? 0xffba7917 : (task.Status switch
                        {
                            TaskStatus.RanToCompletion => 0xff76db3a,
                            TaskStatus.Faulted or TaskStatus.Canceled => 0xff374bcc,
                            TaskStatus.Running or TaskStatus.WaitingToRun or TaskStatus.WaitingForActivation => 0xff767676,
                            _ => 0xff00aeff
                        });

                        var pos = ImGui.GetCursorScreenPos();
                        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + UITileSize, col);
                        var label = $"{i:X}{j:X}";
                        ImGui.SetCursorScreenPos(pos + (UITileSize - ImGui.CalcTextSize(label)) * 0.5f);
                        ImGui.Text(label);
                        ImGui.SetCursorScreenPos(pos);
                        ImGui.Dummy(UITileSize);

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

        if (FocusedTile is (var tile, var inter))
        {
            using var nt = _tree.Node($"Tile {tile.X}x{tile.Z}###focused");
            if (!nt.Opened)
                return;

            DrawInstances(tile);

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
                            {
                                _coll.VisualizeCollider(coll, default, default);
                                Vector3 t;
                                coll->GetTranslation(&t);
                                _dd.DrawWorldLine(Service.ObjectTable.LocalPlayer?.Position ?? default, t, 0xFFFF00FF);
                            }
                        }
                    }
                }
            }

            if (highlightAll)
                _dd.EffectMesh?.Draw(_dd.RenderContext, GetOrInitVisualizer(tile));

            if (inter != null)
            {
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
    }

    private EffectMesh.Data GetOrInitVisualizer(Tile tile)
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

    private string _meshFilter = "o6b1_a5_stc02";

    private void DrawInstances(Tile t)
    {
        using var ni = _tree.Node("Mesh instances");
        if (!ni.Opened)
            return;

        ImGui.InputText("Filter", ref _meshFilter);

        var meshIndex = 0;
        foreach (var group in t.Objects.Values.GroupBy(o => o.Mesh.Path))
        {
            var name = group.Key;
            var mesh = group.First().Mesh;
            var instances = group.Select(g => g.Instance).ToList();

            using var nm = _tree.Node($"{name}: flags={mesh.MeshType}");
            if (nm.SelectedOrHovered)
                VisualizeMeshInstances(t, meshIndex);

            if (nm.Opened)
            {
                using (var np = _tree.Node($"Parts ({mesh.Parts.Count})###parts", mesh.Parts.Count == 0))
                {
                    if (np.Opened)
                    {
                        int partIndex = 0;
                        foreach (var p in mesh.Parts)
                        {
                            using var npi = _tree.Node(partIndex.ToString());
                            if (npi.SelectedOrHovered)
                                VisualizeMeshPart(t, mesh, meshIndex, partIndex);

                            if (npi.Opened)
                            {
                                using (var nv = _tree.Node($"Vertices ({p.Vertices.Count})###verts"))
                                {
                                    if (nv.Opened)
                                    {
                                        int j = 0;
                                        foreach (var v in p.Vertices)
                                            if (_tree.LeafNode($"{j++}: {v:f3}").SelectedOrHovered)
                                                VisualizeVertex(instances, v);
                                    }
                                }

                                using (var nt = _tree.Node($"Primitives ({p.Primitives.Count})###prims"))
                                {
                                    if (nt.Opened)
                                    {
                                        int j = 0;
                                        foreach (var t2 in p.Primitives)
                                        {
                                            var v1 = p.Vertices[t2.V1];
                                            var v2 = p.Vertices[t2.V2];
                                            var v3 = p.Vertices[t2.V3];
                                            if (_tree.LeafNode($"{j++}: {t2.V1}x{t2.V2}x{t2.V3} ({v1:f3} x {v2:f3} x {v3:f3}) ({t2.Flags})").SelectedOrHovered)
                                                VisualizeTriangle(instances, v1, v2, v3);
                                        }
                                    }
                                }
                            }

                            ++partIndex;
                        }
                    }
                }

                using (var ni2 = _tree.Node($"Instances ({group.Count()})###instances", !group.Any()))
                {
                    if (ni2.Opened)
                    {
                        int instIndex = 0;
                        foreach (var i3 in group)
                        {
                            var i = i3.Instance;
                            if (_tree.LeafNode($"{instIndex}: {i.WorldBounds.Min:f3}-{i.WorldBounds.Max:f3}, R0 = {i.WorldTransform.Row0:f3}, R1 = {i.WorldTransform.Row1:f3}, R2 = {i.WorldTransform.Row2:f3}, R3 = {i.WorldTransform.Row3:f3}, {i.WorldBounds.Min:f3} - {i.WorldBounds.Max:f3} (+: {i.ForceSetPrimFlags}, -: {i.ForceClearPrimFlags})").SelectedOrHovered)
                                VisualizeMeshInstance(t, meshIndex, instIndex);
                            ++instIndex;
                        }
                    }
                }

            }

            meshIndex++;
        }
    }

    private void VisualizeMeshInstances(Tile t, int meshIndex)
    {
        _dd.EffectMesh?.DrawSingle(_dd.RenderContext, GetOrInitVisualizer(t), meshIndex);
    }

    private void VisualizeMeshInstance(Tile t, int meshIndex, int instIndex)
    {
        if (_dd.EffectMesh == null)
            return;
        var visu = GetOrInitVisualizer(t);
        var visuMesh = visu.Meshes[meshIndex];
        visuMesh.FirstInstance += instIndex;
        visuMesh.NumInstances = 1;
        _dd.EffectMesh.Bind(_dd.RenderContext, false, false);
        visu.Bind(_dd.RenderContext);
        visu.DrawManual(_dd.RenderContext, visuMesh);
    }

    private void VisualizeMeshPart(Tile t, SceneExtractor.Mesh mesh, int meshIndex, int partIndex)
    {
        if (_dd.EffectMesh == null)
            return;
        var visu = GetOrInitVisualizer(t);
        var visuMesh = visu.Meshes[meshIndex];
        visuMesh.FirstPrimitive += mesh.Parts.Take(partIndex).Sum(part => part.Primitives.Count);
        visuMesh.NumPrimitives = mesh.Parts[partIndex].Primitives.Count;
        _dd.EffectMesh.Bind(_dd.RenderContext, false, false);
        visu.Bind(_dd.RenderContext);
        visu.DrawManual(_dd.RenderContext, visuMesh);
    }

    private void VisualizeVertex(List<SceneExtractor.MeshInstance> inst, Vector3 v)
    {
        foreach (var i in inst)
            _dd.DrawWorldPoint(i.WorldTransform.TransformCoordinate(v), 5, 0xff0000ff);
    }

    private void VisualizeTriangle(List<SceneExtractor.MeshInstance> inst, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        foreach (var i in inst)
            _dd.DrawWorldTriangle(i.WorldTransform.TransformCoordinate(v1), i.WorldTransform.TransformCoordinate(v2), i.WorldTransform.TransformCoordinate(v3), 0xff0000ff);
    }
}
