using Dalamud.Common;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace Navmesh;

public sealed class Plugin : IDalamudPlugin
{
    private WindowSystem WindowSystem = new("vnavmesh");
    private MainWindow _wndMain;

    public Plugin(DalamudPluginInterface dalamud)
    {
        var dir = dalamud.ConfigDirectory;
        if (!dir.Exists)
            dir.Create();
        var dalamudRoot = dalamud.GetType().Assembly.
                GetType("Dalamud.Service`1", true)!.MakeGenericType(dalamud.GetType().Assembly.GetType("Dalamud.Dalamud", true)!).
                GetMethod("Get")!.Invoke(null, BindingFlags.Default, null, Array.Empty<object>(), null);
        var dalamudStartInfo = (DalamudStartInfo)dalamudRoot?.GetType().GetProperty("StartInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dalamudRoot)!;
        FFXIVClientStructs.Interop.Resolver.GetInstance.SetupSearchSpace(0, new(Path.Combine(dalamud.ConfigDirectory.FullName, $"{dalamudStartInfo.GameVersion}_cs.json")));
        FFXIVClientStructs.Interop.Resolver.GetInstance.Resolve();

        dalamud.Create<Service>();

        _wndMain = new();
        WindowSystem.AddWindow(_wndMain);

        dalamud.UiBuilder.Draw += WindowSystem.Draw;
        dalamud.UiBuilder.OpenConfigUi += () => _wndMain.IsOpen = true;
        Service.CommandManager.AddHandler("/vnavmesh", new(OnCommand));

        _wndMain.IsOpen = true;
    }

    public void Dispose()
    {
        Service.CommandManager.RemoveHandler("/vnavmesh");
        WindowSystem.RemoveAllWindows();
        _wndMain.Dispose();
    }

    private void OnCommand(string command, string arguments)
    {
        Service.Log.Debug($"cmd: '{command}', args: '{arguments}'");
        if (arguments.Length == 0)
        {
            _wndMain.IsOpen ^= true;
            return;
        }

        var args = arguments.Split(' ');
        switch (args[0])
        {
            case "rebuild":
                _wndMain.Path.RebuildNavmesh();
                break;
            case "moveto":
                if (args.Length > 3)
                    MoveToCommand(args, false, false);
                break;
            case "movedir":
                if (args.Length > 3)
                    MoveToCommand(args, true, false);
                break;
            case "movetarget":
                var moveTarget = Service.TargetManager.Target;
                if (moveTarget != null)
                    _wndMain.Path.MoveTo(moveTarget.Position);
                break;
            case "flyto":
                if (args.Length > 3)
                    MoveToCommand(args, false, true);
                break;
            case "flydir":
                if (args.Length > 3)
                    MoveToCommand(args, true, true);
                break;
            case "flytarget":
                var flyTarget = Service.TargetManager.Target;
                if (flyTarget != null)
                    _wndMain.Path.FlyTo(flyTarget.Position);
                break;
            case "stop":
                _wndMain.Path.Stop();
                break;
        }
    }

    private void MoveToCommand(string[] args, bool relativeToPlayer, bool fly)
    {
        var originActor = relativeToPlayer ? Service.ClientState.LocalPlayer : null;
        var origin = originActor?.Position ?? new();
        var offset = new Vector3(float.Parse(args[1]), float.Parse(args[2]), float.Parse(args[3]));
        if (fly)
            _wndMain.Path.FlyTo(origin + offset);
        else
            _wndMain.Path.MoveTo(origin + offset);
    }
}
