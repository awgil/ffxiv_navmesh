using System;
using System.Collections.Generic;
using System.Numerics;
using Navmesh.Debug;

namespace Navmesh.Movement;

public class FollowPath : IDisposable
{
    public float Tolerance = 0.5f;

    private NavmeshBuilder _navmesh;
    private List<Vector3> _waypoints = new();
    private OverrideCamera _camera = new();
    private OverrideMovement _movement = new();

    public FollowPath(NavmeshBuilder navmesh)
    {
        _navmesh = navmesh;
    }

    public void Dispose()
    {
        _camera.Dispose();
        _movement.Dispose();
    }

    public unsafe void Update()
    {
        var player = Service.ClientState.LocalPlayer;
        if (player == null)
            return;

        while (_waypoints.Count > 0)
        {
            var toNext = _waypoints[0] - player.Position;
            if (new Vector2(toNext.X, toNext.Z).LengthSquared() > Tolerance * Tolerance)
                break;
            _waypoints.RemoveAt(0);
        }

        if (_waypoints.Count == 0)
        {
            _movement.Enabled = _camera.Enabled = false;
            _camera.SpeedH = _camera.SpeedV = default;
            _movement.DesiredPosition = player.Position;
        }
        else
        {
            _movement.Enabled = true;
            //_camera.Enabled = true;
            _movement.DesiredPosition = _waypoints[0];
            _camera.SpeedH = _camera.SpeedV = 360.Degrees();
            _camera.DesiredAzimuth = Angle.FromDirectionXZ(_movement.DesiredPosition - player.Position) + 180.Degrees();
        }
    }

    public void DrawPath(DebugDrawer drawer)
    {
        var player = Service.ClientState.LocalPlayer;
        if (player == null)
            return;
        var from = player.Position;
        var color = 0xff00ff00;
        foreach (var to in _waypoints)
        {
            drawer.DrawWorldLine(from, to, color);
            from = to;
            color = 0xff00ffff;
        }
    }

    public void RebuildNavmesh()
    {
        if (_navmesh.CurrentState == NavmeshBuilder.State.InProgress)
            return;
        _navmesh.Rebuild();
    }

    public void MoveTo(Vector3 destination)
    {
        var player = Service.ClientState.LocalPlayer;
        if (player == null)
            return;
        _waypoints = _navmesh.Pathfind(player.Position, destination);
    }

    public void Stop() => _waypoints.Clear();
}
