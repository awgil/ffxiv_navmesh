using System.Numerics;

namespace Navmesh
{
    internal static class IPC
    {
        internal static void Init()
        {
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.Stop").RegisterAction(Stop);
            Service.PluginInterface.GetIpcProvider<float, float, float, object>("vnavmesh.MoveTo").RegisterAction(MoveTo);
            Service.PluginInterface.GetIpcProvider<float, float, float, object>("vnavmesh.FlyTo").RegisterAction(FlyTo);
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.IsNavmeshReady").RegisterFunc(CheckNavmesh);
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.IsPathRunning").RegisterFunc(CheckPathRunning);
        }

        private static void FlyTo(float x, float y, float z)
        {
            var newPos = new Vector3(x, y, z);
            Plugin.P._followPath.FlyTo(newPos);
        }

        private static void MoveTo(float x, float y, float z)
        {
            var newPos = new Vector3(x, y, z);
            Plugin.P._followPath.MoveTo(newPos);
        }

        private static bool CheckPathRunning()
        {
            return Plugin.P._followPath.Waypoints.Count > 0;
        }

        private static bool CheckNavmesh()
        {
            return Plugin.P._navmeshManager.Navmesh != null;
        }

        internal static void Dispose()
        {
            Service.PluginInterface.GetIpcProvider<object>("vnavmesh.Stop").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<float, float, float, object>("vnavmesh.MoveTo").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<float, float, float, object>("vnavmesh.FlyTo").UnregisterAction();
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.IsNavmeshReady").UnregisterFunc();
            Service.PluginInterface.GetIpcProvider<bool>("vnavmesh.IsPathRunning").UnregisterFunc();
        }

        private static void Stop()
        {
            Plugin.P._followPath.Stop();
        }

    }
}
