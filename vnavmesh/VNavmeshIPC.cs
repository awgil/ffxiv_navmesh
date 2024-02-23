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
            Service.PluginInterface.GetIpcProvider<Vector3, object>("vnavmesh.MoveTo").RegisterAction(MoveTo);
            Service.PluginInterface.GetIpcProvider<Vector3, object>("vnavmesh.MoveDir").RegisterAction(MoveDir);
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.MoveTarget").RegisterAction(MoveTarget);
            Service.PluginInterface.GetIpcProvider<Vector3, object>("vnavmesh.FlyTo").RegisterAction(FlyTo);
            Service.PluginInterface.GetIpcProvider<Vector3, object>("vnavmesh.FlyDir").RegisterAction(FlyDir);
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.FlyTarget").RegisterAction(FlyTarget);
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.MovementAllowed").RegisterFunc(MovementAllowed);
            Service.PluginInterface.GetIpcProvider<bool, object>("vnavmesh.SetMovementAllowed").RegisterAction(SetMovementAllowed);
            Service.PluginInterface.GetIpcProvider<float>("vnavmesh.Tolerance").RegisterFunc(Tolerance);
            Service.PluginInterface.GetIpcProvider<float, object>("vnavmesh.SetTolerance").RegisterAction(SetTolerance);
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.Stop").RegisterAction(Stop);
            Service.PluginInterface.GetIpcProvider<bool, object>("vnavmesh.AutoMesh").RegisterAction(AutoMesh);
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.Reload").RegisterAction(Reload);
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.Rebuild").RegisterAction(Rebuild);
            Service.PluginInterface.GetIpcProvider<bool, object>("vnavmesh.MainWindowIsOpen").RegisterAction(MainWindowIsOpen);

            _navmeshManager = navmeshManager;
            _followPath = followPath;
            _mainWindow = mainWindow;
        }

        internal static void Dispose()
        {
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.NavmeshIsNull").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<float>("vnavmesh.TaskProgress").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<int>("vnavmesh.WaypointsCount").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<Vector3, object>("vnavmesh.MoveTo").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<Vector3, object>("vnavmesh.MoveDir").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.MoveTarget").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<Vector3, object>("vnavmesh.FlyTo").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<Vector3, object>("vnavmesh.FlyDir").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.FlyTarget").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.IsMovementAllowed").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<bool, object>("vnavmesh.SetMovementAllowed").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<float>("vnavmesh.Tolerance").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<float, object>("vnavmesh.SetTolerance").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.Stop").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<bool, object>("vnavmesh.AutoMesh").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.Reload").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.Rebuild").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<bool, object>("vnavmesh.MainWindowIsOpen").UnregisterAction();
        }

        private static bool NavmeshIsNull() => _navmeshManager.Navmesh is null;

        private static float TaskProgress() => _navmeshManager.TaskProgress;

        private static int WaypointsCount() => _followPath.Waypoints.Count;

        private static void MoveTo(Vector3 position)
        {
            if (_followPath.Waypoints.Count == 0)
                MoveToCommand(position, false, false);

        }

        private static void MoveDir(Vector3 position)
        {
            if (_followPath.Waypoints.Count == 0)
                MoveToCommand(position, true, false);
        }

        private static void MoveTarget()
        {
            if (_followPath.Waypoints.Count == 0)
            {
                var moveTarget = Service.TargetManager.Target;
                if (moveTarget != null)
                    _followPath.MoveTo(moveTarget.Position);
            }
        }
        
        private static void FlyTo(Vector3 position)
        {
            if (_followPath.Waypoints.Count == 0)
                MoveToCommand(position, false, true);
        }

        private static void FlyDir(Vector3 position)
        {
            if (_followPath.Waypoints.Count == 0)
                MoveToCommand(position, true, true);
        }

        private static void FlyTarget()
        {
            if (_followPath.Waypoints.Count == 0)
            {
                var moveTarget = Service.TargetManager.Target;
                if (moveTarget != null)
                    _followPath.FlyTo(moveTarget.Position);
            }
        }

        private static void SetMovementAllowed(bool allowed) => _followPath.MovementAllowed = allowed;

        private static bool MovementAllowed() => _followPath.MovementAllowed;

        private static void SetTolerance(float tolerance) => _followPath.Tolerance = tolerance;

        private static float Tolerance() => _followPath.Tolerance;

        private static void Stop()
        {
            if (_followPath.Waypoints.Count > 0)
                _followPath.Stop();
        }

        private static void AutoMesh(bool autoMesh) => _navmeshManager.AutoMesh = autoMesh;
        private static void Reload() => _navmeshManager.Reload(true);
        private static void Rebuild() => _navmeshManager.Reload(false);
        private static void MainWindowIsOpen(bool isOpen) => _mainWindow.IsOpen = isOpen;

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
