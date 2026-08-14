using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace Navmesh.Movement.Human;

// task-driven capture of paired human/agent trajectories over identical routes; see ADR 0003
public unsafe class TrajectoryRecorder : IDisposable
{
    public enum State
    {
        Idle,
        Travelling, // plugin is driving the player to A
        Armed,      // sitting at A, waiting for departure
        Recording,  // capturing A -> B
    }

    public enum Source
    {
        Human,
        Agent,
    }

    public const int SchemaVersion = 2;

    public State CurrentState { get; private set; } = State.Idle;
    public Source CurrentSource { get; private set; }
    public Route? CurrentRoute { get; private set; }
    public string Status { get; private set; } = "idle";
    public int SampleCount => _samples.Count;
    public string CaptureDir => _captureDir;

    private readonly AsyncMoveRequest _move;
    private readonly FollowPath _follow;
    private readonly NavmeshManager _manager;
    private readonly string _captureDir;
    private readonly List<TrajectorySample> _samples = [];

    private DateTime _stateEntered;
    private DateTime _recordStarted;
    private DateTime _armedAt;
    private float _armedToFirstInputMs;
    private Vector3 _startFacingPos;
    private float _startFacing;
    private Vector3? _prevPos;
    private bool _agentMoveIssued;
    private uint _prevWalkSeq;
    private uint _prevFlySeq;
    private bool _sawIdle; // seen genuine zero input while armed, so the next press is a real departure

    public TrajectoryRecorder(AsyncMoveRequest move, FollowPath follow, NavmeshManager manager, string configDir)
    {
        _move = move;
        _follow = follow;
        _manager = manager;
        _captureDir = Path.Combine(configDir, "captures");
    }

    // a walking pathfind needs a start polygon on the walkable mesh, so being airborne or otherwise
    // off-mesh fails deep inside the query with a message that does not name the real cause
    private string? WhyCannotStart(Route route, Vector3 playerPos)
    {
        if (_manager.Navmesh == null || _manager.Query is not { } query)
            return "navmesh is not loaded yet";
        if (route.Fly)
            return null;
        if (query.FindNearestMeshPoly(playerPos) == 0)
            return "you are not standing on the navmesh - land first";
        if (query.FindNearestMeshPoly(route.A) == 0)
            return $"route point A ({route.A:f1}) is not on the navmesh";
        return null;
    }

    public void Dispose() => Cancel("plugin unloading");

    public void Begin(Route route, Source source)
    {
        if (CurrentState != State.Idle)
        {
            Service.Log.Warning("[recorder] already running, ignoring Begin");
            return;
        }

        if (Service.ClientState.TerritoryType != route.Territory)
        {
            Status = $"wrong territory (need {route.Territory})";
            return;
        }

        if (Service.ObjectTable.LocalPlayer is not { } player)
        {
            Status = "no player";
            return;
        }

        if (WhyCannotStart(route, player.Position) is { } reason)
        {
            Status = reason;
            Service.Log.Warning($"[recorder] refusing to start: {reason}");
            return;
        }

        CurrentRoute = route;
        CurrentSource = source;
        _samples.Clear();
        _prevPos = null;
        _agentMoveIssued = false;
        _armedToFirstInputMs = 0;

        // hooks installed but not writing, so we observe genuine input during human captures
        _follow.Movement.Observing = true;

        if (!_move.MoveTo(route.A, route.Fly, Service.Config.Humanizer.RecorderArriveTolerance * 0.5f))
        {
            _follow.Movement.Observing = false;
            CurrentRoute = null;
            Status = "another pathfind is already running";
            return;
        }

        Enter(State.Travelling);
        Status = "travelling to A";
    }

    public void Cancel(string reason)
    {
        if (CurrentState == State.Idle)
            return;

        Service.Log.Info($"[recorder] cancelled: {reason}");
        _follow.Stop();
        _follow.Movement.Observing = false;
        _samples.Clear();
        CurrentRoute = null;
        Enter(State.Idle);
        Status = $"cancelled: {reason}";
    }

    public void Update(IFramework fwk)
    {
        if (CurrentState == State.Idle || CurrentRoute is not { } route)
            return;

        var player = Service.ObjectTable.LocalPlayer;
        if (player == null)
        {
            Cancel("no player");
            return;
        }
        if (Service.ClientState.TerritoryType != route.Territory)
        {
            Cancel("left territory");
            return;
        }

        var cfg = Service.Config.Humanizer;
        if ((DateTime.UtcNow - _stateEntered).TotalSeconds > cfg.RecorderTimeoutSec)
        {
            Cancel($"timed out in {CurrentState}");
            return;
        }

        var pos = player.Position;
        var mv = _follow.Movement;

        // advance both watermarks exactly once per frame, so nothing downstream double consumes them
        var walkFresh = mv.WalkInputSequence != _prevWalkSeq;
        var flyFresh = mv.FlyInputSequence != _prevFlySeq;
        _prevWalkSeq = mv.WalkInputSequence;
        _prevFlySeq = mv.FlyInputSequence;

        switch (CurrentState)
        {
            case State.Travelling:
                // a pathfind still in flight will repopulate the waypoints after we stop, and the
                // armed state would read that as departure. matters when we already start on top of A.
                if (_move.TaskInProgress)
                    break;

                if (Vector3.Distance(pos, route.A) <= cfg.RecorderArriveTolerance)
                {
                    _follow.Stop();
                    _armedAt = DateTime.UtcNow;
                    _sawIdle = false;
                    Enter(State.Armed);
                    Status = CurrentSource == Source.Human
                        ? "at A - release keys, then walk to B"
                        : "at A - dispatching agent";
                }
                else if (_follow.Waypoints.Count == 0)
                {
                    // pathfind finished and the follower ran dry without getting us there; the query
                    // logs the underlying reason, so say what we know rather than just "could not reach"
                    var dist = Vector3.Distance(pos, route.A);
                    Cancel($"could not reach A, still {dist:f0}y away - check the pathfind error above");
                }
                break;

            case State.Armed:
                if (CurrentSource == Source.Agent && !_agentMoveIssued)
                {
                    if (!_move.MoveTo(route.B, route.Fly, cfg.RecorderFinishTolerance * 0.5f))
                        break; // pathfind busy, retry next frame
                    _agentMoveIssued = true;
                }

                // arriving at A leaves the travelling phase's full throttle value sitting in the
                // channel, so wait for a genuine idle before treating a press as departure
                var armedMag = FreshMagnitude(mv, walkFresh, flyFresh);
                if (!_sawIdle)
                {
                    if ((walkFresh || flyFresh) && armedMag <= cfg.RecorderDepartThreshold)
                        _sawIdle = true;
                    break;
                }

                // both sources start recording at departure, so the captures align
                if (armedMag > cfg.RecorderDepartThreshold)
                {
                    _armedToFirstInputMs = (float)(DateTime.UtcNow - _armedAt).TotalMilliseconds;
                    _recordStarted = DateTime.UtcNow;
                    _startFacingPos = pos;
                    _startFacing = player.Rotation;
                    _prevPos = null;
                    Enter(State.Recording);
                    Status = "recording";
                    Sample(fwk, pos, player.Rotation, mv, walkFresh, flyFresh);
                }
                break;

            case State.Recording:
                Sample(fwk, pos, player.Rotation, mv, walkFresh, flyFresh);
                if (Vector3.Distance(pos, route.B) <= cfg.RecorderFinishTolerance)
                    Finish(route);
                break;
        }
    }

    // largest magnitude across the channels that actually ran this frame; a stale channel
    // reads as zero rather than holding its last value forever
    private static float FreshMagnitude(OverrideMovement mv, bool walkFresh, bool flyFresh) =>
        MathF.Max(walkFresh ? mv.LastWalkInput.Length() : 0, flyFresh ? mv.LastFlyInput.Length() : 0);

    private void Sample(IFramework fwk, Vector3 pos, float facing, OverrideMovement mv, bool walkFresh, bool flyFresh)
    {
        var dt = (float)fwk.UpdateDelta.TotalSeconds;
        var speed = _prevPos is { } prev && dt > 0 ? Vector3.Distance(pos, prev) / dt : 0;
        _prevPos = pos;

        float camH = 0, camV = 0;
        var cam = (CameraEx*)CameraManager.Instance()->GetActiveCamera();
        if (cam != null)
        {
            camH = cam->DirH;
            camV = cam->DirV;
        }

        var flags = SampleFlags.None;
        if (Service.Condition[ConditionFlag.Mounted])
            flags |= SampleFlags.Mounted;
        if (Service.Condition[ConditionFlag.InFlight])
            flags |= SampleFlags.InFlight;
        if (Service.Condition[ConditionFlag.Diving])
            flags |= SampleFlags.Diving;
        if (Service.Condition[ConditionFlag.Jumping])
            flags |= SampleFlags.Jumping;
        if ((walkFresh && mv.LastWalkOverridden) || (flyFresh && mv.LastFlyOverridden))
            flags |= SampleFlags.Overridden;
        if (walkFresh)
            flags |= SampleFlags.WalkFresh;
        if (flyFresh)
            flags |= SampleFlags.FlyFresh;

        _samples.Add(new TrajectorySample
        {
            T = (float)(DateTime.UtcNow - _recordStarted).TotalSeconds,
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
            Facing = facing,
            CamH = camH,
            CamV = camV,
            InLeft = walkFresh ? mv.LastWalkInput.X : 0,
            InFwd = walkFresh ? mv.LastWalkInput.Y : 0,
            FlyUp = flyFresh ? mv.LastFlyInput.Z : 0,
            Speed = speed,
            Flags = flags,
        });
    }

    private void Finish(Route route)
    {
        var path = Write(route);
        _follow.Stop();
        _follow.Movement.Observing = false;
        Status = path != null ? $"saved {_samples.Count} samples to {Path.GetFileName(path)}" : "save failed";
        Service.Log.Info($"[recorder] {Status}");
        _samples.Clear();
        CurrentRoute = null;
        Enter(State.Idle);
    }

    private string? Write(Route route)
    {
        try
        {
            var dir = Path.Combine(_captureDir, route.Territory.ToString(CultureInfo.InvariantCulture), route.Id);
            Directory.CreateDirectory(dir);
            var name = $"{CurrentSource.ToString().ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
            var full = Path.Combine(dir, name);

            var samples = new JArray();
            Span<float> buf = stackalloc float[TrajectorySample.Columns.Length];
            foreach (var s in _samples)
            {
                s.Write(buf);
                var row = new JArray();
                foreach (var v in buf)
                    row.Add(v);
                samples.Add(row);
            }

            var doc = new JObject
            {
                ["schema"] = SchemaVersion,
                ["source"] = CurrentSource.ToString().ToLowerInvariant(),
                ["territory"] = route.Territory,
                ["route"] = JObject.FromObject(route),
                ["startedUtc"] = _recordStarted.ToString("o", CultureInfo.InvariantCulture),
                ["durationMs"] = (DateTime.UtcNow - _recordStarted).TotalMilliseconds,
                ["armedToFirstInputMs"] = _armedToFirstInputMs,
                ["startFacing"] = _startFacing,
                ["startPos"] = new JArray(_startFacingPos.X, _startFacingPos.Y, _startFacingPos.Z),
                ["columns"] = new JArray(TrajectorySample.Columns),
                ["samples"] = samples,
            };

            File.WriteAllText(full, doc.ToString(Formatting.None));
            return full;
        }
        catch (Exception e)
        {
            Service.Log.Error($"[recorder] failed to write capture: {e}");
            return null;
        }
    }

    private void Enter(State state)
    {
        CurrentState = state;
        _stateEntered = DateTime.UtcNow;
    }
}
