using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.Movement;

public class FollowPath : IDisposable
{
    public bool MovementAllowed = true;
    public bool AlignCamera = false;
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
            _movement.DesiredPosition = _waypoints[0];
            _camera.Enabled = AlignCamera;
            _camera.SpeedH = _camera.SpeedV = 360.Degrees();
            _camera.DesiredAzimuth = Angle.FromDirectionXZ(_movement.DesiredPosition - player.Position) + 180.Degrees();
            _camera.DesiredAltitude = -30.Degrees();
        }
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
