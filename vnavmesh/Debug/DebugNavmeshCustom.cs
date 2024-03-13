using Dalamud.Interface.Utility.Raii;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using ImGuiNET;
using Navmesh.NavVolume;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Navmesh.Debug;

class DebugNavmeshCustom : IDisposable
{
    private record struct HeightfieldComparison(float DurationOld, float DurationNew, bool Identical);

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

        public void Rebuild(NavmeshSettings settings, bool flyable, bool includeTiles)
        {
            Clear();
            Service.Log.Debug("[navmesh] extract from scene");
            _scene = new();
            _scene.FillFromActiveLayout();
            Service.Log.Debug("[navmesh] schedule async build");
            _task = Task.Run(() => BuildNavmesh(_scene, flyable, settings, includeTiles));
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

        private void BuildNavmesh(SceneDefinition scene, bool flyable, NavmeshSettings settings, bool includeTiles)
        {
            try
            {
                var timer = Timer.Create();
                _builder = new(scene, flyable, settings);

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
        public HeightfieldComparison? HFC;

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
    private HeightfieldComparison? _globalHFC;
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
            if (ImGui.Button("Rebuild navmesh flyable"))
            {
                Clear();
                _navmesh.Rebuild(_settings, true, true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Rebuild navmesh non-flyable"))
            {
                Clear();
                _navmesh.Rebuild(_settings, false, true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Rebuild scene extract only"))
            {
                Clear();
                _navmesh.Rebuild(_settings, true, false);
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
                using (var ng = _tree.Node("Global"))
                {
                    if (ng.Opened)
                    {
                        _globalHFC ??= CompareAllHeightfields(_navmesh.Extractor!);
                        _tree.LeafNode($"Old: {_globalHFC.Value.DurationOld:f3}");
                        _tree.LeafNode($"New: {_globalHFC.Value.DurationNew:f3}");
                        _tree.LeafNode($"Match: {_globalHFC.Value.Identical}");
                    }
                }

                for (int z = 0; z < intermediates.NumTilesZ; ++z)
                {
                    for (int x = 0; x < intermediates.NumTilesX; ++x)
                    {
                        using var nt = _tree.Node($"Tile {x}x{z}");
                        if (!nt.Opened)
                            continue;

                        var inter = intermediates.Tiles[x, z];
                        var debug = _debugTiles[x, z] ??= new();
                        if (inter != null)
                        {
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

                        using (var nhfc = _tree.Node("HF comparison"))
                        {
                            if (nhfc.Opened)
                            {
                                debug.HFC ??= CompareHeightfields(x, z, _navmesh.Extractor!, true);
                                _tree.LeafNode($"Old: {debug.HFC.Value.DurationOld:f3}");
                                _tree.LeafNode($"New: {debug.HFC.Value.DurationNew:f3}");
                                _tree.LeafNode($"Match: {debug.HFC.Value.Identical}");
                            }
                        }
                    }
                }
            }
        }
        _drawNavmesh ??= new(navmesh, null, _tree, _dd);
        _drawNavmesh.Draw();
        if (_navmesh.Volume != null)
        {
            _debugVoxelMap ??= new(_navmesh.Volume, null, _tree, _dd);
            _debugVoxelMap.Draw();
        }
    }

    private void Clear()
    {
        _drawExtracted?.Dispose();
        _drawExtracted = null;
        _globalHFC = null;
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

    private HeightfieldComparison CompareHeightfields(int tx, int tz, SceneExtractor scene, bool flyable)
    {
        var telemetry = new RcContext();
        var boundsMin = new Vector3(-1024);
        var boundsMax = new RcVec3f(1024);
        var numTilesXZ = _settings.NumTiles[0];
        var tileWidth = (boundsMax.X - boundsMin.X) / numTilesXZ;
        var tileHeight = (boundsMax.Z - boundsMin.Z) / numTilesXZ;
        var walkableClimbVoxels = (int)MathF.Floor(_settings.AgentMaxClimb / _settings.CellHeight);
        var walkableRadiusVoxels = (int)MathF.Ceiling(_settings.AgentRadius / _settings.CellSize);
        var walkableNormalThreshold = _settings.AgentMaxSlopeDeg.Degrees().Cos();
        var borderSizeVoxels = 3 + walkableRadiusVoxels;
        var borderSizeWorld = borderSizeVoxels * _settings.CellSize;
        var tileSizeXVoxels = (int)MathF.Ceiling(tileWidth / _settings.CellSize) + 2 * borderSizeVoxels;
        var tileSizeZVoxels = (int)MathF.Ceiling(tileHeight / _settings.CellSize) + 2 * borderSizeVoxels;
        var tileBoundsMin = new Vector3(boundsMin.X + tx * tileWidth, boundsMin.Y, boundsMin.Z + tz * tileHeight);
        var tileBoundsMax = new Vector3(tileBoundsMin.X + tileWidth, boundsMax.Y, tileBoundsMin.Z + tileHeight);
        tileBoundsMin.X -= borderSizeWorld;
        tileBoundsMin.Z -= borderSizeWorld;
        tileBoundsMax.X += borderSizeWorld;
        tileBoundsMax.Z += borderSizeWorld;

        var timer = Timer.Create();
        var shfOld = new RcHeightfield(tileSizeXVoxels, tileSizeZVoxels, tileBoundsMin.SystemToRecast(), tileBoundsMax.SystemToRecast(), _settings.CellSize, _settings.CellHeight, borderSizeVoxels);
        var rasterizerOld = new NavmeshRasterizer(shfOld, walkableNormalThreshold, walkableClimbVoxels, telemetry);
        rasterizerOld.RasterizeOld(scene, true, true, true, flyable);
        var dur1 = (float)timer.Value().TotalSeconds;

        var shfNew = new RcHeightfield(tileSizeXVoxels, tileSizeZVoxels, tileBoundsMin.SystemToRecast(), tileBoundsMax.SystemToRecast(), _settings.CellSize, _settings.CellHeight, borderSizeVoxels);
        var rasterizerNew = new NavmeshRasterizer(shfNew, walkableNormalThreshold, walkableClimbVoxels, telemetry);
        rasterizerNew.Rasterize(scene, true, true, true, false, flyable);
        var dur2 = (float)timer.Value().TotalSeconds;

        bool identical = true;
        int ispan = 0;
        for (int z = 0; z < tileSizeZVoxels; ++z)
        {
            for (int x = 0; x < tileSizeXVoxels; ++x)
            {
                var so = shfOld.spans[ispan];
                var sn = shfNew.spans[ispan];
                while (so != 0 && sn != 0)
                {
                    ref var spanOld = ref shfOld.Span(so);
                    ref var spanNew = ref shfNew.Span(sn);
                    identical &= (spanOld.smin == spanNew.smin) && (spanOld.smax == spanNew.smax) && (spanOld.area == spanNew.area);
                    so = spanOld.next;
                    sn = spanNew.next;
                }
                identical &= (so == 0) && (sn == 0);
                ispan++;
            }
        }
        return new(dur1, dur2, identical);
    }

    private HeightfieldComparison CompareAllHeightfields(SceneExtractor scene)
    {
        float dur1 = 0, dur2 = 0;
        bool identical = true;
        var numTilesXZ = _settings.NumTiles[0];
        for (int tz = 0; tz < numTilesXZ; ++tz)
        {
            for (int tx = 0; tx < numTilesXZ; ++tx)
            {
                var hfc = CompareHeightfields(tx, tz, scene, true);
                dur1 += hfc.DurationOld;
                dur2 += hfc.DurationNew;
                identical &= hfc.Identical;
            }
        }
        return new(dur1, dur2, identical);
    }
}
