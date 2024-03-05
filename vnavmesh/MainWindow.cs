using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Navmesh.Debug;
using Navmesh.Movement;
using System;

namespace Navmesh;

public class MainWindow : Window, IDisposable
{
    private DebugDrawer _dd = new();
    private DebugGameCollision _debugGameColl;
    private DebugNavmeshManager _debugNavmeshManager;
    private DebugNavmeshCustom _debugNavmeshCustom;
    private DebugLayout _debugLayout;

    public MainWindow(NavmeshManager manager, FollowPath path, AsyncMoveRequest move, DTRProvider dtr) : base("Navmesh")
    {
        _debugGameColl = new(_dd);
        _debugNavmeshManager = new(_dd, _debugGameColl, manager, path, move, dtr);
        _debugNavmeshCustom = new(_dd, _debugGameColl);
        _debugLayout = new(_debugGameColl);
    }

    public void Dispose()
    {
        _debugLayout.Dispose();
        _debugNavmeshCustom.Dispose();
        _debugNavmeshManager.Dispose();
        _debugGameColl.Dispose();
        _dd.Dispose();
    }

    public override void Draw()
    {
        _dd.StartFrame();
        using (var tabs = ImRaii.TabBar("Tabs"))
        {
            if (tabs)
            {
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
        _dd.EndFrame();
    }
}
