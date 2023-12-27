using Dalamud.Interface.Windowing;
using System;

namespace Navmesh;

public class MainWindow : Window, IDisposable
{
    private DebugCollisionData _debugColl = new();

    public MainWindow() : base("Navmesh") { }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        _debugColl.Draw();
    }
}
