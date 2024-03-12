using Dalamud.Interface.Utility.Raii;
using DotRecast.Detour;
using ImGuiNET;
using Navmesh.NavVolume;
using System;
using System.Threading.Tasks;

namespace Navmesh.Debug;

class DebugNavmeshCustom : IDisposable
{
    // async navmesh builder
    public class AsyncBuilder : IDisposable
    {
        public enum State { NotBuilt, InProgress, Failed, Ready }

        public class IntermediateData
        {
            public int NumTilesX;
            public int NumTilesZ;
            public NavmeshBuilder.Intermediates?[,] Tiles;

            public IntermediateData(int numTilesX, int numTilesZ)
            {
                NumTilesX = numTilesX;
                NumTilesZ = numTilesZ;
                Tiles = new NavmeshBuilder.Intermediates?[numTilesX, numTilesZ];
            }
        }

        // results - should not be accessed while task is running
        private SceneDefinition? _scene;
        private NavmeshBuilder? _builder;
        private NavmeshQuery? _query;
        private IntermediateData? _intermediates;
        private Task? _task;

        public State CurrentState => _task == null ? State.NotBuilt : !_task.IsCompleted ? State.InProgress : _task.IsFaulted ? State.Failed : State.Ready;
        public SceneDefinition? Scene => _task != null && _task.IsCompletedSuccessfully ? _scene : null;
        public SceneExtractor? Extractor => _task != null && _task.IsCompletedSuccessfully ? _builder?.Scene : null;
        public IntermediateData? Intermediates => _task != null && _task.IsCompletedSuccessfully ? _intermediates : null;
        public NavmeshQuery? Query => _task != null && _task.IsCompletedSuccessfully ? _query : null;
        public DtNavMesh? Navmesh => _task != null && _task.IsCompletedSuccessfully ? _builder?.Navmesh.Mesh : null;
        public DtNavMeshQuery? MeshQuery => _task != null && _task.IsCompletedSuccessfully ? _query?.MeshQuery : null;
        public VoxelMap? Volume => _task != null && _task.IsCompletedSuccessfully ? _builder?.Navmesh.Volume : null;
        public VoxelPathfind? VolumeQuery => _task != null && _task.IsCompletedSuccessfully ? _query?.VolumeQuery : null;

        public void Dispose()
        {
            Clear();
        }

        public void Rebuild(NavmeshSettings settings, bool includeTiles)
        {
            Clear();
            Service.Log.Debug("[navmesh] extract from scene");
            _scene = new();
            _scene.FillFromActiveLayout();
            Service.Log.Debug("[navmesh] schedule async build");
            _task = Task.Run(() => BuildNavmesh(_scene, settings, includeTiles));
        }

        public void Clear()
        {
            if (_task != null)
            {
                if (!_task.IsCompleted)
                    _task.Wait();
                _task.Dispose();
                _task = null;
            }
            _scene = null;
            _builder = null;
            _query = null;
            _intermediates = null;
            //GC.Collect();
        }

        private void BuildNavmesh(SceneDefinition scene, NavmeshSettings settings, bool includeTiles)
        {
            try
            {
                var timer = Timer.Create();
                _builder = new(scene, settings);

                // create tile data and add to navmesh
                _intermediates = new(_builder.NumTilesX, _builder.NumTilesZ);
                if (includeTiles)
                {
                    for (int z = 0; z < _builder.NumTilesZ; ++z)
                    {
                        for (int x = 0; x < _builder.NumTilesX; ++x)
                        {
                            _intermediates.Tiles[x, z] = _builder.BuildTile(x, z);
                        }
                    }
                }

                _query = new(_builder.Navmesh);
                Service.Log.Debug($"navmesh build time: {timer.Value().TotalMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Service.Log.Error($"Error building navmesh: {ex}");
                throw;
            }
        }
    }

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

    private NavmeshSettings _settings = new();
    private AsyncBuilder _navmesh = new();
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
                _settings.Draw();

        using (var d = ImRaii.Disabled(_navmesh.CurrentState == AsyncBuilder.State.InProgress))
        {
            if (ImGui.Button("Rebuild navmesh"))
            {
                Clear();
                _navmesh.Rebuild(_settings, true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Rebuild scene extract only"))
            {
                Clear();
                _navmesh.Rebuild(_settings, false);
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"State: {_navmesh.CurrentState}");
        }

        if (_navmesh.CurrentState != AsyncBuilder.State.Ready)
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
                        var inter = intermediates.Tiles[x, z];
                        if (inter == null)
                            continue;

                        using var nt = _tree.Node($"Tile {x}x{z}");
                        if (!nt.Opened)
                            continue;

                        var debug = _debugTiles[x, z] ??= new();
                        debug.DrawSolidHeightfield ??= new(inter.Value.SolidHeightfield, _tree, _dd);
                        debug.DrawSolidHeightfield.Draw();
                        debug.DrawCompactHeightfield ??= new(inter.Value.CompactHeightfield, _tree, _dd);
                        debug.DrawCompactHeightfield.Draw();
                        debug.DrawContourSet ??= new(inter.Value.ContourSet, _tree, _dd);
                        debug.DrawContourSet.Draw();
                        debug.DrawPolyMesh ??= new(inter.Value.PolyMesh, _tree, _dd);
                        debug.DrawPolyMesh.Draw();
                        if (inter.Value.DetailMesh != null)
                        {
                            debug.DrawPolyMeshDetail ??= new(inter.Value.DetailMesh, _tree, _dd);
                            debug.DrawPolyMeshDetail.Draw();
                        }
                    }
                }
            }
        }
        _drawNavmesh ??= new(navmesh, null, _tree, _dd);
        _drawNavmesh.Draw();
        _debugVoxelMap ??= new(_navmesh.Volume!, null, _tree, _dd);
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
