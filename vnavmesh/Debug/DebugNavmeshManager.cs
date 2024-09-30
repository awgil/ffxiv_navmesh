using ImGuiNET;
using Navmesh.Movement;
using Navmesh.NavVolume;
using System;
using System.IO;
using System.Numerics;

namespace Navmesh.Debug;

class DebugNavmeshManager : IDisposable
{
    private NavmeshManager _manager;
    private FollowPath _path;
    private AsyncMoveRequest _asyncMove;
    private DTRProvider _dtr;
    private UITree _tree = new();
    private DebugDrawer _dd;
    private DebugGameCollision _coll;
    private Vector3 _target;

    private DebugDetourNavmesh? _drawNavmesh;
    private DebugVoxelMap? _debugVoxelMap;

    public DebugNavmeshManager(DebugDrawer dd, DebugGameCollision coll, NavmeshManager manager, FollowPath path, AsyncMoveRequest move, DTRProvider dtr)
    {
        _manager = manager;
        _path = path;
        _asyncMove = move;
        _dtr = dtr;
        _dd = dd;
        _coll = coll;
        _manager.OnNavmeshChanged += OnNavmeshChanged;
    }

    public void Dispose()
    {
        _manager.OnNavmeshChanged -= OnNavmeshChanged;
        _drawNavmesh?.Dispose();
        _debugVoxelMap?.Dispose();
    }

    public void Draw()
    {
        var progress = _manager.LoadTaskProgress;
        if (progress >= 0)
        {
            ImGui.ProgressBar(progress, new(200, 0));
        }
        else
        {
            ImGui.SetNextItemWidth(100);
            if (ImGui.Button("Reload"))
                _manager.Reload(true);
            ImGui.SameLine();
            if (ImGui.Button("Rebuild"))
                _manager.Reload(false);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(_manager.CurrentKey);
        ImGui.TextUnformatted($"Num pathfinding tasks: {(_manager.PathfindInProgress ? 1 : 0)} in progress, {_manager.NumQueuedPathfindRequests} queued");

        if (_manager.Navmesh == null || _manager.Query == null)
            return;

        var player = Service.ClientState.LocalPlayer;
        var playerPos = player?.Position ?? default;
        ImGui.TextUnformatted($"Player pos: {playerPos}");
        if (ImGui.Button("Set target to current pos"))
            _target = player?.Position ?? default;
        ImGui.SameLine();
        if (ImGui.Button("Set target to target pos"))
            _target = player?.TargetObject?.Position ?? default;
        ImGui.SameLine();
        if (ImGui.Button("Set target to flag position"))
            _target = MapUtils.FlagToPoint(_manager.Query) ?? default;
        ImGui.SameLine();
        ImGui.TextUnformatted($"Current target: {_target}");

        if (ImGui.Button("Export bitmap"))
            ExportBitmap(_manager.Navmesh, _manager.Query, playerPos);

        ImGui.Checkbox("Allow movement", ref _path.MovementAllowed);
        ImGui.Checkbox("Use raycasts", ref _manager.UseRaycasts);
        ImGui.Checkbox("Use string pulling", ref _manager.UseStringPulling);
        if (ImGui.Button("Pathfind to target using navmesh"))
            _asyncMove.MoveTo(_target, false);
        ImGui.SameLine();
        if (ImGui.Button("Pathfind to target using volume"))
            _asyncMove.MoveTo(_target, true);

        DrawPosition("Player", playerPos);
        DrawPosition("Target", _target);
        DrawPosition("Flag", MapUtils.FlagToPoint(_manager.Query) ?? default);
        DrawPosition("Floor", _manager.Query.FindPointOnFloor(playerPos) ?? default);

        _drawNavmesh ??= new(_manager.Navmesh.Mesh, _manager.Query.MeshQuery, _tree, _dd);
        _drawNavmesh.Draw();
        if (_manager.Navmesh.Volume != null)
        {
            _debugVoxelMap ??= new(_manager.Navmesh.Volume, _manager.Query.VolumeQuery, _tree, _dd);
            _debugVoxelMap.Draw();
        }
    }

    private void DrawPosition(string tag, Vector3 position)
    {
        _manager.Navmesh!.Mesh.CalcTileLoc(position.SystemToRecast(), out var tileX, out var tileZ);
        _tree.LeafNode($"{tag} position: {position:f3}, tile: {tileX}x{tileZ}, poly: {_manager.Query!.FindNearestMeshPoly(position):X}");
        var voxel = _manager.Query.FindNearestVolumeVoxel(position);
        if (_tree.LeafNode($"{tag} voxel: {voxel:X}###{tag}voxel").SelectedOrHovered && voxel != VoxelMap.InvalidVoxel)
            _debugVoxelMap?.VisualizeVoxel(voxel);
    }

    private void ExportBitmap(Navmesh navmesh, NavmeshQuery query, Vector3 startingPos)
    {
        var startPoly = query.FindNearestMeshPoly(startingPos);
        var reachablePolys = query.FindReachableMeshPolys(startPoly);

        Vector3 min = new(1024), max = new(-1024);
        foreach (var p in reachablePolys)
        {
            navmesh.Mesh.GetTileAndPolyByRefUnsafe(p, out var tile, out var poly);
            for (int i = 0; i < poly.vertCount; ++i)
            {
                var v = NavmeshBitmap.GetVertex(tile, i);
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        var bitmap = new NavmeshBitmap(min, max, 0.5f);
        foreach (var p in reachablePolys)
        {
            bitmap.RasterizePolygon(navmesh.Mesh, p);
        }
        //for (int i = 0, numTiles = navmesh.Mesh.GetParams().maxTiles; i < numTiles; ++i)
        //{
        //    //if (i != 9)
        //    //    continue;
        //    var tile = navmesh.Mesh.GetTile(i);
        //    if (tile.data == null)
        //        continue;

        //    for (int j = 0; j < tile.data.header.polyCount; ++j)
        //    {
        //        //if (j != 583)
        //        //    continue;
        //        var p = tile.data.polys[j];
        //        if (p.GetPolyType() != DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION && p.vertCount >= 3)
        //        {
        //            bitmap.RasterizePolygon(tile, p);
        //        }
        //    }
        //}

        //var bitmap = new NavmeshBitmap(navmesh, new(-128, 10, -128), new(0, 30, 0), 0.5f);
        using var fs = new FileStream("D:\\navmesh.bmp", FileMode.Create, FileAccess.Write);
        using var wr = new BinaryWriter(fs);
        wr.Write((ushort)0x4D42); // 'BM' (word)
        wr.Write(14 + 12 + 8 + bitmap.Data.Length); // file size (dword)
        wr.Write(0); // reserved (2x word)
        wr.Write(14 + 12 + 8); // pixel data offset (dword)
        wr.Write(12); // BITMAPCOREHEADER size (dword)
        wr.Write((ushort)bitmap.Width); // width in pixels (word)
        wr.Write((ushort)bitmap.Height); // height in pixels (word)
        wr.Write((ushort)1); // num color planes (word)
        wr.Write((ushort)1); // bits per pixel (word)
        wr.Write(0x00ffff00); // color 0
        wr.Write(0x00000000); // color 1
        wr.Write(bitmap.Data); // pixel data
    }

    private void OnNavmeshChanged(Navmesh? navmesh, NavmeshQuery? query)
    {
        _drawNavmesh?.Dispose();
        _drawNavmesh = null;
        _debugVoxelMap?.Dispose();
        _debugVoxelMap = null;
    }
}
