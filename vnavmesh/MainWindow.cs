using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Navmesh.Debug;
using Navmesh.Movement;
using System;

namespace Navmesh;

public class MainWindow : Window, IDisposable
{
    private NavmeshBuilder _navmesh = new();
    private FollowPath _path;
    private DebugDrawer _dd = new();
    private DebugGameCollision _debugGameColl;
    private DebugNavmesh _debugNavmesh;

    public FollowPath Path => _path; // TODO: reconsider

    public MainWindow() : base("Navmesh")
    {
        _path = new(_navmesh);
        _debugGameColl = new(_dd);
        _debugNavmesh = new(_dd, _navmesh);
    }

    public void Dispose()
    {
        _navmesh.Dispose();
        _path.Dispose();
        _debugNavmesh.Dispose();
        _debugGameColl.Dispose();
        _dd.Dispose();
    }

    public override void PreOpenCheck()
    {
        _path.Update();
    }

    public override void Draw()
    {
        _dd.StartFrame();
        _path.DrawPath(_dd);
        using (var tabs = ImRaii.TabBar("Tabs"))
        {
            if (tabs)
            {
                using (var tab = ImRaii.TabItem("Collision"))
                    if (tab)
                        _debugGameColl.Draw();
                using (var tab = ImRaii.TabItem("Navmesh"))
                    if (tab)
                        _debugNavmesh.Draw();
            }
        }
        _dd.EndFrame();
    }
}
