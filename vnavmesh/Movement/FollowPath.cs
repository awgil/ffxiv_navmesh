using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.Movement;

public class FollowPath : IDisposable
{
    public bool MovementAllowed = true;
    public bool AlignCamera = false;
    public bool IgnoreDeltaY = false;
    public float Tolerance = 0.5f;
    public List<Vector3> Waypoints = new();
    private DateTime NextJump = default;

    private NavmeshManager _manager;
    private OverrideCamera _camera = new();
    private OverrideMovement _movement = new();

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
            var toNext = Waypoints[0] - player.Position;
            if (IgnoreDeltaY)
                toNext.Y = 0;
            if (toNext.LengthSquared() > Tolerance * Tolerance)
                break;
            Waypoints.RemoveAt(0);
        }

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
            if (_movement.DesiredPosition.Y > player.Position.Y)
            {
                if (!Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight] && Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted])
                    ExecuteJump(); // Spam jump to take off
                else if (!Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight] && !IgnoreDeltaY)
                    _movement.Enabled = false; // Else if we're not in flight, don't move since the target is above us and we can't reach it
            }
            _camera.Enabled = AlignCamera;
            _camera.SpeedH = _camera.SpeedV = 360.Degrees();
            _camera.DesiredAzimuth = Angle.FromDirectionXZ(_movement.DesiredPosition - player.Position) + 180.Degrees();
            _camera.DesiredAltitude = -30.Degrees();
        }
    }

    public void Stop() => Waypoints.Clear();

    private unsafe void ExecuteJump()
    {
        if (DateTime.Now >= NextJump)
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
            NextJump = DateTime.Now.AddMilliseconds(100);
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
