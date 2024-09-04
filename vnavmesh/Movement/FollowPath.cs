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
    public List<Vector3> Waypoints = new();

    private NavmeshManager _manager;
    private OverrideCamera _camera = new();
    private OverrideMovement _movement = new();
    private DateTime _nextJump;

    private Vector3? posPreviousFrame;

    public FollowPath(NavmeshManager manager)
    {
        _manager = manager;
        _manager.OnNavmeshChanged += OnNavmeshChanged;
        OnNavmeshChanged(_manager.Navmesh, _manager.Query);
    }

    public void Dispose()
    {
        _manager.OnNavmeshChanged -= OnNavmeshChanged;
        _camera.Dispose();
        _movement.Dispose();
    }

    public unsafe void Update()
    {
        var player = Service.ClientState.LocalPlayer;
        if (player == null)
            return;

        while (Waypoints.Count > 0)
        {
            var a = Waypoints[0];
            var b = player.Position;
            var c = posPreviousFrame ?? b;

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

        posPreviousFrame = player.Position;

        if (Waypoints.Count == 0)
        {
            _movement.Enabled = _camera.Enabled = false;
            _camera.SpeedH = _camera.SpeedV = default;
            _movement.DesiredPosition = player.Position;
        }
        else
        {
            OverrideAFK.ResetTimers();
            _movement.Enabled = MovementAllowed;
            _movement.DesiredPosition = Waypoints[0];
            if (_movement.DesiredPosition.Y > player.Position.Y && !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight] && !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving] && && !IgnoreDeltaY) //Only do this bit if on a flying path
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
            _camera.DesiredAltitude = -30.Degrees();
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

    public void Stop() => Waypoints.Clear();

    private unsafe void ExecuteJump()
    {
        if (DateTime.Now >= _nextJump)
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
            _nextJump = DateTime.Now.AddMilliseconds(100);
        }
    }

    public void Move(List<Vector3> waypoints, bool ignoreDeltaY)
    {
        Waypoints = waypoints;
        IgnoreDeltaY = ignoreDeltaY;
    }

    private void OnNavmeshChanged(Navmesh? navmesh, NavmeshQuery? query)
    {
        Waypoints.Clear();
    }
}
