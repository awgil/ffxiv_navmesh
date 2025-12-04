using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Navmesh.Movement;
using Navmesh.NavVolume;
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

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
        _manager.NavmeshChanged += OnNavmeshChanged;
    }

    public void Dispose()
    {
        _manager.NavmeshChanged -= OnNavmeshChanged;
        _drawNavmesh?.Dispose();
        _debugVoxelMap?.Dispose();
    }

    public void Draw()
    {
        var progress = _manager.LoadTaskProgress;
        if (progress >= 0)
        {
            ImGui.ProgressBar(progress, new Vector2(200, 0));
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
        ImGui.TextUnformatted($"z{_manager.Scene.LastLoadedZone}, {string.Join(".", _manager.GetActiveFestivals().Select(f => f.ToString("X")))}");
        ImGui.TextUnformatted($"Num pathfinding tasks: {(_manager.PathfindInProgress ? 1 : 0)} in progress, {_manager.NumQueuedPathfindRequests} queued");

        var s = Slog.Instance();
        var en = s.Enabled;
        if (ImGui.Checkbox($"Enable trace logging", ref en))
        {
            s.Enabled = en;
        }
        ImGuiComponents.HelpMarker("Trace logging produces A LOT of data. Only enable this if someone has specifically asked for it (or if you're really interested in the internals of vnav).");

        if (_manager.Navmesh == null || _manager.Query == null)
            return;

        var player = Service.ObjectTable.LocalPlayer;
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
            ExportBitmap(playerPos);

        ImGui.Checkbox("Allow movement", ref _path.MovementAllowed);
        ImGui.Checkbox("Use raycasts", ref _manager.UseRaycasts);
        ImGui.Checkbox("Use string pulling", ref _manager.UseStringPulling);
        if (Service.PluginInterface.IsDev)
            ImGui.Checkbox("Seed mode", ref _manager.SeedMode);
        if (ImGui.Button("Pathfind to target using navmesh"))
            _asyncMove.MoveTo(_target, false);
        ImGui.SameLine();
        if (ImGui.Button("Pathfind to target using volume"))
            _asyncMove.MoveTo(_target, true);

        if (ImGui.Button("Record seed point"))
        {
            Task.Run(async () =>
            {
                var ff = await FloodFill.GetAsync();
                ff.AddPoint(Service.ClientState.TerritoryType, playerPos);
                await ff.Serialize();
            });
        }
        ImGui.SameLine();
        var pts = FloodFill.Get()?.Seeds.TryGetValue(Service.ClientState.TerritoryType, out var vs) == true ? vs : [];
        ImGui.TextUnformatted($"Num points for current zone: {pts.Count}");

        DrawPosition("Player", playerPos);
        DrawPosition("Target", _target);
        DrawPosition("Flag", MapUtils.FlagToPoint(_manager.Query) ?? default);
        DrawPosition("Floor", _manager.Query.FindPointOnFloor(playerPos) ?? default);
        _manager.Navmesh!.Mesh.CalcTileLoc(playerPos.SystemToRecast(), out var playerTileX, out var playerTileZ);

        _drawNavmesh ??= new(_manager.Navmesh.Mesh, _manager.Query.MeshQuery, _manager.Query.LastPath, _tree, _dd);
        _drawNavmesh.Draw(playerTileX, playerTileZ);
        if (_manager.Navmesh.Volume != null)
        {
            _debugVoxelMap ??= new(_manager.Navmesh.Volume, _manager.Query.VolumeQuery, _tree, _dd);
            _debugVoxelMap.Draw();
        }
    }

    private void DrawPosition(string tag, Vector3 position)
    {
        if (position == default)
        {
            _tree.LeafNode($"{tag} position: <none>");
            return;
        }

        _manager.Navmesh!.Mesh.CalcTileLoc(position.SystemToRecast(), out var tileX, out var tileZ);
        _tree.LeafNode($"{tag} position: {position:f3}, tile: {tileX}x{tileZ}, poly: {_manager.Query!.FindNearestMeshPoly(position):X}");
        var voxel = _manager.Query.FindNearestVolumeVoxel(position);
        if (_tree.LeafNode($"{tag} voxel: {voxel:X}###{tag}voxel").SelectedOrHovered && voxel != VoxelMap.InvalidVoxel)
            _debugVoxelMap?.VisualizeVoxel(voxel);
    }

    private void ExportBitmap(Vector3 startingPos)
    {
        _manager.BuildBitmap(startingPos, "D:\\navmesh.bmp", 0.5f);
    }

    private void OnNavmeshChanged(Navmesh? navmesh, NavmeshQuery? query)
    {
        _drawNavmesh?.Dispose();
        _drawNavmesh = null;
        _debugVoxelMap?.Dispose();
        _debugVoxelMap = null;
    }
}
