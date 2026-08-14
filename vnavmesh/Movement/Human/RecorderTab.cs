using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Navmesh.Debug;
using System;
using System.Linq;
using System.Numerics;

namespace Navmesh.Movement.Human;

// UI for authoring routes and running captures; see ADR 0003
public class RecorderTab
{
    private readonly TrajectoryRecorder _recorder;
    private readonly RouteBook _routes;
    private readonly DebugDrawer _dd;

    private Vector3? _pendingA;
    private Vector3? _pendingB;
    private bool _pendingFly;
    private string _pendingNotes = "";
    private Route? _selected;

    public RecorderTab(TrajectoryRecorder recorder, RouteBook routes, DebugDrawer dd)
    {
        _recorder = recorder;
        _routes = routes;
        _dd = dd;
    }

    // build time of the loaded assembly, so it is obvious whether a rebuild has actually been picked up
    private static readonly string _buildStamp = BuildStamp();

    private static string BuildStamp()
    {
        try
        {
            // Assembly.Location is empty here: dalamud loads plugins from bytes rather than from the
            // file, so the path has to come from dalamud itself. re-stat rather than reading the
            // FileInfo it hands over, which cached its timestamp when the plugin was discovered.
            var dll = new System.IO.FileInfo(Service.PluginInterface.AssemblyLocation.FullName);
            return dll.Exists ? dll.LastWriteTime.ToString("HH:mm:ss") : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    public void Draw()
    {
        var territory = Service.ClientState.TerritoryType;
        var player = Service.ObjectTable.LocalPlayer;

        ImGui.TextDisabled($"loaded build: {_buildStamp}");
        ImGui.Separator();

        var batch = _recorder.BatchRemaining > 0 ? $"  |  {_recorder.BatchRemaining} more queued" : "";
        ImGui.TextUnformatted($"State: {_recorder.CurrentState}  |  {_recorder.Status}{batch}");
        if (_recorder.CurrentState == TrajectoryRecorder.State.Recording)
            ImGui.TextUnformatted($"Samples: {_recorder.SampleCount}");
        if (_recorder.CurrentState != TrajectoryRecorder.State.Idle && ImGui.Button("Cancel capture"))
            _recorder.Cancel("user request");

        ImGui.Separator();

        DrawSteering();

        ImGui.Separator();

        DrawAuthoring(territory, player?.Position);

        ImGui.Separator();

        DrawRouteList(territory);

        ImGui.Separator();
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("Runs per click", ref Service.Config.Humanizer.RecorderRepeats, 1, 10))
            Service.Config.NotifyModified();
        ImGui.TextDisabled($"Captures: {_recorder.CaptureDir}");
        if (ImGui.Checkbox("Draw active route in world", ref Service.Config.Humanizer.RecorderDrawRoute))
            Service.Config.NotifyModified();
    }

    private void DrawSteering()
    {
        var cfg = Service.Config.Humanizer;

        // off is arm 2 of the evaluation, on is arm 3, so this switch is the whole experiment
        if (ImGui.Checkbox("Humanized steering", ref cfg.SteeringEnabled))
            Service.Config.NotifyModified();
        ImGui.SameLine();
        ImGui.TextDisabled(cfg.SteeringEnabled ? "(arm 3)" : "(arm 2, upstream behaviour)");

        using var _ = ImRaii.Disabled(!cfg.SteeringEnabled);
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Lookahead (yalms)", ref cfg.SteeringLookahead, 1f, 40f, "%.1f"))
            Service.Config.NotifyModified();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far along the path to aim. Larger smooths corners more but cuts them harder.");

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Max turn rate (deg/s)", ref cfg.SteeringMaxTurnRate, 10f, 400f, "%.0f"))
            Service.Config.NotifyModified();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ceiling on commanded heading change. Measured humans turn at 22-42 through corners; the character itself tops out near 400.");

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Wall clearance (yalms)", ref cfg.SteeringWallClearance, 0f, 10f, "%.1f"))
            Service.Config.NotifyModified();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far out obstacles start being given way to. 0 disables it, and the character only reacts once the way is blocked.");

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Wall avoidance strength", ref cfg.SteeringWallAvoidance, 0f, 1.5f, "%.2f"))
            Service.Config.NotifyModified();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How hard the drift pulls. Too high and it hugs open ground and will not enter doorways.");

        if (ImGui.Checkbox("Clamp aim point to the mesh", ref cfg.SteeringClampToMesh))
            Service.Config.NotifyModified();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stops the aim point sitting through a wall when a corner is cut, which is what makes the character stick on obstacle heavy routes.");

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Recovery turn rate (deg/s)", ref cfg.SteeringRecoveryTurnRate, 60f, 400f, "%.0f"))
            Service.Config.NotifyModified();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Used for large corrections and the last stretch of a path, where the normal ceiling would make the character orbit its target.");
    }

    private void DrawAuthoring(uint territory, Vector3? pos)
    {
        if (!ImGui.CollapsingHeader("New route"))
            return;

        using var _ = ImRaii.PushIndent();

        if (pos is not { } here)
        {
            ImGui.TextDisabled("no player");
            return;
        }

        if (ImGui.Button("Set A here"))
            _pendingA = here;
        ImGui.SameLine();
        ImGui.TextUnformatted(_pendingA is { } a ? $"A = {a:f2}" : "A = unset");

        if (ImGui.Button("Set B here"))
            _pendingB = here;
        ImGui.SameLine();
        ImGui.TextUnformatted(_pendingB is { } b ? $"B = {b:f2}" : "B = unset");

        ImGui.Checkbox("Flying route", ref _pendingFly);
        ImGui.InputText("Notes", ref _pendingNotes, 256);

        using (ImRaii.Disabled(_pendingA == null || _pendingB == null))
        {
            if (ImGui.Button("Add route"))
            {
                _routes.Add(new Route
                {
                    Id = _routes.SuggestId(territory),
                    Territory = territory,
                    A = _pendingA!.Value,
                    B = _pendingB!.Value,
                    Fly = _pendingFly,
                    Notes = _pendingNotes,
                });
                _pendingA = _pendingB = null;
                _pendingNotes = "";
            }
        }
    }

    private void DrawRouteList(uint territory)
    {
        var here = _routes.ForTerritory(territory).ToList();
        ImGui.TextUnformatted($"Routes in territory {territory}: {here.Count}");

        var idle = _recorder.CurrentState == TrajectoryRecorder.State.Idle;

        foreach (var route in here)
        {
            using var id = ImRaii.PushId(route.Id);

            if (ImGui.Selectable($"{route.Id}  ({route.Length:f0}y{(route.Fly ? ", fly" : "")})", _selected == route))
                _selected = route;

            using var _ = ImRaii.PushIndent();

            var repeats = Math.Max(1, Service.Config.Humanizer.RecorderRepeats);
            using (ImRaii.Disabled(!idle))
            {
                if (ImGui.Button($"Record human x{repeats}"))
                    _recorder.Begin(route, TrajectoryRecorder.Source.Human, repeats);
                ImGui.SameLine();
                if (ImGui.Button($"Record agent x{repeats}"))
                    _recorder.Begin(route, TrajectoryRecorder.Source.Agent, repeats);
            }

            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                _routes.Remove(route);
                if (_selected == route)
                    _selected = null;
                break;
            }

            if (route.Notes.Length > 0)
                ImGui.TextDisabled(route.Notes);
        }
    }

    // called from MainWindow.EndFrame, where the drawer is between StartFrame/EndFrame
    public void DrawWorld()
    {
        if (!Service.Config.Humanizer.RecorderDrawRoute)
            return;

        var route = _recorder.CurrentRoute ?? _selected;
        if (route == null || route.Territory != Service.ClientState.TerritoryType)
            return;

        // A green, B magenta, with a hint line between them
        _dd.DrawWorldSphere(route.A, 1.0f, 0xff00ff00);
        _dd.DrawWorldSphere(route.B, 1.0f, 0xffff00ff);
        _dd.DrawWorldLine(route.A, route.B, 0x40ffffff);
    }
}
