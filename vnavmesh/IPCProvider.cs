using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh
{
    class IPCProvider : IDisposable
    {
        private List<Action> _disposeActions = new();

        public IPCProvider(NavmeshManager navmeshManager, FollowPath followPath, MainWindow mainWindow)
        {
            Register("Nav.IsReady", () => navmeshManager.Navmesh != null);
            Register("Nav.BuildProgress", () => navmeshManager.TaskProgress);
            Register("Nav.Reload", () => navmeshManager.Reload(true));
            Register("Nav.Rebuild", () => navmeshManager.Reload(false));
            Register("Nav.IsAutoLoad", () => navmeshManager.AutoLoad);
            Register<bool>("Nav.SetAutoLoad", v => navmeshManager.AutoLoad = v);

            Register<Vector3>("Path.MoveTo", followPath.MoveTo);
            Register<Vector3>("Path.FlyTo", followPath.FlyTo);
            Register("Path.Stop", followPath.Stop);
            Register("Path.IsRunning", () => followPath.Waypoints.Count > 0);
            Register("Path.NumWaypoints", () => followPath.Waypoints.Count);
            Register("Path.GetMovementAllowed", () => followPath.MovementAllowed);
            Register<bool>("Path.SetMovementAllowed", v => followPath.MovementAllowed = v);
            Register("Path.GetTolerance", () => followPath.Tolerance);
            Register<float>("Path.SetTolerance", v => followPath.Tolerance = v);

            Register("Window.IsOpen", () => mainWindow.IsOpen);
            Register<bool>("Window.SetOpen", v => mainWindow.IsOpen = v);
        }

        public void Dispose()
        {
            foreach (var a in _disposeActions)
                a();
        }

        private void Register<TRet>(string name, Func<TRet> func)
        {
            var p = Service.PluginInterface.GetIpcProvider<TRet>("vnavmesh." + name);
            p.RegisterFunc(func);
            _disposeActions.Add(p.UnregisterFunc);
        }

        private void Register(string name, Action func)
        {
            var p = Service.PluginInterface.GetIpcProvider<object>("vnavmesh." + name);
            p.RegisterAction(func);
            _disposeActions.Add(p.UnregisterAction);
        }

        private void Register<T1>(string name, Action<T1> func)
        {
            var p = Service.PluginInterface.GetIpcProvider<T1, object>("vnavmesh." + name);
            p.RegisterAction(func);
            _disposeActions.Add(p.UnregisterAction);
        }
    }
}
