using Dalamud.Plugin;
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

    // Event for requesting re-pathing when stuck
    public event Action<Vector3, Vector3, bool>? OnRepathRequested; // (currentPos, destination, wasFlying)

    private IDalamudPluginInterface _dalamud;
    private NavmeshManager _manager;
    private OverrideCamera _camera = new();
    private OverrideMovement _movement;
    private DateTime _nextJump;

    private Vector3? posPreviousFrame;

    // Stuck detection state
    private DateTime _lastProgressTime = DateTime.Now;
    private Vector3 _lastProgressPosition;
    private float _lastDistanceToWaypoint = float.MaxValue;
    private int _retryCount = 0;
    private Vector3 _originalDestination;
    private bool _wasFlying;

    // entries in dalamud shared data cache must be reference types, so we use an array
    private readonly bool[] _sharedPathIsRunning;

    private const string _sharedPathTag = "vnav.PathIsRunning";

    public FollowPath(IDalamudPluginInterface dalamud, NavmeshManager manager)
    {
        _dalamud = dalamud;
        _sharedPathIsRunning = _dalamud.GetOrCreateData<bool[]>(_sharedPathTag, () => [false]);
        _manager = manager;
        _movement = new OverrideMovement(_camera);
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

        posPreviousFrame = player.Position;

        if (Waypoints.Count == 0)
        {
            _movement.Enabled = _camera.Enabled = false;
            _camera.SpeedH = _camera.SpeedV = default;
            _movement.DesiredPosition = player.Position;
            UpdateSharedState(false);
        }
        else
        {
            if (Service.Config.CancelMoveOnUserInput && _movement.UserInput)
            {
                Stop();
                return;
            }

            // Check for stuck condition
            if (Service.Config.EnableStuckDetection && CheckStuckCondition(player))
            {
                Service.Log.Warning($"Player appears to be stuck. Attempting re-path (retry {_retryCount + 1}/{Service.Config.MaxRetryAttempts})");
                if (_retryCount < Service.Config.MaxRetryAttempts)
                {
                    _retryCount++;
                    RequestRepathing(player.Position);
                    return;
                }
                else
                {
                    Service.Log.Error("Max retry attempts reached. Stopping movement.");
                    Stop();
                    return;
                }
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

    public void Stop()
    {
        UpdateSharedState(false);
        Waypoints.Clear();
        ResetStuckDetection();
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
        
        // Store destination and flight mode for re-pathing
        if (waypoints.Count > 0)
        {
            _originalDestination = waypoints[^1];
            _wasFlying = !ignoreDeltaY;
        }
        
        ResetStuckDetection();
    }

    private void OnNavmeshChanged(Navmesh? navmesh, NavmeshQuery? query)
    {
        UpdateSharedState(false);
        Waypoints.Clear();
        ResetStuckDetection();
    }

    private void ResetStuckDetection()
    {
        _lastProgressTime = DateTime.Now;
        _lastProgressPosition = Vector3.Zero;
        _lastDistanceToWaypoint = float.MaxValue;
        _retryCount = 0;
    }

    private bool CheckStuckCondition(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
    {
        if (Waypoints.Count == 0)
            return false;

        var currentPosition = player.Position;
        var targetWaypoint = Waypoints[0];
        var currentDistance = Vector3.Distance(currentPosition, targetWaypoint);
        
        // Check if we've made progress
        bool madeProgress = false;
        
        // Progress check 1: Getting closer to the waypoint
        if (currentDistance < _lastDistanceToWaypoint - Service.Config.StuckDistanceThreshold)
        {
            madeProgress = true;
        }
        
        // Progress check 2: Player has moved a reasonable distance
        if (_lastProgressPosition != Vector3.Zero)
        {
            var playerMovement = Vector3.Distance(currentPosition, _lastProgressPosition);
            if (playerMovement > Service.Config.StuckDistanceThreshold)
            {
                madeProgress = true;
            }
        }
        
        // Update progress tracking
        if (madeProgress)
        {
            _lastProgressTime = DateTime.Now;
            _lastProgressPosition = currentPosition;
            _lastDistanceToWaypoint = currentDistance;
            return false;
        }
        
        // Initialize tracking on first check
        if (_lastProgressPosition == Vector3.Zero)
        {
            _lastProgressPosition = currentPosition;
            _lastDistanceToWaypoint = currentDistance;
            return false;
        }
        
        // Check if we've been stuck for too long
        var timeSinceProgress = DateTime.Now - _lastProgressTime;
        return timeSinceProgress.TotalSeconds > Service.Config.StuckTimeoutSeconds;
    }

    private void RequestRepathing(Vector3 currentPosition)
    {
        if (_originalDestination == Vector3.Zero)
            return;

        // Stop current movement
        Waypoints.Clear();
        _movement.Enabled = false;
        _camera.Enabled = false;
        UpdateSharedState(false);
        
        Service.Log.Information($"Requesting re-path from {currentPosition} to {_originalDestination} (flying: {_wasFlying})");
        
        // Trigger re-pathing event for the calling system to handle
        OnRepathRequested?.Invoke(currentPosition, _originalDestination, _wasFlying);
        
        // Reset stuck detection for the new path attempt
        ResetStuckDetection();
    }
}
