using Dalamud.Interface.Utility.Raii;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh.Debug;

internal class DebugNavmesh : IDisposable
{
    private NavmeshBuilder _navmesh;
    private Vector3 _target;
    private List<Vector3> _waypoints = new();

    private UITree _tree = new();
    private DebugDrawer _dd;
    private DebugExtractedCollision? _drawExtracted;
    private DebugSolidHeightfield? _drawSolidHeightfield;
    private DebugCompactHeightfield? _drawCompactHeightfield;
    private DebugContourSet? _drawContourSet;
    private DebugPolyMesh? _drawPolyMesh;

    public DebugNavmesh(DebugDrawer dd, NavmeshBuilder navmesh)
    {
        _navmesh = navmesh;
        _dd = dd;
    }

    public void Dispose()
    {
        _drawExtracted?.Dispose();
        _drawSolidHeightfield?.Dispose();
        _drawCompactHeightfield?.Dispose();
        _drawContourSet?.Dispose();
        _drawPolyMesh?.Dispose();
    }

    public void Draw()
    {
        DrawConfig();

        using (var d = ImRaii.Disabled(_navmesh.CurrentState == NavmeshBuilder.State.InProgress))
        {
            if (ImGui.Button("Rebuild navmesh"))
            {
                _drawExtracted?.Dispose();
                _drawExtracted = null;
                _drawSolidHeightfield?.Dispose();
                _drawSolidHeightfield = null;
                _drawCompactHeightfield?.Dispose();
                _drawCompactHeightfield = null;
                _drawContourSet?.Dispose();
                _drawContourSet = null;
                _drawPolyMesh?.Dispose();
                _drawPolyMesh = null;
                _navmesh.Rebuild();
                _waypoints.Clear();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"State: {_navmesh.CurrentState}");
        }

        if (_navmesh.CurrentState != NavmeshBuilder.State.Ready)
            return;

        if (ImGui.Button("Set target to current pos"))
            _target = Service.ClientState.LocalPlayer?.Position ?? default;
        ImGui.SameLine();
        if (ImGui.Button("Set target to target pos"))
            _target = Service.TargetManager.Target?.Position ?? default;
        ImGui.SameLine();
        ImGui.TextUnformatted($"Current target: {_target}");

        if (ImGui.Button("Pathfind to target"))
            _waypoints = _navmesh.Pathfind(Service.ClientState.LocalPlayer?.Position ?? default, _target);

        if (_waypoints.Count > 0)
        {
            var from = _waypoints[0];
            foreach (var to in _waypoints.Skip(1))
            {
                _dd.DrawWorldLine(from, to, 0xff00ff00);
                from = to;
            }
        }

        var intermediates = _navmesh.Intermediates!;
        _drawExtracted ??= new(_navmesh.CollisionGeometry, _tree, _dd);
        _drawExtracted.Draw();
        _drawSolidHeightfield ??= new(intermediates.GetSolidHeightfield(), _tree, _dd);
        _drawSolidHeightfield.Draw();
        _drawCompactHeightfield ??= new(intermediates.GetCompactHeightfield(), _tree, _dd);
        _drawCompactHeightfield.Draw();
        _drawContourSet ??= new(intermediates.GetContourSet(), _tree, _dd);
        _drawContourSet.Draw();
        _drawPolyMesh ??= new(intermediates.GetMesh(), _tree, _dd);
        _drawPolyMesh.Draw();
    }

    private void DrawConfig()
    {
        using var n = _tree.Node("Navmesh properties");
        if (!n.Opened)
            return;

        ImGui.InputFloat("The xz-plane cell size to use for fields. [Limit: > 0] [Units: wu]", ref _navmesh.CellSize);
        ImGui.InputFloat("The y-axis cell size to use for fields. [Limit: > 0] [Units: wu]", ref _navmesh.CellHeight);
        ImGui.InputFloat("The maximum slope that is considered walkable. [Limits: 0 <= value < 90] [Units: Degrees]", ref _navmesh.AgentMaxSlopeDeg);
        ImGui.InputFloat("Maximum ledge height that is considered to still be traversable. [Limit: >= 0] [Units: wu]", ref _navmesh.AgentMaxClimb);
        ImGui.InputFloat("Minimum floor to 'ceiling' height that will still allow the floor area to be considered walkable. [Limit: >= 3 * CellHeight] [Units: wu]", ref _navmesh.AgentHeight);
        ImGui.Checkbox("Filter low-hanging obstacles", ref _navmesh.FilterLowHangingObstacles);
        ImGui.Checkbox("Filter ledges", ref _navmesh.FilterLedgeSpans);
        ImGui.Checkbox("Filter low-height spans", ref _navmesh.FilterWalkableLowHeightSpans);
    }
}
