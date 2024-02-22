using Dalamud.Common;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Navmesh.Movement;
using System;
using System.IO;
using System.Numerics;
using System.Reflection;

namespace Navmesh;

public sealed class Plugin : IDalamudPlugin
{
    private WindowSystem WindowSystem = new("vnavmesh");
    private NavmeshManager _navmeshManager;
    private FollowPath _followPath;
    private MainWindow _wndMain;

    public Plugin(DalamudPluginInterface dalamud)
    {
        if (!dalamud.ConfigDirectory.Exists)
            dalamud.ConfigDirectory.Create();
        var dalamudRoot = dalamud.GetType().Assembly.
                GetType("Dalamud.Service`1", true)!.MakeGenericType(dalamud.GetType().Assembly.GetType("Dalamud.Dalamud", true)!).
                GetMethod("Get")!.Invoke(null, BindingFlags.Default, null, Array.Empty<object>(), null);
        var dalamudStartInfo = (DalamudStartInfo)dalamudRoot?.GetType().GetProperty("StartInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dalamudRoot)!;
        FFXIVClientStructs.Interop.Resolver.GetInstance.SetupSearchSpace(0, new(Path.Combine(dalamud.ConfigDirectory.FullName, $"{dalamudStartInfo.GameVersion}_cs.json")));
        FFXIVClientStructs.Interop.Resolver.GetInstance.Resolve();

        dalamud.Create<Service>();

        _navmeshManager = new(new($"{dalamud.ConfigDirectory.FullName}/meshcache"));
        _followPath = new(_navmeshManager);

        _wndMain = new(_navmeshManager, _followPath);
        WindowSystem.AddWindow(_wndMain);

        dalamud.UiBuilder.Draw += WindowSystem.Draw;
        dalamud.UiBuilder.OpenConfigUi += () => _wndMain.IsOpen = true;
        Service.CommandManager.AddHandler("/vnavmesh", new(OnCommand)
        {
            HelpMessage = "Opens the main menu\n" +
                "/vnavmesh moveto <X> <Y> <Z> → move to raw coordinates\n" +
                "/vnavmesh movedir <X> <Y> <Z> → move this many units over (relative to player facing)\n" +
                "/vnavmesh movetarget → move to target's position\n" +
                "/vnavmesh flyto <X> <Y> <Z> → fly to raw coordinates\n" +
                "/vnavmesh flydir <X> <Y> <Z> → fly this many units over (relative to player facing)\n" +
                "/vnavmesh flytarget → fly to target's position\n" +
                "/vnavmesh stop → stop all movement\n" +
                "/vnavmesh reload → reload current territory's navmesh from cache.\n" +
                "/vnavmesh rebuild → rebuild current territory's navmesh from scratch.",
            ShowInHelp = true,
        });

        _wndMain.IsOpen = true;

        Service.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Service.Framework.Update -= OnUpdate;

        Service.CommandManager.RemoveHandler("/vnavmesh");
        WindowSystem.RemoveAllWindows();
        _wndMain.Dispose();

        _followPath.Dispose();
        _navmeshManager.Dispose();
    }

    private void OnUpdate(IFramework fwk)
    {
        _navmeshManager.Update();
        _followPath.Update();
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
            case "reload":
                _navmeshManager.Reload(true);
                break;
            case "rebuild":
                _navmeshManager.Reload(false);
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
                    _followPath.MoveTo(moveTarget.Position);
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
                    _followPath.FlyTo(flyTarget.Position);
                break;
            case "stop":
                _followPath.Stop();
                break;
        }
    }

    private void MoveToCommand(string[] args, bool relativeToPlayer, bool fly)
    {
        var originActor = relativeToPlayer ? Service.ClientState.LocalPlayer : null;
        var origin = originActor?.Position ?? new();
        var offset = new Vector3(
            float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
        if (fly)
            _followPath.FlyTo(origin + offset);
        else
            _followPath.MoveTo(origin + offset);
    }
}
