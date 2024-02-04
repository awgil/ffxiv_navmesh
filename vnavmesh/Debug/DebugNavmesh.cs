using Dalamud.Interface.Utility.Raii;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh.Debug;

internal class DebugNavmesh : IDisposable
{
    // TODO: should each debug drawer handle tiled geometry itself?
    private class PerTile : IDisposable
    {
        public DebugSolidHeightfield? DrawSolidHeightfield;
        public DebugCompactHeightfield? DrawCompactHeightfield;
        public DebugContourSet? DrawContourSet;
        public DebugPolyMesh? DrawPolyMesh;
        public DebugPolyMeshDetail? DrawPolyMeshDetail;

        public void Dispose()
        {
            DrawSolidHeightfield?.Dispose();
            DrawCompactHeightfield?.Dispose();
            DrawContourSet?.Dispose();
            DrawPolyMesh?.Dispose();
            DrawPolyMeshDetail?.Dispose();
        }
    }

    private NavmeshBuilder _navmesh;
    private Vector3 _target;
    private List<Vector3> _waypoints = new();

    private UITree _tree = new();
    private DebugDrawer _dd;
    private DebugGameCollision _coll;
    private DebugExtractedCollision? _drawExtracted;
    private PerTile[,]? _debugTiles;
    private DebugDetourNavmesh? _drawNavmesh;
    private DebugVoxelMap? _debugVoxelMap;

    public DebugNavmesh(DebugDrawer dd, DebugGameCollision coll, NavmeshBuilder navmesh)
    {
        _navmesh = navmesh;
        _dd = dd;
        _coll = coll;
    }

    public void Dispose()
    {
        _drawExtracted?.Dispose();
        if (_debugTiles != null)
            foreach (var t in _debugTiles)
                t?.Dispose();
        _drawNavmesh?.Dispose();
        _debugVoxelMap?.Dispose();
    }

    public void Draw()
    {
        using (var nsettings = _tree.Node("Navmesh properties"))
            if (nsettings.Opened)
                _navmesh.Settings.Draw();

        using (var d = ImRaii.Disabled(_navmesh.CurrentState == NavmeshBuilder.State.InProgress))
        {
            if (ImGui.Button("Rebuild navmesh"))
            {
                Clear();
                _navmesh.Rebuild();
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear navmesh"))
            {
                Clear();
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

        if (ImGui.Button("Pathfind to target using navmesh"))
            _waypoints = _navmesh.Pathfind(Service.ClientState.LocalPlayer?.Position ?? default, _target);
        ImGui.SameLine();
        if (ImGui.Button("Pathfind to target using volume"))
            _waypoints = _navmesh.PathfindVolume(Service.ClientState.LocalPlayer?.Position ?? default, _target);

        if (_waypoints.Count > 0)
        {
            var from = _waypoints[0];
            foreach (var to in _waypoints.Skip(1))
            {
                _dd.DrawWorldLine(from, to, 0xff00ff00);
                from = to;
            }
        }

        var navmesh = _navmesh.Navmesh!;
        navmesh.CalcTileLoc((Service.ClientState.LocalPlayer?.Position ?? default).SystemToRecast(), out var playerTileX, out var playerTileZ);
        _tree.LeafNode($"Player tile: {playerTileX}x{playerTileZ}");

        _drawExtracted ??= new(_navmesh.Scene!, _navmesh.Extractor!, _tree, _dd, _coll);
        _drawExtracted.Draw();
        var intermediates = _navmesh.Intermediates;
        if (intermediates != null)
        {
            using var n = _tree.Node("Intermediates");
            if (n.Opened)
            {
                _debugTiles ??= new PerTile[intermediates.NumTilesX, intermediates.NumTilesZ];
                for (int z = 0; z < intermediates.NumTilesZ; ++z)
                {
                    for (int x = 0; x < intermediates.NumTilesX; ++x)
                    {
                        using var nt = _tree.Node($"Tile {x}x{z}");
                        if (!nt.Opened)
                            continue;

                        var debug = _debugTiles[x, z] ??= new();
                        debug.DrawSolidHeightfield ??= new(intermediates.SolidHeightfields[x, z], _tree, _dd);
                        debug.DrawSolidHeightfield.Draw();
                        debug.DrawCompactHeightfield ??= new(intermediates.CompactHeightfields[x, z], _tree, _dd);
                        debug.DrawCompactHeightfield.Draw();
                        debug.DrawContourSet ??= new(intermediates.ContourSets[x, z], _tree, _dd);
                        debug.DrawContourSet.Draw();
                        debug.DrawPolyMesh ??= new(intermediates.PolyMeshes[x, z], _tree, _dd);
                        debug.DrawPolyMesh.Draw();
                        if (intermediates.DetailMeshes[x, z] is var dmesh && dmesh != null)
                        {
                            debug.DrawPolyMeshDetail ??= new(dmesh, _tree, _dd);
                            debug.DrawPolyMeshDetail.Draw();
                        }
                    }
                }
            }
        }
        _drawNavmesh ??= new(navmesh, _navmesh.Query!, _tree, _dd);
        _drawNavmesh.Draw();
        _debugVoxelMap ??= new(_navmesh.Volume!, _tree, _dd);
        _debugVoxelMap.Draw();
    }

    private void Clear()
    {
        _drawExtracted?.Dispose();
        _drawExtracted = null;
        if (_debugTiles != null)
            foreach (var t in _debugTiles)
                t?.Dispose();
        _debugTiles = null;
        _drawNavmesh?.Dispose();
        _drawNavmesh = null;
        _debugVoxelMap?.Dispose();
        _debugVoxelMap = null;
        _waypoints.Clear();
        _navmesh.Clear();
    }
}
