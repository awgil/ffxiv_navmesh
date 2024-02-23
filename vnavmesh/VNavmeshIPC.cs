using Navmesh.Movement;
using System.Numerics;

namespace Navmesh
{
    internal static class VNavmeshIPC
    {
        private static NavmeshManager _navmeshManager;
        private static FollowPath _followPath;
        private static MainWindow _mainWindow;

        internal static void Initialize(NavmeshManager navmeshManager, FollowPath followPath, MainWindow mainWindow)
        {
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.NavmeshIsNull").RegisterFunc(NavmeshIsNull);
            Service.PluginInterface.GetIpcProvider<float>("vnavmesh.TaskProgress").RegisterFunc(TaskProgress);
            Service.PluginInterface.GetIpcProvider<int>("vnavmesh.WaypointsCount").RegisterFunc(WaypointsCount);
            Service.PluginInterface.GetIpcProvider<Vector3, bool>("vnavmesh.MoveTo").RegisterFunc(MoveTo);
            Service.PluginInterface.GetIpcProvider<Vector3, bool>("vnavmesh.MoveDir").RegisterFunc(MoveDir);
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.MoveTarget").RegisterFunc(MoveTarget);
            Service.PluginInterface.GetIpcProvider<Vector3, bool>("vnavmesh.FlyTo").RegisterFunc(FlyTo);
            Service.PluginInterface.GetIpcProvider<Vector3, bool>("vnavmesh.FlyDir").RegisterFunc(FlyDir);
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.FlyTarget").RegisterFunc(FlyTarget);
            Service.PluginInterface.GetIpcProvider<bool, bool>("vnavmesh.MovementAllowed").RegisterFunc(MovementAllowed);
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.Stop").RegisterFunc(Stop);
            Service.PluginInterface.GetIpcProvider<bool, bool>("vnavmesh.AutoMesh").RegisterFunc(AutoMesh);
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.Reload").RegisterFunc(Reload);
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.Rebuild").RegisterFunc(Rebuild);
            Service.PluginInterface.GetIpcProvider<bool, bool>("vnavmesh.MainWindowIsOpen").RegisterFunc(MainWindowIsOpen);

            _navmeshManager = navmeshManager;
            _followPath = followPath;
            _mainWindow = mainWindow;
        }

        internal static void Dispose()
        {
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.NavmeshIsNull").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<float>("vnavmesh.TaskProgress").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<int>("vnavmesh.WaypointsCount").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<Vector3, bool>("vnavmesh.MoveTo").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<Vector3, bool>("vnavmesh.MoveDir").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.MoveTarget").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<Vector3, bool>("vnavmesh.FlyTo").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<Vector3, bool>("vnavmesh.FlyDir").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.FlyTarget").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<bool, bool>("vnavmesh.MovementAllowed").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.Stop").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<bool, bool>("vnavmesh.AutoMesh").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.Reload").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.Rebuild").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.MainWindowIsOpen").UnregisterFunc();
        }

        private static bool NavmeshIsNull() => _navmeshManager.Navmesh is null;

        private static float TaskProgress() => _navmeshManager.TaskProgress;

        private static int WaypointsCount() => _followPath.Waypoints.Count;

        private static bool MoveTo(Vector3 position)
        {
            if (_followPath.Waypoints.Count == 0)
            {
                MoveToCommand(position, false, false);
                return true;
            }
            else
                return false;
        }

        private static bool MoveDir(Vector3 position)
        {
            if (_followPath.Waypoints.Count == 0)
            {
                MoveToCommand(position, true, false);
                return true;
            }
            else
                return false;
        }

        private static bool MoveTarget()
        {
            if (_followPath.Waypoints.Count == 0)
            {
                var moveTarget = Service.TargetManager.Target;
                if (moveTarget != null)
                    _followPath.MoveTo(moveTarget.Position);
                return true;
            }
            else
                return false;
        }
        
        private static bool FlyTo(Vector3 position)
        {
            if (_followPath.Waypoints.Count == 0)
            {
                MoveToCommand(position, false, true);
                return true;
            }
            else
                return false;
        }

        private static bool FlyDir(Vector3 position)
        {
            if (_followPath.Waypoints.Count == 0)
            {
                MoveToCommand(position, true, true);
                return true;
            }
            else
                return false;
        }

        private static bool FlyTarget()
        {
            if (_followPath.Waypoints.Count == 0)
            {
                var moveTarget = Service.TargetManager.Target;
                if (moveTarget != null)
                    _followPath.FlyTo(moveTarget.Position);
                return true;
            }
            else
                return false;
        }

        private static bool MovementAllowed(bool allowed)
        {
            _followPath.MovementAllowed = allowed;
            return true;
        }

        private static bool Stop()
        {
            if (_followPath.Waypoints.Count > 0)
            {
                _followPath.Stop();
                return true;
            }
            else
                return false;
        }

        private static bool AutoMesh(bool autoMesh)
        {
            _navmeshManager.AutoMesh = autoMesh;
            return true;
        }

        private static bool Reload()
        {
            _navmeshManager.Reload(true);
            return true;
        }

        private static bool Rebuild()
        {
            _navmeshManager.Reload(false);
            return true;
        }

        private static bool MainWindowIsOpen(bool isOpen)
        {
            _mainWindow.IsOpen = isOpen;
            return true;
        }

        private static void MoveToCommand(Vector3 offset, bool relativeToPlayer, bool fly)
        {
            var originActor = relativeToPlayer ? Service.ClientState.LocalPlayer : null;
            var origin = originActor?.Position ?? new();

            if (fly)
                _followPath.FlyTo(origin + offset);
            else
                _followPath.MoveTo(origin + offset);
        }
    }
}
