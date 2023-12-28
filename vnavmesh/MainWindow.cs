using Dalamud.Interface.Windowing;
using System;

namespace Navmesh;

public class MainWindow : Window, IDisposable
{
    private DebugGeometry _debugGeom = new();
    private DebugCollisionData _debugColl;

    public MainWindow() : base("Navmesh")
    {
        _debugColl = new(_debugGeom);
    }

    public void Dispose()
    {
        _debugGeom.Dispose();
    }

    public override void Draw()
    {
        _debugGeom.StartFrame();
        _debugColl.Draw();
        _debugGeom.EndFrame();
    }
}
