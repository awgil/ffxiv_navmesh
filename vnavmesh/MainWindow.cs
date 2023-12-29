using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using System;

namespace Navmesh;

public class MainWindow : Window, IDisposable
{
    private DebugGeometry _debugGeom = new();
    private DebugCollisionData _debugColl;
    private DebugNavmesh _debugNavmesh;

    public MainWindow() : base("Navmesh")
    {
        _debugColl = new(_debugGeom);
        _debugNavmesh = new(_debugGeom);
    }

    public void Dispose()
    {
        _debugGeom.Dispose();
        _debugNavmesh.Dispose();
    }

    public override void Draw()
    {
        _debugGeom.StartFrame();
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
