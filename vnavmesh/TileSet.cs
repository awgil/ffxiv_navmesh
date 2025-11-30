using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using System.Threading;

namespace Navmesh;

// sorts and groups change events from LayoutObjectSet
public sealed class TileSet : Subscribable<TileSet.TileChangeArgs>
{
    public record struct TileChangeArgs(uint Territory, int X, int Z);

    private readonly Dictionary<ulong, InstanceWithMesh>[,] _tiles = new Dictionary<ulong, InstanceWithMesh>[16, 16];

    private readonly Lock _lock = new();

    public IObservable<IList<Tile>> BatchedWithTimeout(TimeSpan delay, TimeSpan timeout) => this
        .GroupByUntil(_ => new System.Reactive.Unit(), g => g.Throttle(delay).Timeout(timeout))
        .SelectMany(x => x.ToList())
        .Select(result =>
        {
            // pause grid modifications while building sorted collections, which takes a few ms; if any of the layout objects are modified in this window, it will trigger an InvalidOperationException
            lock (_lock)
            {
                return result.DistinctBy(t => (t.X, t.Z)).Select(t =>
                    new Tile(
                        t.X,
                        t.Z,
                        new SortedDictionary<ulong, InstanceWithMesh>(_tiles[t.X, t.Z].ToDictionary(k => k.Key, k => k.Value with { Instance = k.Value.Instance.Clone() })),
                        NavmeshCustomizationRegistry.ForTerritory(t.Territory),
                        t.Territory
                    )).ToList();
            }
        });

    private IDisposable? _sceneSubscription;

    protected override void OnUnsubscribeAll()
    {
        _sceneSubscription?.Dispose();
    }

    public void Watch(LayoutObjectSet set)
    {
        _sceneSubscription = set.Subscribe(Apply);
        set.TerritoryChanged += Clear;
    }

    private const int TileUnits = 128;
    private const int RowLength = 16;

    public LayoutObjectSet.InstanceChangeArgs? LastEvent;

    private void Apply(LayoutObjectSet.InstanceChangeArgs change)
    {
        HashSet<(int, int)> modified = [];

        lock (_lock)
        {
            LastEvent = change;

            // remove all existing copies of instance, in case previous transform occupied different set of tiles
            modified.UnionWith(Remove(change.Key));

            if (change.Instance != null)
            {
                var min = change.Instance.Instance.WorldBounds.Min - new Vector3(-1024);
                var max = change.Instance.Instance.WorldBounds.Max - new Vector3(-1024);

                var imin = (int)min.X / TileUnits;
                var imax = (int)max.X / TileUnits;
                var jmin = (int)min.Z / TileUnits;
                var jmax = (int)max.Z / TileUnits;

                // TODO: objects that are entirely outside the accessible zone should probably be filtered earlier, we might be able to use BoundingSphereImpl or something
                if (imax < 0 || jmax < 0 || imin > 15 || jmin > 15)
                {
                    //Service.Log.Warning($"object {change.Instance} is outside world bounds, which will break the cache");
                    return;
                }

                for (var i = Math.Max(0, imin); i <= Math.Min(imax, RowLength - 1); i++)
                    for (var j = Math.Max(0, jmin); j <= Math.Min(jmax, RowLength - 1); j++)
                    {
                        _tiles[i, j] ??= [];
                        _tiles[i, j][change.Instance.Instance.Id] = change.Instance;
                        modified.Add((i, j));
                    }
            }

            foreach (var (x, z) in modified)
                Notify(new(change.Zone, x, z));
        }
    }

    private HashSet<(int, int)> Remove(ulong key)
    {
        var changes = new HashSet<(int, int)>();

        for (var i = 0; i < _tiles.GetLength(0); i++)
            for (var j = 0; j < _tiles.GetLength(1); j++)
                if (_tiles[i, j]?.Remove(key) == true)
                    changes.Add((i, j));
        return changes;
    }

    private void Clear(LayoutObjectSet cs)
    {
        lock (_lock)
        {
            foreach (var t in _tiles)
                t?.Clear();
        }
    }

    public void Reload(uint zone, int x, int z)
    {
        Notify(new(zone, x, z));
    }
}
