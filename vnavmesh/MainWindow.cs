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
    private DebugDrawer _debugGeom = new();
    private DebugGameCollision _debugColl;
    private DebugNavmesh _debugNavmesh;

    public FollowPath Path => _path; // TODO: reconsider

    public MainWindow() : base("Navmesh")
    {
        _path = new(_navmesh);
        _debugColl = new(_debugGeom);
        _debugNavmesh = new(_debugGeom, _navmesh);
    }

    public void Dispose()
    {
        _navmesh.Dispose();
        _path.Dispose();
        _debugNavmesh.Dispose();
        _debugGeom.Dispose();
    }

    public override void PreOpenCheck()
    {
        _path.Update();
    }

    public override void Draw()
    {
        _debugGeom.StartFrame();
        _path.DrawPath(_debugGeom);
        using (var tabs = ImRaii.TabBar("Tabs"))
        {
            if (tabs)
            {
                using (var tab = ImRaii.TabItem("Collision"))
                    if (tab)
                        _debugColl.Draw();
                using (var tab = ImRaii.TabItem("Navmesh"))
                    if (tab)
                        _debugNavmesh.Draw();
            }
        }
        _debugGeom.EndFrame();
    }
}
