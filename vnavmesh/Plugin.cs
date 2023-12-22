using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace Navmesh;

public sealed class Plugin : IDalamudPlugin
{
    private WindowSystem WindowSystem = new("vnavmesh");
    private MainWindow _wndMain;

    public Plugin(DalamudPluginInterface dalamud)
    {
        //var dir = dalamud.ConfigDirectory;
        //if (!dir.Exists)
        //    dir.Create();
        //var dalamudRoot = dalamud.GetType().Assembly.
        //        GetType("Dalamud.Service`1", true)!.MakeGenericType(dalamud.GetType().Assembly.GetType("Dalamud.Dalamud", true)!).
        //        GetMethod("Get")!.Invoke(null, BindingFlags.Default, null, Array.Empty<object>(), null);
        //var dalamudStartInfo = dalamudRoot.GetFoP<DalamudStartInfo>("StartInfo");
        //FFXIVClientStructs.Interop.Resolver.GetInstance.SetupSearchSpace(0, new(Path.Combine(dalamud.ConfigDirectory.FullName, $"{dalamudStartInfo.GameVersion}_cs.json")));
        //FFXIVClientStructs.Interop.Resolver.GetInstance.Resolve();

        dalamud.Create<Service>();

        _wndMain = new();
        WindowSystem.AddWindow(_wndMain);

        dalamud.UiBuilder.Draw += WindowSystem.Draw;
        dalamud.UiBuilder.OpenConfigUi += () => _wndMain.IsOpen = true;
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();
        _wndMain.Dispose();
    }
}
