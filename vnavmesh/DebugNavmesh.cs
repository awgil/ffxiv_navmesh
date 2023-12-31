using Dalamud.Interface.Utility.Raii;
using DotRecast.Recast;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
        if (_navmesh.Navmesh == null)
            return;
        var hf = _navmesh.Intermediates!.GetSolidHeightfield();

        using var n = _tree.Node("Heightfield (solid)", _navmesh.Navmesh == null);
        if (n.SelectedOrHovered)
            VisualizeSolidHeightfield(hf);
        if (!n.Opened)
            return;

        var playerPos = Service.ClientState.LocalPlayer?.Position ?? default;
        _tree.LeafNode($"Num cells: {hf.width}x{hf.height}");
        _tree.LeafNode($"Bounds: [{hf.bmin}] - [{hf.bmax}]");
        _tree.LeafNode($"Cell size: {hf.cs}x{hf.ch}");
        _tree.LeafNode($"Border size: {hf.borderSize}");
        _tree.LeafNode($"Player's cell: {(playerPos.X - hf.bmin.X) / hf.cs}x{(playerPos.Y - hf.bmin.Y) / hf.ch}x{(playerPos.Z - hf.bmin.Z) / hf.cs}");
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
                if (nx.SelectedOrHovered)
                    VisualizeSolidHeightfieldCell(hf, x, z);
                if (nx.Opened)
                {
                    while (span != null)
                    {
                        if (_tree.LeafNode($"{span.smin}-{span.smax} = {span.area:X}").SelectedOrHovered)
                            VisualizeSolidHeightfieldSpan(hf, x, z, span);
                        span = span.next;
                    }
                }
            }
            nz?.Dispose();
        }
    }

    private void VisualizeSolidHeightfield(RcHeightfield hf)
    {
        for (int z = 0; z < hf.height; ++z)
            for (int x = 0; x < hf.width; ++x)
                VisualizeSolidHeightfieldCell(hf, x, z);
    }

    private void VisualizeSolidHeightfieldCell(RcHeightfield hf, int x, int z)
    {
        var span = hf.spans[z * hf.width + x];
        while (span != null)
        {
            VisualizeSolidHeightfieldSpan(hf, x, z, span);
            span = span.next;
        }
    }

    private void VisualizeSolidHeightfieldSpan(RcHeightfield hf, int x, int z, RcSpan span)
    {
        var mtx = new FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3();
        mtx.M11 = mtx.M33 = 0.5f * hf.cs;
        mtx.M22 = (span.smax - span.smin) * 0.5f * hf.ch;
        mtx.M41 = hf.bmin.X + (x + 0.5f) * hf.cs;
        mtx.M42 = hf.bmin.Y + (span.smin + span.smax) * 0.5f * hf.ch;
        mtx.M43 = hf.bmin.Z + (z + 0.5f) * hf.cs;
        _geom.DrawBox(ref mtx, new(1, span.area == 0 ? 0 : 1, 0, 1));
    }
}
