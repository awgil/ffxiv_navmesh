using FFXIVClientStructs.FFXIV.Client.Game;
using IPlayerCharacter = Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Navmesh.Movement;

public class FollowPath : IDisposable
{
    public bool MovementAllowed = true;
    public bool Fly = false;                    //  is a FlyTo command
    public float Tolerance = 0.25f;             //  waypoint threshold, distance < Tolerance means player reached node
    public List<Vector3> Waypoints = new();     //  nodes to follow

    private NavmeshManager _manager;
    private AsyncMoveRequest _asyncmove { get; set; }
    private OverrideCamera _camera = new();
    private OverrideMovement _movement = new();
    private DateTime _nextJump;
    private IPlayerCharacter? _player;
    private Vector3 _playerPos;

    private Vector3? posPreviousFrame;
    private FixedSizeDeque<float> _previousDistancesSquared = new(5);
    private DateTime _lastMoveToRequestTime = DateTime.MinValue;
    private const int moveRequestTimeoutMs = 500;
    private bool _bMoveRequestAllowed => !Fly && DateTime.Now - _lastMoveToRequestTime > TimeSpan.FromMilliseconds(moveRequestTimeoutMs);

    public void SetAsyncMove(AsyncMoveRequest asyncmove)
    {
        _asyncmove = asyncmove;
    }

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
        if (Service.ClientState.LocalPlayer is not { } player)
            return;
        _player = player;

        if (Waypoints.Count > 0)
        {
            var node = Waypoints[0];

            var nodeDistSq = (node - player.Position).LengthSquared();

            HandlePlayerMovingAway(nodeDistSq);
            HandleAntistuck();
            _previousDistancesSquared.AddBack(nodeDistSq);

            //  Debug - add to Debug window instead
            //Service.Log.Debug($"{_previousDistancesSquared.ToString()}");

            if (nodeDistSq <= Tolerance)
            {
                Waypoints.RemoveAt(0);
            }

            if (Waypoints.Count > 1)
            {
                HandleNodeStaleness();
            }
        }

        UpdateMovementState();
    }

    private void HandlePlayerMovingAway(float nodeDistSq)
    {
        if (_previousDistancesSquared.IsEmpty) return;

        var lastDistSquared = _previousDistancesSquared.PeekBack();

        if (!_manager.PathfindInProgress &&
            nodeDistSq > lastDistSquared &&
            _bMoveRequestAllowed
            )
        {
            Service.Log.Debug($"[FollowPath] Detected Player moving away from Node requesting moveTo Fly:{Fly}");
            _asyncmove.MoveTo(Waypoints[Waypoints.Count - 1], Fly); // seems good but need 1 more check for ETA
            _lastMoveToRequestTime = DateTime.Now; // Update the time of the last move request
        }
    }

    private void HandleAntistuck()
    {
        if (_movement.Enabled &&
            _previousDistancesSquared.Count > 4 &&
            _bMoveRequestAllowed
            )
        {
            float minDistance = _previousDistancesSquared.Min;
            float maxDistance = _previousDistancesSquared.Max;
            float deltaDistance = maxDistance - minDistance;

            if (deltaDistance < 5)
            {
                Service.Log.Debug($"[FollowPath] Detected Player Stuck requesting moveTo Fly:{Fly} deltaDistance: {deltaDistance}");
                _asyncmove.MoveTo(Waypoints[Waypoints.Count - 1], Fly);
                _lastMoveToRequestTime = DateTime.Now;
            }
        }
    }

    private void HandleNodeStaleness()
    {
        var node = Waypoints[0];
        var nextNode = Waypoints[1];
        bool removeNode = false;

        var playerPos = _player.Position;

        if (!Fly)
        {
            node.Y = 0;
            playerPos.Y = 0;
            nextNode.Y = 0;
        }

        removeNode = NodeIsStale(playerPos, node, nextNode);

        if (removeNode)
        {
            Waypoints.RemoveAt(0);
            if (_bMoveRequestAllowed)
            {
                Service.Log.Debug($"[FollowPath] Detected & removed stale node, requesting moveTo Fly:{Fly}");
                _asyncmove.MoveTo(Waypoints[Waypoints.Count - 1], Fly);
            }
            _lastMoveToRequestTime = DateTime.Now;
        }
    }


    private void UpdateMovementState()
    {
        if (Waypoints.Count == 0)
        {
            _movement.Enabled = _camera.Enabled = false;
            _camera.SpeedH = _camera.SpeedV = default;
            _movement.DesiredPosition = _player.Position;
        }
        else
        {
            OverrideAFK.ResetTimers();
            _movement.Enabled = MovementAllowed;
            _movement.DesiredPosition = Waypoints[0];

            if (Fly &&
                _movement.DesiredPosition.Y > _player.Position.Y &&
                !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight])
            {
                if (Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted])
                {
                    ExecuteJump();
                }
                else
                {
                    _movement.Enabled = false;
                    return;
                }
            }

            _camera.Enabled = Service.Config.AlignCameraToMovement;
            _camera.SpeedH = _camera.SpeedV = 360.Degrees();
            _camera.DesiredAzimuth = Angle.FromDirectionXZ(_movement.DesiredPosition - _player.Position) + 180.Degrees();
            _camera.DesiredAltitude = -30.Degrees();
        }
    }


    private static bool NodeIsStale(Vector3 point, Vector3 currentNode, Vector3 nextNode)
    {
        // Calculate the vector along the line segment and the vector from startPoint to the point
        Vector3 segment = nextNode - currentNode;
        Vector3 toPoint = point - currentNode;

        // Project the point onto the line defined by the segment, https://mathinsight.org/dot_product#:~:text=applet
        //          <=0: projection falls before or at start of segment, the closest point is currentNode
        //           >0: projection falls on the segment, 0.15 == 15% of the way
        //           >1: projection falls after the end of the segment, the closest point is nextNode

        //  currentNode/nextNode are the same ? consider first node stale : stale if point is past currentNode
        return (segment.LengthSquared() == 0) ? true : Vector3.Dot(toPoint, segment) / segment.LengthSquared() > 0.15;
    }


    public void Stop()
    {
        Waypoints.Clear();
        _previousDistancesSquared.Clear();
    }


    private unsafe void ExecuteJump()
    {
        if (DateTime.Now >= _nextJump)
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
            _nextJump = DateTime.Now.AddMilliseconds(100);
        }
    }

    public void Move(List<Vector3> waypoints, bool fly)
    {
        Stop();
        Waypoints = waypoints;
        Fly = fly;
    }

    private void OnNavmeshChanged(Navmesh? navmesh, NavmeshQuery? query)
    {
        Stop();
    }

    private struct FixedSizeDeque<T>
    {
        private readonly T[] _array;
        private int _front;
        private int _back;
        private int _count;

        public FixedSizeDeque(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");

            _array = new T[capacity];
            _front = 0;
            _back = 0;
            _count = 0;
        }

        public void AddFront(T item)
        {
            if (_count == _array.Length)
                RemoveBack();

            _front = (_front - 1 + _array.Length) % _array.Length;
            _array[_front] = item;
            _count++;
        }

        public void AddBack(T item)
        {
            if (_count == _array.Length)
                RemoveFront();

            _array[_back] = item;
            _back = (_back + 1) % _array.Length;
            _count++;
        }

        public T RemoveFront()
        {
            if (_count == 0)
                throw new InvalidOperationException("Deque is empty.");

            var value = _array[_front];
            _front = (_front + 1) % _array.Length;
            _count--;
            return value;
        }

        public T RemoveBack()
        {
            if (_count == 0)
                throw new InvalidOperationException("Deque is empty.");

            _back = (_back - 1 + _array.Length) % _array.Length;
            var value = _array[_back];
            _count--;
            return value;
        }

        public T PeekBack()
        {
            if (_count == 0)
                throw new InvalidOperationException("Deque is empty.");

            var index = (_back - 1 + _array.Length) % _array.Length;
            return _array[index];
        }

        public T Peek(int index)
        {
            if (index < 0 || index >= _count)
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

            return _array[(_front + index) % _array.Length];
        }

        public void Clear()
        {
            _front = 0;
            _back = 0;
            _count = 0;
        }

        public bool IsEmpty => _count == 0;

        public int Count => _count;

        public T Max
        {
            get
            {
                if (IsEmpty) throw new InvalidOperationException("Deque is empty.");
                return _array.Take(_count).Max();
            }
        }

        public T Min
        {
            get
            {
                if (IsEmpty) throw new InvalidOperationException("Deque is empty.");
                return _array.Take(_count).Min();
            }
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            for (int i = 0; i < _count; i++)
            {
                builder.Append($"{Peek(i)}, ");
            }

            if (builder.Length > 0)
            {
                builder.Length -= 2;
            }

            return builder.ToString();
        }
    }
}
