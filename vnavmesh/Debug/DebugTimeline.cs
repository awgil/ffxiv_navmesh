using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Clip;
using FFXIVClientStructs.Interop;
using System;

namespace Navmesh.Debug;

internal unsafe class DebugTimeline
{
    private readonly UITree _tree = new();

    public void Draw()
    {
        var am = ActionTimelineManager.Instance();
        ImGui.TextUnformatted($"Address: {(nint)am:X}");

        var sp = new Span<Pointer<TimelineGroup>>(&am->Timeline1, 5);
        for (var i = 0; i < sp.Length; i++)
            DrawGroup(i.ToString(), sp[i]);
    }

    private void DrawGroup(string i, TimelineGroup* tg)
    {
        using var nt = _tree.Node($"[{i}] TimelineGroup {(nint)tg:X}###tg{i}", tg == null);
        if (!nt.Opened)
            return;

        if (tg->List.First != null)
            DrawGroup($"{i}->Prev", tg->List.First);
        if (tg->List.Last != null)
            DrawGroup($"{i}->Next", tg->List.Last);

        var nextTimeline = tg->SomeTimeline;
        while (nextTimeline != null)
        {
            DrawTimeline(nextTimeline);
            nextTimeline = nextTimeline->List.Last;
        }
    }

    private void DrawTimeline(SchedulerTimeline* timeline)
    {
        var atk = timeline == null ? "" : LayoutUtils.ReadString(timeline->ActionTimelineKey);
        using var nt = _tree.Node($"SchedulerTimeline {(nint)timeline:X} '{atk}'", timeline == null);
        if (!nt.Opened)
            return;

        _tree.LeafNode($"Timestamp: {timeline->CurrentTimestamp}");
        var face = LayoutUtils.ReadString(timeline->FaceLibraryPath);
        if (face != "")
            _tree.LeafNode($"FaceLibrary: {face}");

        DrawState4(timeline->State4List);
    }

    private void DrawState4(SchedulerState4* state)
    {
        using var nt = _tree.Node($"State4 list: {(nint)state:X}", state == null);
        if (!nt.Opened)
            return;

        foreach (var c in state->Children)
            DrawState3(c.Value);
    }

    private void DrawState3(SchedulerState3* state)
    {
        using var nt = _tree.Node($"State3 list: {(nint)state:X}", state == null);
        if (!nt.Opened)
            return;

        foreach (var clip in state->Children)
            DrawBaseClip(clip.Value);
    }

    private void DrawBaseClip(BaseClip* clip)
    {
        var vt = clip->VirtualTable;
        var label = vt == SharedGroupClip.StaticVirtualTablePointer ? "SharedGroupClip" : "BaseClip";
        _tree.LeafNode($"{label} {(nint)clip:X}");
    }
}
