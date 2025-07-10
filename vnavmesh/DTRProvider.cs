using Dalamud.Game.Gui.Dtr;
using Navmesh.Movement;
using System;

namespace Navmesh;

public class DTRProvider : IDisposable
{
    private NavmeshManager _manager;
    private AsyncMoveRequest _asyncMove;
    private FollowPath _followPath;
    private IDtrBarEntry _dtrBarEntry;

    public DTRProvider(NavmeshManager manager, AsyncMoveRequest asyncMove, FollowPath followPath)
    {
        _manager = manager;
        _asyncMove = asyncMove;
        _followPath = followPath;
        _dtrBarEntry = Service.DtrBar.Get("vnavmesh");
    }

    public void Dispose()
    {
        _dtrBarEntry.Remove();
    }

    public void Update()
    {
        _dtrBarEntry.Shown = Service.Config.EnableDTR;
        if (_dtrBarEntry.Shown)
        {
            var loadProgress = _manager.LoadTaskProgress;
            var meshStatus = loadProgress >= 0 ? $"{loadProgress * 100:f0}%" : _manager.Navmesh != null ? "Ready" : "Not Ready";
            
            var statusText = "Mesh: " + meshStatus;
            
            if (Service.Config.ShowQueryStatusInDTR)
            {
                var pathfindInProgress = _manager.PathfindInProgress;
                var numQueued = _manager.NumQueuedPathfindRequests;
                var asyncMoveActive = _asyncMove.TaskInProgress;
                var isMoving = _followPath.Waypoints.Count > 0;
                
                // Show query status when there's activity
                if (pathfindInProgress || numQueued > 0)
                {
                    var activeCount = pathfindInProgress ? 1 : 0;
                    statusText += $" | Queries: {activeCount}";
                    if (numQueued > 0)
                        statusText += $" (+{numQueued} queued)";
                }
                
                // Show current operations
                if (asyncMoveActive)
                    statusText += " | Pathfinding";
                if (isMoving)
                    statusText += " | Moving";
            }
            else
            {
                // Fallback to original simple status for backward compatibility
                if (_asyncMove.TaskInProgress || _followPath.Waypoints.Count > 0)
                    statusText = "Mesh: Pathfinding";
            }
            
            _dtrBarEntry.Text = statusText;
        }
    }
}
