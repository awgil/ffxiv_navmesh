using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.Movement;

public class FollowPath : IDisposable
{
    public bool MovementAllowed = true;
    public bool IgnoreDeltaY = false;
    public float Tolerance = 0.25f;
    public float DestinationTolerance = 0;
    public List<Vector3> Waypoints = new();

    private IDalamudPluginInterface _dalamud;
    private NavmeshManager _manager;
    private OverrideCamera _camera = new();
    private OverrideMovement _movement = new();
    private DateTime _nextJump;

    private Vector3? posPreviousFrame;

    private int _millisecondsWithNoSignificantMovement = 0;

    public event Action<Vector3, bool, float>? OnStuck;

    // entries in dalamud shared data cache must be reference types, so we use an array
    private readonly bool[] _sharedPathIsRunning;

    private const string _sharedPathTag = "vnav.PathIsRunning";

    public FollowPath(IDalamudPluginInterface dalamud, NavmeshManager manager)
    {
        _dalamud = dalamud;
        _sharedPathIsRunning = _dalamud.GetOrCreateData<bool[]>(_sharedPathTag, () => [false]);
        _manager = manager;
        _manager.OnNavmeshChanged += OnNavmeshChanged;
        OnNavmeshChanged(_manager.Navmesh, _manager.Query);
    }

    public void Dispose()
    {
        UpdateSharedState(false);
        _dalamud.RelinquishData(_sharedPathTag);
        _manager.OnNavmeshChanged -= OnNavmeshChanged;
        _camera.Dispose();
        _movement.Dispose();
    }

    private void UpdateSharedState(bool isRunning) => _sharedPathIsRunning[0] = isRunning;

    public void Update(IFramework fwk)
    {
        var player = Service.ClientState.LocalPlayer;
        if (player == null)
            return;

        while (Waypoints.Count > 0)
        {
            var a = Waypoints[0];
            var b = player.Position;
            var c = posPreviousFrame ?? b;

            if (DestinationTolerance > 0 && (b - Waypoints[^1]).Length() <= DestinationTolerance)
            {
                Waypoints.Clear();
                break;
            }

            if (IgnoreDeltaY)
            {
                a.Y = 0;
                b.Y = 0;
                c.Y = 0;
            }

            if (DistanceToLineSegment(a, b, c) > Tolerance)
                break;

            Waypoints.RemoveAt(0);
        }


        if (Waypoints.Count == 0)
        {
            posPreviousFrame = player.Position;
            _movement.Enabled = _camera.Enabled = false;
            _camera.SpeedH = _camera.SpeedV = default;
            _movement.DesiredPosition = player.Position;
            UpdateSharedState(false);
        }
        else
        {
            if (Service.Config.StopOnStuck && posPreviousFrame.HasValue)
            {
                float delta = fwk.UpdateDelta.Milliseconds / 1000f;
                float distance = Vector3.Distance(player.Position, posPreviousFrame.Value) / delta;
                if (distance <= Service.Config.StuckTolerance)
                {
                    _millisecondsWithNoSignificantMovement += fwk.UpdateDelta.Milliseconds;
                }
                else
                {
                    _millisecondsWithNoSignificantMovement = 0;
                }

                if (_millisecondsWithNoSignificantMovement >= Service.Config.StuckTimeoutMs)
                {
                    var destination = Waypoints[^1];
                    Stop();
                    OnStuck?.Invoke(destination, !IgnoreDeltaY, DestinationTolerance);
                    return;
                }
            }

            posPreviousFrame = player.Position;

            if (Service.Config.CancelMoveOnUserInput && _movement.UserInput)
            {
                Stop();
                return;
            }

            OverrideAFK.ResetTimers();
            _movement.Enabled = MovementAllowed;
            _movement.DesiredPosition = Waypoints[0];
            if (_movement.DesiredPosition.Y > player.Position.Y && !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight] && !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving] && !IgnoreDeltaY) //Only do this bit if on a flying path
            {
                // walk->fly transition (TODO: reconsider?)
                if (Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted])
                    ExecuteJump(); // Spam jump to take off
                else
                {
                    _movement.Enabled = false; // Don't move, since it'll just run on the spot
                    return;
                }
            }

            _camera.Enabled = Service.Config.AlignCameraToMovement;
            _camera.SpeedH = _camera.SpeedV = 360.Degrees();
            _camera.DesiredAzimuth = Angle.FromDirectionXZ(_movement.DesiredPosition - player.Position) + 180.Degrees();
            _camera.DesiredAltitude = Service.Config.AlignCameraHeight.Degrees();
        }
    }

    private static float DistanceToLineSegment(Vector3 v, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var av = v - a;

        if (ab.Length() == 0 || Vector3.Dot(av, ab) <= 0)
            return av.Length();

        var bv = v - b;
        if (Vector3.Dot(bv, ab) >= 0)
            return bv.Length();

        return Vector3.Cross(ab, av).Length() / ab.Length();
    }

    public void Stop()
    {
        UpdateSharedState(false);
        _millisecondsWithNoSignificantMovement = 0;
        Waypoints.Clear();
    }

    private unsafe void ExecuteJump()
    {
        // Unable to jump while diving, prevents spamming error messages.
        if (Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving])
            return;

        if (DateTime.Now >= _nextJump)
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
            _nextJump = DateTime.Now.AddMilliseconds(100);
        }
    }

    public void Move(List<Vector3> waypoints, bool ignoreDeltaY, float destTolerance = 0)
    {
        UpdateSharedState(true);
        Waypoints = waypoints;
        IgnoreDeltaY = ignoreDeltaY;
        DestinationTolerance = destTolerance;
    }

    private void OnNavmeshChanged(Navmesh? navmesh, NavmeshQuery? query)
    {
        UpdateSharedState(false);
        Waypoints.Clear();
    }
}
