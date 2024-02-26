using DotRecast.Detour;
using System;
using System.Collections.Generic;
using System.Numerics;
using XIVRunner;

namespace Navmesh.Movement;

public class FollowPath : IDisposable
{
    public bool MovementAllowed = true;
    public bool UseRaycasts = true;
    public bool UseStringPulling = true;
    public float Tolerance = 0.5f;

    public NavmeshQuery? Query => _query;
    public IReadOnlyList<Vector3> Waypoints => _waypoints;

    private NavmeshManager _manager;
    private NavmeshQuery? _query;
    private bool _flying;
    private List<Vector3> _waypoints = new();
    private OverrideCamera _camera = new();
    private OverrideMovement _movement = new();
    private OverrideAFK _overrideAFK = new();

    public FollowPath(NavmeshManager manager)
    {
        _manager = manager;
        _manager.OnNavmeshChanged += OnNavmeshChanged;
        OnNavmeshChanged(_manager.Navmesh);
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

        while (_waypoints.Count > 0)
        {
            var toNext = _waypoints[0] - player.Position;
            if (!_flying)
                toNext.Y = 0;
            if (toNext.LengthSquared() > Tolerance * Tolerance)
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
            _movement.Enabled = MovementAllowed;
            //_camera.Enabled = true;
            _movement.DesiredPosition = _waypoints[0];
            _camera.SpeedH = _camera.SpeedV = 360.Degrees();
            _camera.DesiredAzimuth = Angle.FromDirectionXZ(_movement.DesiredPosition - player.Position) + 180.Degrees();
            _overrideAFK.ResetTimers();
        }
    }

    public Vector3 FindClosestPolyPoint(Vector3 input, Vector3 halfExtents)
    {
        if (_query == null)
            return Vector3.Zero;

        var m_filter = new DtQueryDefaultFilter(
                0xffff,
                0x10,
                [1f, 1f, 1f, 1f, 2f, 1.5f]
            );

        _ = _query.MeshQuery.FindNearestPoly(input.SystemToRecast(), halfExtents.SystemToRecast(), m_filter, out _, out var polyPoint, out _);

        return polyPoint.RecastToSystem();
    }

    public void MoveTo(Vector3 destination)
    {
        var player = Service.ClientState.LocalPlayer;
        if (player == null || _query == null)
            return;
        _waypoints = _query.PathfindMesh(player.Position, destination, UseRaycasts, UseStringPulling);
        _flying = false;
    }

    public void FlyTo(Vector3 destination)
    {
        var player = Service.ClientState.LocalPlayer;
        if (player == null || _query == null)
            return;
        _waypoints = _query.PathfindVolume(player.Position, destination, UseRaycasts, UseStringPulling);
        _flying = true;
    }

    public void Stop() => _waypoints.Clear();

    private void OnNavmeshChanged(Navmesh? navmesh)
    {
        _query = null;
        _waypoints.Clear();
        if (navmesh != null)
            _query = new(navmesh);
    }
}
