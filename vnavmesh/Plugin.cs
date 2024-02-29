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
    private IPCProvider _ipcProvider;

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
        _ipcProvider = new(_navmeshManager, _followPath, _wndMain);

        WindowSystem.AddWindow(_wndMain);
        //_wndMain.IsOpen = true;

        dalamud.UiBuilder.Draw += WindowSystem.Draw;
        dalamud.UiBuilder.OpenConfigUi += () => _wndMain.IsOpen = true;
        Service.CommandManager.AddHandler("/vnavmesh", new(OnCommand)
        {
            HelpMessage = """
            Opens the debug menu.
            /vnavmesh moveto <X> <Y> <Z> → move to raw coordinates
            /vnavmesh movedir <X> <Y> <Z> → move this many units over (relative to player facing)
            /vnavmesh movetarget → move to target's position
            /vnavmesh flyto <X> <Y> <Z> → fly to raw coordinates
            /vnavmesh flydir <X> <Y> <Z> → fly this many units over (relative to player facing)
            /vnavmesh flytarget → fly to target's position
            /vnavmesh stop → stop all movement
            /vnavmesh reload → reload current territory's navmesh from cache
            /vnavmesh rebuild → rebuild current territory's navmesh from scratch
            /vnavmesh aligncamera → toggle aligning camera to movement direction
            """,
            ShowInHelp = true,
        });

        Service.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Service.Framework.Update -= OnUpdate;

        Service.CommandManager.RemoveHandler("/vnavmesh");
        WindowSystem.RemoveAllWindows();

        _ipcProvider.Dispose();
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
            case "aligncamera":
                _followPath.AlignCamera ^= true;
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
