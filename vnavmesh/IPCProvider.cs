using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Navmesh;

class IPCProvider : IDisposable
{
    private List<Action> _disposeActions = new();

    public IPCProvider(NavmeshManager navmeshManager, FollowPath followPath, AsyncMoveRequest move, MainWindow mainWindow, DTRProvider dtr)
    {
        RegisterFunc("Nav.IsReady", () => navmeshManager.Navmesh != null);
        RegisterFunc("Nav.BuildProgress", () => navmeshManager.LoadTaskProgress);
        RegisterFunc("Nav.Reload", () => navmeshManager.Reload(true));
        RegisterFunc("Nav.Rebuild", () => navmeshManager.Reload(false));
        RegisterFunc("Nav.Pathfind", (Vector3 from, Vector3 to, bool fly) => navmeshManager.QueryPath(from, to, fly));
        RegisterFunc("Nav.PathfindCancelable", (Vector3 from, Vector3 to, bool fly, CancellationToken cancel) => navmeshManager.QueryPath(from, to, fly, cancel));
        RegisterAction("Nav.PathfindCancelAll", () => navmeshManager.Reload(true));
        RegisterFunc("Nav.PathfindInProgress", () => navmeshManager.PathfindInProgress);
        RegisterFunc("Nav.PathfindNumQueued", () => navmeshManager.NumQueuedPathfindRequests);
        RegisterFunc("Nav.IsAutoLoad", () => Service.Config.AutoLoadNavmesh);
        RegisterAction("Nav.SetAutoLoad", (bool v) => { Service.Config.AutoLoadNavmesh = v; Service.Config.NotifyModified(); });
        RegisterFunc("Nav.BuildBitmap", (Vector3 startingPos, string filename, float pixelSize) => navmeshManager.BuildBitmap(startingPos, filename, pixelSize));
        RegisterFunc("Nav.BuildBitmapBounded", (Vector3 startingPos, string filename, float pixelSize, Vector3 minBounds, Vector3 maxBounds) => navmeshManager.BuildBitmap(startingPos, filename, pixelSize, new AABB { Min = minBounds, Max = maxBounds }));

        RegisterFunc("Query.Mesh.NearestPoint", (Vector3 p, float halfExtentXZ, float halfExtentY) => navmeshManager.Query?.FindNearestPointOnMesh(p, halfExtentXZ, halfExtentY));
        RegisterFunc("Query.Mesh.PointOnFloor", (Vector3 p, bool allowUnlandable, float halfExtentXZ) => navmeshManager.Query?.FindPointOnFloor(p, halfExtentXZ));

        RegisterAction("Path.MoveTo", (List<Vector3> waypoints, bool fly) => followPath.Move(waypoints, !fly));
        RegisterAction("Path.Stop", followPath.Stop);
        RegisterFunc("Path.IsRunning", () => followPath.Waypoints.Count > 0);
        RegisterFunc("Path.NumWaypoints", () => followPath.Waypoints.Count);
        RegisterFunc("Path.ListWaypoints", () => followPath.Waypoints);
        RegisterFunc("Path.GetMovementAllowed", () => followPath.MovementAllowed);
        RegisterAction("Path.SetMovementAllowed", (bool v) => followPath.MovementAllowed = v);
        RegisterFunc("Path.GetAlignCamera", () => Service.Config.AlignCameraToMovement);
        RegisterAction("Path.SetAlignCamera", (bool v) => { Service.Config.AlignCameraToMovement = v; Service.Config.NotifyModified(); });
        RegisterFunc("Path.GetTolerance", () => followPath.Tolerance);
        RegisterAction("Path.SetTolerance", (float v) => followPath.Tolerance = v);

        RegisterFunc("SimpleMove.PathfindAndMoveTo", (Vector3 dest, bool fly) => move.MoveTo(dest, fly));
        RegisterFunc("SimpleMove.PathfindInProgress", () => move.TaskInProgress);

        RegisterFunc("Window.IsOpen", () => mainWindow.IsOpen);
        RegisterAction("Window.SetOpen", (bool v) => mainWindow.IsOpen = v);

        RegisterFunc("DTR.IsShown", () => Service.Config.EnableDTR);
        RegisterAction("DTR.SetShown", (bool v) => { Service.Config.EnableDTR = v; Service.Config.NotifyModified(); });
    }

    public void Dispose()
    {
        foreach (var a in _disposeActions)
            a();
    }

    private void RegisterFunc<TRet>(string name, Func<TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1>(string name, Func<T1, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2>(string name, Func<T1, T2, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2, T3>(string name, Func<T1, T2, T3, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, T3, TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2, T3, T4>(string name, Func<T1, T2, T3, T4, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, T3, T4, TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterFunc<TRet, T1, T2, T3, T4, T5>(string name, Func<T1, T2, T3, T4, T5, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, T3, T4, T5, TRet>("vnavmesh." + name);
        p.RegisterFunc(func);
        _disposeActions.Add(p.UnregisterFunc);
    }

    private void RegisterAction(string name, Action func)
    {
        var p = Service.PluginInterface.GetIpcProvider<object>("vnavmesh." + name);
        p.RegisterAction(func);
        _disposeActions.Add(p.UnregisterAction);
    }

    private void RegisterAction<T1>(string name, Action<T1> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, object>("vnavmesh." + name);
        p.RegisterAction(func);
        _disposeActions.Add(p.UnregisterAction);
    }

    private void RegisterAction<T1, T2>(string name, Action<T1, T2> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, object>("vnavmesh." + name);
        p.RegisterAction(func);
        _disposeActions.Add(p.UnregisterAction);
    }
}
