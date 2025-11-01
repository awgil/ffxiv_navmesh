using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Navmesh.Debug;
using Navmesh.Movement;
using System;
using System.Collections.Generic;

namespace Navmesh;

public class MainWindow : Window, IDisposable
{
    private FollowPath _path;
    private DebugDrawer _dd = new();
    private DebugGameCollision _debugGameColl;
    private DebugNavmeshManager _debugNavmeshManager;
    private DebugNavmeshCustom _debugNavmeshCustom;
    private DebugLayout _debugLayout;
    private SceneTracker _scene;
    private string _configDirectory;

    public MainWindow(NavmeshManager manager, FollowPath path, AsyncMoveRequest move, DTRProvider dtr, string configDir) : base("Navmesh")
    {
        _scene = new();
        _scene.TileChanged += OnTileChanged;
        _scene.OnPluginInit();
        _path = path;
        _configDirectory = configDir;
        _debugGameColl = new(_dd);
        _debugNavmeshManager = new(_dd, _debugGameColl, manager, path, move, dtr);
        _debugNavmeshCustom = new(_dd, _debugGameColl, manager, _scene, _configDirectory);
        _debugLayout = new(_dd, _debugGameColl);
    }

    private void OnTileChanged(List<SceneTracker.ChangedTile> tiles)
    {
        foreach (var tile in tiles)
            Service.Log.Debug($"tile {tile.X}x{tile.Z} hash key: {SceneTracker.HashKeys(tile.SortedIds)}");
    }

    public void Dispose()
    {
        _scene.Dispose();
        _debugLayout.Dispose();
        _debugNavmeshCustom.Dispose();
        _debugNavmeshManager.Dispose();
        _debugGameColl.Dispose();
        _dd.Dispose();
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
            }
        }
    }
}
