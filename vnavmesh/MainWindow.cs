using Dalamud.Interface.Windowing;
using ImGuiNET;
using System;

namespace Navmesh;

public class MainWindow : Window, IDisposable
{
    public MainWindow() : base("Navmesh") { }

    public void Dispose()
    {

    }

    public override void Draw()
    {
        ImGui.TextUnformatted("Hello!");
    }
}
