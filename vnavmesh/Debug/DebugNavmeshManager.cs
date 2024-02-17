using ImGuiNET;
using Navmesh.Movement;
using System;
using System.Numerics;

namespace Navmesh.Debug;

class DebugNavmeshManager : IDisposable
{
    private NavmeshManager _manager;
    private FollowPath _path;
    private UITree _tree = new();
    private DebugDrawer _dd;
    private DebugGameCollision _coll;
    private Vector3 _target;

    private DebugDetourNavmesh? _drawNavmesh;
    private DebugVoxelMap? _debugVoxelMap;

    public DebugNavmeshManager(DebugDrawer dd, DebugGameCollision coll, NavmeshManager manager, FollowPath path)
    {
        _manager = manager;
        _path = path;
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
        var progress = _manager.TaskProgress;
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

        if (_manager.Navmesh == null)
            return;

        var player = Service.ClientState.LocalPlayer;
        if (ImGui.Button("Set target to current pos"))
            _target = player?.Position ?? default;
        ImGui.SameLine();
        if (ImGui.Button("Set target to target pos"))
            _target = player?.TargetObject?.Position ?? default;
        ImGui.SameLine();
        ImGui.TextUnformatted($"Current target: {_target}");

        ImGui.Checkbox("Allow movement", ref _path.MovementAllowed);
        ImGui.Checkbox("Use raycasts", ref _path.UseRaycasts);
        ImGui.Checkbox("Use string pulling", ref _path.UseStringPulling);
        if (ImGui.Button("Pathfind to target using navmesh"))
            _path.MoveTo(_target);
        ImGui.SameLine();
        if (ImGui.Button("Pathfind to target using volume"))
            _path.FlyTo(_target);

        // draw current path
        if (player != null)
        {
            var from = player.Position;
            var color = 0xff00ff00;
            foreach (var to in _path.Waypoints)
            {
                _dd.DrawWorldLine(from, to, color);
                from = to;
                color = 0xff00ffff;
            }
        }

        var playerPos = Service.ClientState.LocalPlayer?.Position ?? default;
        _manager.Navmesh.Mesh.CalcTileLoc(playerPos.SystemToRecast(), out var playerTileX, out var playerTileZ);
        _tree.LeafNode($"Player tile: {playerTileX}x{playerTileZ}");
        _tree.LeafNode($"Player poly: {_path.Query?.FindNearestMeshPoly(playerPos):X}");
        _tree.LeafNode($"Target poly: {_path.Query?.FindNearestMeshPoly(_target):X}");
        _tree.LeafNode($"Player voxel: {_path.Query?.FindNearestVolumeVoxel(playerPos):X}");
        _tree.LeafNode($"Target voxel: {_path.Query?.FindNearestVolumeVoxel(_target):X}");

        _drawNavmesh ??= new(_manager.Navmesh.Mesh, _path.Query?.MeshQuery, _tree, _dd);
        _drawNavmesh.Draw();
        _debugVoxelMap ??= new(_manager.Navmesh.Volume, _tree, _dd);
        _debugVoxelMap.Draw();
    }

    private void OnNavmeshChanged(Navmesh? navmesh)
    {
        _drawNavmesh?.Dispose();
        _drawNavmesh = null;
        _debugVoxelMap?.Dispose();
        _debugVoxelMap = null;
    }
}
