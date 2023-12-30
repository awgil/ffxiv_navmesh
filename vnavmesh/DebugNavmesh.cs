using Dalamud.Interface.Utility.Raii;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh;

internal class DebugNavmesh : IDisposable
{
    private DebugGeometry _geom;
    private NavmeshBuilder _navmesh;
    private Vector3 _target;
    private List<Vector3> _waypoints = new();

    public DebugNavmesh(DebugGeometry geom, NavmeshBuilder navmesh)
    {
        _geom = geom;
        _navmesh = navmesh;
    }

    public void Dispose()
    {
    }

    public void Draw()
    {
        var state = _navmesh.CurrentState;
        using (var d = ImRaii.Disabled(state == NavmeshBuilder.State.InProgress))
        {
            if (ImGui.Button("Rebuild navmesh"))
            {
                _navmesh.Rebuild();
                _waypoints.Clear();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"State: {state}");
        }

        if (state != NavmeshBuilder.State.Ready)
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
                _geom.DrawWorldLine(from, to, 0xff00ff00);
                from = to;
            }
        }
    }
}
