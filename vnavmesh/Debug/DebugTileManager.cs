using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using System;

namespace Navmesh.Debug;

public sealed unsafe class DebugTileManager(TileManager tiles, DebugDrawer drawer, DebugGameCollision coll) : IDisposable
{
    private readonly TileManager _tiles = tiles;
    private readonly UITree _tree = new();
    private readonly DebugDrawer _dd = drawer;
    private readonly DebugGameCollision _coll = coll;

    private SceneTracker Scene => _tiles.Scene;

    public void Dispose() { }

    public void Draw()
    {
        using (var np = _tree.Node($"Objects ({Scene.LayoutObjects.Count})###bgparts", Scene.LayoutObjects.Count == 0))
        {
            if (np.Opened)
            {
                foreach (var (key, part) in Scene.LayoutObjects)
                {
                    var coll = FindCollider(InstanceType.BgPart, key);
                    using var n = _tree.Node($"[{key:X16}] {part.Transform.Row3} {part.Path}###{key:X}");
                    if (n.SelectedOrHovered)
                    {
                        _dd.DrawWorldAABB(part.Bounds, 0xff0000ff);
                        if (coll != null)
                            _coll.VisualizeCollider(coll, default, default);
                    }
                }
            }
        }

        /*
        using (var nc = _tree.Node($"Colliders ({_scene.Colliders.Count})##colliders", _scene.Colliders.Count == 0))
        {
            if (nc.Opened)
            {
                foreach (var (key, part) in _scene.Colliders)
                {
                    var collider = FindCollider(InstanceType.CollisionBox, key);
                    using var n = _tree.Node($"[{key:X16}] {part.Transform.Row3} {part.MaterialId:X}###{key:X}");
                    if (n.SelectedOrHovered)
                    {
                        _dd.DrawWorldAABB(part.Bounds, 0xff0000ff);
                        if (collider != null)
                            _coll.VisualizeCollider(collider, default, default);
                    }
                }
            }
        }
        */

        using (var nk = _tree.Node($"Tile caches##tilecache"))
        {
            if (nk.Opened)
            {
                for (var i = 0; i < Scene.NumTilesInRow; i++)
                {
                    for (var j = 0; j < Scene.NumTilesInRow; j++)
                    {
                        if (Scene.Tiles[i, j].Objects is not { } obj || obj.Count == 0)
                            continue;

                        double minStale = 2000.0;
                        double maxStale = 10000.0;

                        var staleness = -(Scene.Tiles[i, j].Timer - SceneTracker.DebounceMS);
                        var alpha = 0xff - (byte)(Math.Max(0, Math.Min(maxStale, staleness) - minStale) / (maxStale - minStale) * 0x80);

                        var color = (uint)alpha << 24 | 0xFFFFFF;

                        _tree.LeafNode($"[{i}x{j}] {Scene.Tiles[i, j].Objects.Count} ({staleness * 0.001f:f2}s old)##ck{i}", color);
                    }
                }
            }
        }
    }

    private unsafe Collider* FindCollider(InstanceType type, ulong key)
    {
        var layout = LayoutWorld.Instance()->ActiveLayout;
        var insts = layout != null ? LayoutUtils.FindPtr(ref layout->InstancesByType, type) : null;
        var inst = insts != null ? LayoutUtils.FindPtr(ref *insts, key) : null;
        var coll = inst != null ? inst->GetCollider() : null;
        return coll;
    }
}
