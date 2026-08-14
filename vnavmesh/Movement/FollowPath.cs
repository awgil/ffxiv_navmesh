using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.Movement;

public readonly record struct Waypoint(Vector3 Position, Navmesh.AreaId Type)
{
	public Waypoint(Vector3 Position) : this(Position, Navmesh.AreaId.Default) { }
}

public class FollowPath : IDisposable
{
	public bool MovementAllowed = true;
	public bool IgnoreDeltaY = false;
	public float Tolerance = 0.25f;
	public float DestinationTolerance = 0;
	public List<Waypoint> Waypoints = [];

	private IDalamudPluginInterface _dalamud;
	private NavmeshManager _manager;
	private OverrideCamera _camera = new();
	private OverrideMovement _movement = new();

	// exposed so the recorder can observe raw input without taking over movement (ADR 0003)
	public OverrideMovement Movement => _movement;
	private DateTime _nextJump;

	private Vector3? posPreviousFrame;
	private Angle? _steerHeading; // commanded heading, carried between frames so it can be rate limited
	private Vector2 _avoidSmoothed; // wall drift, averaged over time so a corridor does not make it weave

	// last steering decision, recorded per frame so a capture can show what the controller actually did
	public bool SteerRan { get; private set; }
	public bool SteerRecovering { get; private set; }
	public Angle SteerCommanded { get; private set; }
	public float SteerWallDist { get; private set; } = -1; // -1 when nothing is within clearance
	public float SteerWallPush { get; private set; }       // applied avoidance weight, 0 when it did not fire
	public float SteerAimDist { get; private set; }        // how far ahead it ended up aiming
	public int SteerWaypointsLeft { get; private set; }    // a short list means Follow can only offer the destination

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
		var player = Service.ObjectTable.LocalPlayer;
		if (player == null)
			return;

		while (Waypoints.Count > 0)
		{
			var (a, iid) = Waypoints[0];
			var b = player.Position;
			var c = posPreviousFrame ?? b;

			if (DestinationTolerance > 0 && (b - Waypoints[^1].Position).Length() <= DestinationTolerance)
			{
				Waypoints.Clear();
				break;
			}

			if (CheckCondition(iid, out var proceed))
			{
				if (proceed)
					Waypoints.RemoveAt(0);

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
					OnStuck?.Invoke(destination.Position, !IgnoreDeltaY, DestinationTolerance);
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
			_movement.DesiredPosition = SteeringTarget(player.Position, (float)fwk.UpdateDelta.TotalSeconds);
			if (_movement.DesiredPosition.Y > player.Position.Y && !Service.Condition[ConditionFlag.InFlight] && !Service.Condition[ConditionFlag.Diving] && !IgnoreDeltaY) //Only do this bit if on a flying path
			{
				// walk->fly transition (TODO: reconsider?)
				if (Service.Condition[ConditionFlag.Mounted])
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

	// upstream aims straight at Waypoints[0]; humanized steering aims along the path instead, see Human/PathSteering
	private Vector3 SteeringTarget(Vector3 playerPos, float dt)
	{
		var cfg = Service.Config.Humanizer;
		SteerRan = false;
		SteerRecovering = false;
		SteerWallDist = -1;
		SteerWallPush = 0;
		SteerAimDist = 0;
		SteerWaypointsLeft = 0;
		// walking only. everything below leans on navmesh queries, and on a flying path those report
		// the ground somewhere underneath rather than the air being flown through, so the clamp
		// shortens the aim for walls that are not in the way and the drift pushes away from geometry
		// that is not there. IgnoreDeltaY is set from !fly, so it is the path type, not the current
		// state, and steering cannot flicker on and off as the character takes off and lands.
		if (!cfg.SteeringEnabled || !IgnoreDeltaY)
			return Waypoints[0].Position;

		var follow = Human.PathSteering.Follow(Waypoints, playerPos, cfg.SteeringLookahead);
		// drop what the projection says is behind us; proximity popping never fires when corners are cut
		if (follow.Consumed > 0)
			Waypoints.RemoveRange(0, follow.Consumed);

		// aiming ahead cuts corners, which through a narrow gap means aiming into a wall. shorten the
		// lookahead along the path until the line to it is walkable, which keeps the aim on the route:
		// pulling it straight back toward the player instead would leave the path altogether.
		var target = follow.Target;
		if (cfg.SteeringClampToMesh && _manager.Query is { } query)
		{
			var lookahead = cfg.SteeringLookahead;
			while (lookahead > cfg.SteeringMinAimDistance && query.WalkableFraction(playerPos, target) < 1)
			{
				lookahead *= 0.5f;
				target = Human.PathSteering.Follow(Waypoints, playerPos, lookahead).Target;
			}
			// nothing along the path is reachable in a straight line, so follow the corridor closely.
			// keep it a short step along the path rather than the next waypoint, which can sit far away
			// in another direction and makes the aim jump between wildly different distances
			if (query.WalkableFraction(playerPos, target) < 1)
				target = Human.PathSteering.Follow(Waypoints, playerPos, cfg.SteeringMinAimDistance).Target;
		}

		var offset = target - playerPos;
		if (new Vector2(offset.X, offset.Z).LengthSquared() < 1e-6f)
			return follow.Target;

		// before the drift scales it, so this really is how far ahead the aim landed
		SteerAimDist = new Vector2(offset.X, offset.Z).Length();
		SteerWaypointsLeft = Waypoints.Count;

		// let the drift fade rather than vanish when the wall drops out of range
		if (_manager.Query is null || cfg.SteeringWallAvoidance <= 0
			|| _manager.Query.NearestWall(playerPos, cfg.SteeringWallClearance) is null)
			_avoidSmoothed *= MathF.Exp(-dt / MathF.Max(0.05f, cfg.SteeringWallSmoothing));

		// blend in a drift away from nearby geometry, so obstacles are given way to while still at a
		// distance instead of on contact
		if (cfg.SteeringWallAvoidance > 0 && cfg.SteeringWallClearance > 0 && _manager.Query is { } wallQuery
			&& wallQuery.NearestWall(playerPos, cfg.SteeringWallClearance) is { } wall)
		{
			var aim = new Vector2(offset.X, offset.Z);
			var len = aim.Length();
			if (len > 1e-4f)
			{
				var away = new Vector2(wall.away.X, wall.away.Z);
				if (away.LengthSquared() > 1e-6f)
				{
					var aimDir = aim / len;
					away = Vector2.Normalize(away);
					// nothing at the clearance edge, full push when right against it
					var urgency = 1 - wall.dist / cfg.SteeringWallClearance;
					var raw = away * (urgency * cfg.SteeringWallAvoidance);
					// average over time: the two sides of a corridor cancel and it runs down the middle,
					// where reacting to whichever edge is nearest right now makes it weave between them
					var alpha = cfg.SteeringWallSmoothing > 0
						? 1 - MathF.Exp(-dt / cfg.SteeringWallSmoothing)
						: 1;
					_avoidSmoothed += (raw - _avoidSmoothed) * alpha;
					SteerWallDist = wall.dist;
					SteerWallPush = _avoidSmoothed.Length();
					var blended = aimDir + _avoidSmoothed;
					if (blended.LengthSquared() > 1e-6f)
						offset = new Vector3(blended.X * len, offset.Y, blended.Y * len);
				}
			}
		}

		var desired = Angle.FromDirectionXZ(offset);
		var error = (desired - (_steerHeading ?? desired)).Normalized().Abs();

		// rate limiting is for easing through corners. applied to a reversal it makes the character
		// orbit, because the turn radius at walking speed exceeds the distance left to cover. so a
		// large correction, or the last stretch, raises the ceiling rather than removing it: an
		// unlimited snap there was the one remaining 500 deg/s spike.
		var recovering = error.Rad > MathF.PI / 2 || follow.ProjectedDistToEnd <= cfg.SteeringLookahead;
		var rate = (recovering ? cfg.SteeringRecoveryTurnRate : cfg.SteeringMaxTurnRate).Degrees();
		_steerHeading = _steerHeading is { } cur
			? Human.PathSteering.SlewHeading(cur, desired, rate, dt)
			: desired;
		SteerRan = true;
		SteerRecovering = recovering;
		SteerCommanded = _steerHeading.Value;

		// keep the aim point at the same planar distance, so arrival and the fly transition are unaffected
		var planar = new Vector2(offset.X, offset.Z).Length();
		var dir = _steerHeading.Value.ToDirectionXZ() * planar;
		return new Vector3(playerPos.X + dir.X, target.Y, playerPos.Z + dir.Z);
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

	private static bool CheckCondition(Navmesh.AreaId areaId, out bool proceed)
	{
		proceed = false;

		switch (areaId)
		{
			case Navmesh.AreaId.Warp:
				// TODO: teleport via aetheryte
				return false;
			case Navmesh.AreaId.ClientPath:
				// 61 is most dungeon clientpaths, 101 is cosmoliners
				proceed = Service.Condition.Any(ConditionFlag.Jumping61, ConditionFlag.Unknown101);
				return true;
			case Navmesh.AreaId.ClientPathEnd:
				// if we don't check the condition here, a path of two consecutive clientpaths will be terminated early (common in cosmic exploration)
				proceed = !Service.Condition.Any(ConditionFlag.Jumping61, ConditionFlag.Unknown101);
				return true;
			default:
				return false;
		}
	}

	public void Stop()
	{
		UpdateSharedState(false);
		_steerHeading = null;
		_avoidSmoothed = default;
		_millisecondsWithNoSignificantMovement = 0;
		Waypoints.Clear();
	}

	private unsafe void ExecuteJump()
	{
		// Unable to jump while diving, prevents spamming error messages.
		if (Service.Condition[ConditionFlag.Diving])
			return;

		if (DateTime.Now >= _nextJump)
		{
			ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
			_nextJump = DateTime.Now.AddMilliseconds(100);
		}
	}

	public void Move(List<Waypoint> waypoints, bool ignoreDeltaY, float destTolerance = 0)
	{
		UpdateSharedState(true);
		_steerHeading = null;
		_avoidSmoothed = default;
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
