using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace Navmesh;

public class AsyncMoveRequest : IDisposable
{
    private NavmeshManager _manager;
    private FollowPath _follow;
    private Task<List<Vector3>>? _pendingTask;
    private bool _pendingFly;

    public bool TaskInProgress => _pendingTask != null;

    public AsyncMoveRequest(NavmeshManager manager, FollowPath follow)
    {
        _manager = manager;
        _follow = follow;
    }

    public void Dispose()
    {
        if (_pendingTask != null)
        {
            if (!_pendingTask.IsCompleted)
                _pendingTask.Wait();
            _pendingTask.Dispose();
            _pendingTask = null;
        }
    }

    public void Update()
    {
        if (_pendingTask != null && _pendingTask.IsCompleted)
        {
            Service.Log.Information($"Pathfinding complete");
            try
            {
                _follow.Move(_pendingTask.Result, _pendingFly);
            }
            catch (Exception ex)
            {
                Service.Log.Error($"Failed to find path: {ex}");
            }
            _pendingTask.Dispose();
            _pendingTask = null;
        }
    }

    public bool MoveTo(Vector3 dest, bool fly)
    {
        if (_pendingTask != null)
        {
            Service.Log.Error($"Pathfinding task is in progress...");
            return false;
        }

        Service.Log.Info($"Queueing {(fly ? "fly" : "move")}-to {dest:f3}");
        _pendingTask = _manager.QueryPath(Service.ClientState.LocalPlayer?.Position ?? default, dest, fly);
        _pendingFly = fly;
        return true;
    }
}
