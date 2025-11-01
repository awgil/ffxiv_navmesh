using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Navmesh.Render;
using System;
using System.Numerics;

namespace Navmesh.Debug;

public class DebugExtractedCollision : IDisposable
{
    private SceneTracker _scene;
    private UITree _tree;
    private DebugDrawer _dd;
    private DebugGameCollision _coll;
    private EffectMesh.Data? _visu;
    private string _configDirectory;

    public DebugExtractedCollision(SceneTracker scene, UITree tree, DebugDrawer dd, DebugGameCollision coll, string configDir)
    {
        _scene = scene;
        _tree = tree;
        _dd = dd;
        _coll = coll;
        _configDirectory = configDir;
    }

    public void Dispose()
    {
        _visu?.Dispose();
    }

    public void Draw()
    {
        DrawDefinition();
    }

    private unsafe void DrawDefinition()
    {
        using var nr = _tree.Node("Scene definition");
        if (!nr.Opened)
            return;

        using (var nt = _tree.Node($"Terrain ({_scene.Terrains.Count})###terrain", _scene.Terrains.Count == 0))
        {
            if (nt.Opened)
            {
                foreach (var t in _scene.Terrains)
                {
                    _tree.LeafNode(t);
                }
            }
        }

        using (var np = _tree.Node($"BGParts ({_scene.BgParts.Count})###bgparts", _scene.BgParts.Count == 0))
        {
            if (np.Opened)
            {
                foreach (var (key, part) in _scene.BgParts)
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

        using (var nk = _tree.Node($"Tile caches##tilecache"))
        {
            if (nk.Opened)
            {
                for (var i = 0; i < _scene.NumTilesInRow; i++)
                {
                    for (var j = 0; j < _scene.NumTilesInRow; j++)
                    {
                        if (_scene[i, j].Count == 0)
                            continue;

                        double minStale = 2000.0;
                        double maxStale = 10000.0;

                        var staleness = -(_scene.Timers[i, j] - SceneTracker.DebounceMS);
                        var alpha = 0xff - (byte)(Math.Max(0, Math.Min(maxStale, staleness) - minStale) / (maxStale - minStale) * 0x80);

                        var color = (uint)alpha << 24 | 0xFFFFFF;

                        _tree.LeafNode($"[{i}x{j}] {_scene[i, j].Count} ({staleness * 0.001f:f2}s old)##ck{i}", color);
                    }
                }
            }
        }

        /*
        using (var np = _tree.Node($"BGParts ({_scene.BgParts.Count})###bgparts", _scene.BgParts.Count == 0))
        {
            if (np.Opened)
            {
                foreach (var p in _scene.BgParts)
                {
                    var coll = FindCollider(InstanceType.BgPart, p.key);
                    (Transform transform, Vector3 bbMin, Vector3 bbMax) shape = default;
                    var haveShape = p.analytic && _scene.AnalyticShapes.TryGetValue(p.crc, out shape);
                    var color = haveShape && (Math.Abs(shape.transform.Translation.X) > 0.1 || Math.Abs(shape.transform.Translation.Y) > 0.1 || Math.Abs(shape.transform.Translation.Z) > 0.1 || shape.transform.Rotation.W < 0.99) ? 0xff00ffff : 0xffffffff;
                    var type = p.analytic ? $"{(haveShape ? ((FileLayerGroupAnalyticCollider.Type)shape.transform.Type).ToString() : "<missing>")}" : $"Mesh {_scene.MeshPaths[p.crc]}";
                    using var n = _tree.Node($"[{p.key:X}] {type} at {p.transform.Translation} ({p.crc:X}) (coll={(nint)coll:X})###{p.key:X}", false, color);
                    if (n.SelectedOrHovered)
                    {
                        var info = _extractor.ExtractBgPartInfo(_scene, p.key, p.transform, p.crc, p.analytic);
                        _dd.DrawWorldAABB(info.bounds, 0xff0000ff);
                        if (coll != null)
                            _coll.VisualizeCollider(coll, default, default);
                    }
                    if (n.Opened)
                    {
                        DrawTransform("Part", p.transform);
                        if (haveShape)
                        {
                            DrawTransform("Shape", shape.transform);
                            _tree.LeafNode($"Shape min: {shape.bbMin}");
                            _tree.LeafNode($"Shape max: {shape.bbMax}");
                        }
                    }
                }
            }
        }

        using (var nc = _tree.Node($"Colliders ({_scene.Colliders.Count})###coll", _scene.Colliders.Count == 0))
        {
            if (nc.Opened)
            {
                foreach (var c in _scene.Colliders)
                {
                    var coll = FindCollider(InstanceType.CollisionBox, c.key);
                    var n = _tree.Node($"[{c.key:X}] {c.type}{(c.type == FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Mesh ? $" '{_scene.MeshPaths[c.crc]}'" : "")} at {c.transform.Translation} ({c.crc:X}) (coll={(nint)coll:X})###{c.key:X}");
                    if (n.SelectedOrHovered)
                    {
                        var info = _extractor.ExtractColliderInfo(_scene, c.key, c.transform, c.crc, c.type);
                        _dd.DrawWorldAABB(info.bounds, 0xff0000ff);
                        if (coll != null)
                            _coll.VisualizeCollider(coll, default, default);
                    }
                    if (n.Opened)
                    {
                        DrawTransform("Part", c.transform);
                    }
                }
            }
        }
        */
    }

    private void DrawTransform(string tag, Transform transform)
    {
        _tree.LeafNode($"{tag} position: {transform.Translation}");
        _tree.LeafNode($"{tag} rotation: {transform.Rotation}");
        _tree.LeafNode($"{tag} scale: {transform.Scale}");
    }

    private string _meshFilter = "";

    private void VisualizeVertex(SceneExtractor.Mesh mesh, Vector3 v)
    {
        foreach (var i in mesh.Instances)
            _dd.DrawWorldPoint(i.WorldTransform.TransformCoordinate(v), 5, 0xff0000ff);
    }

    private void VisualizeTriangle(SceneExtractor.Mesh mesh, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        foreach (var i in mesh.Instances)
            _dd.DrawWorldTriangle(i.WorldTransform.TransformCoordinate(v1), i.WorldTransform.TransformCoordinate(v2), i.WorldTransform.TransformCoordinate(v3), 0xff0000ff);
    }

    private unsafe Collider* FindCollider(InstanceType type, ulong key)
    {
        var layout = LayoutWorld.Instance()->ActiveLayout;
        var insts = layout != null ? LayoutUtils.FindPtr(ref layout->InstancesByType, type) : null;
        var inst = insts != null ? LayoutUtils.FindPtr(ref *insts, key) : null;
        var coll = inst != null ? inst->GetCollider() : null;
        return coll;
    }

    private Vector4 MeshColor(SceneExtractor.Mesh mesh) =>
        mesh.MeshType.HasFlag(SceneExtractor.MeshType.Terrain) ? new(0, 1, 0, 0.55f) :
        mesh.MeshType.HasFlag(SceneExtractor.MeshType.FileMesh) ? new(1, 1, 0, 0.55f) :
        new(1, 0, 0, 0.55f);
}
