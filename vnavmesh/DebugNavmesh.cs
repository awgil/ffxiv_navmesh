using Dalamud.Interface.Utility.Raii;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh;

internal class DebugNavmesh : IDisposable
{
    private UITree _tree = new();
    private DebugGeometry _geom;
    private NavmeshBuilder _navmesh;
    private Vector3 _target;
    private List<Vector3> _waypoints = new();
    private bool _drawSolidHeightfield;

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

        DrawSolidHeightfield();
    }

    private void DrawSolidHeightfield()
    {
        if (_drawSolidHeightfield && _navmesh.Navmesh != null)
        {

        }

        using var n = _tree.Node("Heightfield (solid)", _navmesh.Navmesh == null);
        if (!n.Opened)
            return;

        var hf = _navmesh.Intermediates!.GetSolidHeightfield();
        ImGui.Checkbox("Draw visualization", ref _drawSolidHeightfield);
        _tree.LeafNode($"Num cells: {hf.width}x{hf.height}");
        _tree.LeafNode($"Bounds: [{hf.bmin}] - [{hf.bmax}]");
        _tree.LeafNode($"Cell size: {hf.cs}x{hf.ch}");
        _tree.LeafNode($"Border size: {hf.borderSize}");
        using var nc = _tree.Node("Cells");
        if (!nc.Opened)
            return;

        for (int z = 0; z < hf.height; ++z)
        {
            UITree.NodeRaii? nz = null;
            for (int x = 0; x < hf.width; ++x)
            {
                var span = hf.spans[z * hf.width + x];
                if (span == null)
                    continue;

                nz ??= _tree.Node($"[*x{z}]");
                if (!nz.Value.Opened)
                    break;

                using var nx = _tree.Node($"[{x}x{z}]");
                if (nx.Opened)
                {
                    while (span != null)
                    {
                        _tree.LeafNode($"{span.smin}-{span.smax} = {span.area:X}");
                        span = span.next;
                    }
                }
            }
            nz?.Dispose();
        }
    }
}
