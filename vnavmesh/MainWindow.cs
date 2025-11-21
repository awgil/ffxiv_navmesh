using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Navmesh.Debug;
using Navmesh.Movement;
using System;

namespace Navmesh;

public class MainWindow : Window, IDisposable
{
    private FollowPath _path;
    private DebugDrawer _dd = new();
    private DebugGameCollision _debugGameColl;
    private DebugNavmeshManager _debugNavmeshManager;
    private DebugNavmeshCustom _debugNavmeshCustom;
    private DebugLayout _debugLayout;
    private DebugTileManager _debugTiles;
    private DebugFloodFill _debugFF;
    private string _configDirectory;

    public MainWindow(NavmeshManager manager, FollowPath path, AsyncMoveRequest move, DTRProvider dtr, string configDir) : base("Navmesh")
    {
        _path = path;
        _configDirectory = configDir;
        _debugGameColl = new(_dd);
        _debugNavmeshManager = new(_dd, _debugGameColl, manager, path, move, dtr);
        _debugNavmeshCustom = new(_dd, _debugGameColl, manager, _configDirectory);
        _debugLayout = new(_dd, _debugGameColl);
        _debugTiles = new(manager, _dd, _debugGameColl);
        _debugFF = new();
    }

    public void Dispose()
    {
        _debugTiles.Dispose();
        _debugLayout.Dispose();
        _debugNavmeshCustom.Dispose();
        _debugNavmeshManager.Dispose();
        _debugGameColl.Dispose();
        _dd.Dispose();
    }

    public void OnZoneChange()
    {
        _debugGameColl.Saved = null;
    }

    public void StartFrame()
    {
        _dd.StartFrame();
    }

    public void EndFrame()
    {
        _debugGameColl.DrawVisualizers();
        if (Service.Config.ShowWaypoints)
        {
            var player = Service.ClientState.LocalPlayer;
            if (player != null)
            {
                var from = player.Position;
                var color = 0xff00ff00;
                foreach (var to in _path.Waypoints)
                {
                    _dd.DrawWorldLine(from, to, color);
                    _dd.DrawWorldPointFilled(to, 3, 0xff0000ff);
                    from = to;
                    color = 0xff00ffff;
                }
            }
        }
        _dd.EndFrame();
    }

    public override void Draw()
    {
        using (var tabs = ImRaii.TabBar("Tabs"))
        {
            if (tabs)
            {
                using (var tab = ImRaii.TabItem("Config"))
                    if (tab)
                        Service.Config.Draw();
                using (var tab = ImRaii.TabItem("Layout"))
                    if (tab)
                        _debugLayout.Draw();
                using (var tab = ImRaii.TabItem("Collision"))
                    if (tab)
                        _debugGameColl.Draw();
                using (var tab = ImRaii.TabItem("Navmesh manager"))
                    if (tab)
                        _debugNavmeshManager.Draw();
                using (var tab = ImRaii.TabItem("Navmesh custom"))
                    if (tab)
                        _debugNavmeshCustom.Draw();
                using (var tab = ImRaii.TabItem("Tabs"))
                    if (tab)
                        _debugTiles.Draw();
                using (var tab = ImRaii.TabItem("Flood fill"))
                    if (tab)
                        _debugFF.Draw();
            }
        }
    }
}
