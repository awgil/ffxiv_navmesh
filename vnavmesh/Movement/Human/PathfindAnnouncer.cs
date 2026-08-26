using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace Navmesh.Movement.Human;

// prints the pathfind echo into the local chat log, see ADR 0009. one of these per query: the
// "searching" line goes out when the search actually starts, and the query's end reports itself
// through Done or, if it never gets there, through Dispose
public sealed class PathfindAnnouncer : IDisposable
{
    private readonly CancellationToken _cancel;
    private readonly bool _enabled;
    private readonly long _started = Stopwatch.GetTimestamp();
    private bool _reported;

    public PathfindAnnouncer(Vector3 dest, bool fly, float range, CancellationToken cancel)
    {
        _cancel = cancel;
        _enabled = Service.Config.Humanizer.PathfindChatEnabled;
        if (_enabled)
            Print(PathfindChat.Searching(dest, fly, range));
    }

    public void Done() => Report(PathfindChat.Done(Stopwatch.GetElapsedTime(_started)));

    // getting here without a report means the query threw: cancellation if that is what the token
    // says, otherwise a failure, whose details are in the log
    public void Dispose() => Report(_cancel.IsCancellationRequested ? PathfindChat.Cancelled() : PathfindChat.Failed());

    private void Report(string message)
    {
        if (_reported)
            return;
        _reported = true;
        if (_enabled)
            Print(message);
    }

    // queries finish on the main thread, but a faulted one can land elsewhere, and ChatGui is not
    // safe off it
    private static void Print(string message)
    {
        var line = $"[{Service.PluginInterface.Manifest.Name}] {message}";
        _ = Service.Framework.RunOnFrameworkThread(() => Service.ChatGui.Print(line));
    }
}
