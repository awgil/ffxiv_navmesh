﻿using Navmesh.Movement;
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
    private float _pendingDestRange;

    public bool TaskInProgress => _pendingTask != null;

    public AsyncMoveRequest(NavmeshManager manager, FollowPath follow)
    {
        _manager = manager;
        _follow = follow;
        _follow.OnRepathRequested += HandleRepathRequest;
    }

    public void Dispose()
    {
        _follow.OnRepathRequested -= HandleRepathRequest;
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
                _follow.Move(_pendingTask.Result, !_pendingFly, _pendingDestRange);
            }
            catch (Exception ex)
            {
                Plugin.DuoLog(ex, "Failed to find path");
            }
            _pendingTask.Dispose();
            _pendingTask = null;
        }
    }

    public bool MoveTo(Vector3 dest, bool fly, float range = 0)
    {
        if (_pendingTask != null)
        {
            Service.Log.Error($"Pathfinding task is in progress...");
            return false;
        }

        var toleranceStr = range > 0 ? $" within {range}y" : "";

        Service.Log.Info($"Queueing {(fly ? "fly" : "move")}-to {dest:f3}{toleranceStr}");
        _pendingTask = _manager.QueryPath(Service.ClientState.LocalPlayer?.Position ?? default, dest, fly, range: range);
        _pendingFly = fly;
        _pendingDestRange = range;
        return true;
    }

    private void HandleRepathRequest(Vector3 currentPosition, Vector3 destination, bool wasFlying)
    {
        Service.Log.Information($"Handling re-path request from {currentPosition} to {destination}");
        
        // Cancel any existing pathfinding task
        if (_pendingTask != null)
        {
            if (!_pendingTask.IsCompleted)
                _pendingTask.Wait();
            _pendingTask.Dispose();
            _pendingTask = null;
        }
        
        // Start new pathfinding task
        MoveTo(destination, wasFlying, _pendingDestRange);
    }
}
