using ImGuiNET;
using Navmesh.Movement;
using Navmesh.NavVolume;
using System;
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

        ImGui.Checkbox("Show mesh status in DTR Bar", ref _dtr.ShowDtrBar);
        ImGui.Checkbox("Allow movement", ref _path.MovementAllowed);
        ImGui.Checkbox("Align camera to movement direction", ref _path.AlignCamera);
        ImGui.Checkbox("Auto load mesh when changing zone", ref _manager.AutoLoad);
        ImGui.Checkbox("Use raycasts", ref _manager.UseRaycasts);
        ImGui.Checkbox("Use string pulling", ref _manager.UseStringPulling);
        if (ImGui.Button("Pathfind to target using navmesh"))
            _asyncMove.MoveTo(_target, false);
        ImGui.SameLine();
        if (ImGui.Button("Pathfind to target using volume"))
            _asyncMove.MoveTo(_target, true);

        // draw current path
        if (player != null)
        {
            var from = playerPos;
            var color = 0xff00ff00;
            foreach (var to in _path.Waypoints)
            {
                _dd.DrawWorldLine(from, to, color);
                _dd.DrawWorldPointFilled(to, 3, 0xff0000ff);
                from = to;
                color = 0xff00ffff;
            }
        }

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

    private void OnNavmeshChanged(Navmesh? navmesh, NavmeshQuery? query)
    {
        _drawNavmesh?.Dispose();
        _drawNavmesh = null;
        _debugVoxelMap?.Dispose();
        _debugVoxelMap = null;
    }
}
