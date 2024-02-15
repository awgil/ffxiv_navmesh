using Dalamud.Interface.Utility.Raii;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.Debug;

class DebugNavmeshCustom : IDisposable
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

    private LegacyNavmeshBuilder _navmesh = new();
    private UITree _tree = new();
    private DebugDrawer _dd;
    private DebugGameCollision _coll;
    private DebugExtractedCollision? _drawExtracted;
    private PerTile[,]? _debugTiles;
    private DebugDetourNavmesh? _drawNavmesh;
    private DebugVoxelMap? _debugVoxelMap;

    public DebugNavmeshCustom(DebugDrawer dd, DebugGameCollision coll)
    {
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

        using (var d = ImRaii.Disabled(_navmesh.CurrentState == LegacyNavmeshBuilder.State.InProgress))
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

        if (_navmesh.CurrentState != LegacyNavmeshBuilder.State.Ready)
            return;

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
        _drawNavmesh ??= new(navmesh, null, _tree, _dd);
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
        _navmesh.Clear();
    }
}
