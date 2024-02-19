using DotRecast.Detour;
using DotRecast.Recast;
using Navmesh.NavVolume;
using System;
using System.Threading.Tasks;

namespace Navmesh;

// async navmesh builder; TODO phase out in favour of NavmeshManager
public class LegacyNavmeshBuilder : IDisposable
{
    public enum State { NotBuilt, InProgress, Failed, Ready }

    public class IntermediateData
    {
        public int NumTilesX;
        public int NumTilesZ;
        public RcHeightfield[,] SolidHeightfields;
        public RcCompactHeightfield[,] CompactHeightfields;
        public RcContourSet[,] ContourSets;
        public RcPolyMesh[,] PolyMeshes;
        public RcPolyMeshDetail?[,] DetailMeshes;

        public IntermediateData(int numTilesX, int numTilesZ)
        {
            NumTilesX = numTilesX;
            NumTilesZ = numTilesZ;
            SolidHeightfields = new RcHeightfield[numTilesX, numTilesZ];
            CompactHeightfields = new RcCompactHeightfield[numTilesX, numTilesZ];
            ContourSets = new RcContourSet[numTilesX, numTilesZ];
            PolyMeshes = new RcPolyMesh[numTilesX, numTilesZ];
            DetailMeshes = new RcPolyMeshDetail?[numTilesX, numTilesZ];
        }
    }

    // config
    public NavmeshSettings Settings = new();

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

    public void Rebuild(bool includeTiles)
    {
        Clear();
        Service.Log.Debug("[navmesh] extract from scene");
        _scene = new();
        _scene.FillFromActiveLayout();
        Service.Log.Debug("[navmesh] schedule async build");
        _task = Task.Run(() => BuildNavmesh(_scene, includeTiles));
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

    private void BuildNavmesh(SceneDefinition scene, bool includeTiles)
    {
        try
        {
            var timer = Timer.Create();
            _builder = new(scene, Settings);

            // create tile data and add to navmesh
            _intermediates = new(_builder.NumTilesX, _builder.NumTilesZ);
            if (includeTiles)
            {
                for (int z = 0; z < _builder.NumTilesZ; ++z)
                {
                    for (int x = 0; x < _builder.NumTilesX; ++x)
                    {
                        (_intermediates.SolidHeightfields[x, z], _intermediates.CompactHeightfields[x, z], _intermediates.ContourSets[x, z], _intermediates.PolyMeshes[x, z], _intermediates.DetailMeshes[x, z]) = _builder.BuildTile(x, z);
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
