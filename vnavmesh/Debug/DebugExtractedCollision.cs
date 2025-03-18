﻿using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using ImGuiNET;
using Navmesh.Render;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Navmesh.Debug;

public class DebugExtractedCollision : IDisposable
{
    private SceneDefinition _scene;
    private SceneExtractor _extractor;
    private UITree _tree;
    private DebugDrawer _dd;
    private DebugGameCollision _coll;
    private EffectMesh.Data? _visu;
    private string _configDirectory;

    public DebugExtractedCollision(SceneDefinition scene, SceneExtractor extractor, UITree tree, DebugDrawer dd, DebugGameCollision coll, string configDir)
    {
        _scene = scene;
        _extractor = extractor;
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
        DrawExtractor();
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
    }

    private void DrawTransform(string tag, Transform transform)
    {
        _tree.LeafNode($"{tag} position: {transform.Translation}");
        _tree.LeafNode($"{tag} rotation: {transform.Rotation}");
        _tree.LeafNode($"{tag} scale: {transform.Scale}");
    }

    private void DrawExtractor()
    {
        using var nr = _tree.Node("Extracted geometry");
        if (nr.SelectedOrHovered)
            Visualize();
        if (!nr.Opened)
            return;

        if (ImGui.Button("Export to DotRecast obj file"))
            ExportMesh();

        int meshIndex = 0;
        foreach (var (name, mesh) in _extractor.Meshes)
        {
            using var nm = _tree.Node($"{name}: flags={mesh.MeshType}");
            if (nm.SelectedOrHovered)
                VisualizeMeshInstances(meshIndex);

            if (nm.Opened)
            {
                using (var np = _tree.Node($"Parts ({mesh.Parts.Count})###parts", mesh.Parts.Count == 0))
                {
                    if (np.Opened)
                    {
                        int partIndex = 0;
                        foreach (var p in mesh.Parts)
                        {
                            using var npi = _tree.Node(partIndex.ToString());
                            if (npi.SelectedOrHovered)
                                VisualizeMeshPart(mesh, meshIndex, partIndex);

                            if (npi.Opened)
                            {
                                using (var nv = _tree.Node($"Vertices ({p.Vertices.Count})###verts"))
                                {
                                    if (nv.Opened)
                                    {
                                        int j = 0;
                                        foreach (var v in p.Vertices)
                                            if (_tree.LeafNode($"{j++}: {v:f3}").SelectedOrHovered)
                                                VisualizeVertex(mesh, v);
                                    }
                                }

                                using (var nt = _tree.Node($"Primitives ({p.Primitives.Count})###prims"))
                                {
                                    if (nt.Opened)
                                    {
                                        int j = 0;
                                        foreach (var t in p.Primitives)
                                        {
                                            var v1 = p.Vertices[t.V1];
                                            var v2 = p.Vertices[t.V2];
                                            var v3 = p.Vertices[t.V3];
                                            if (_tree.LeafNode($"{j++}: {t.V1}x{t.V2}x{t.V3} ({v1:f3} x {v2:f3} x {v3:f3}) ({t.Flags})").SelectedOrHovered)
                                                VisualizeTriangle(mesh, v1, v2, v3);
                                        }
                                    }
                                }
                            }

                            ++partIndex;
                        }
                    }
                }

                using (var ni = _tree.Node($"Instances ({mesh.Instances.Count})###instances", mesh.Instances.Count == 0))
                {
                    if (ni.Opened)
                    {
                        int instIndex = 0;
                        foreach (var i in mesh.Instances)
                        {
                            if (_tree.LeafNode($"{instIndex}: {i.WorldBounds.Min:f3}-{i.WorldBounds.Max:f3}, R0 = {i.WorldTransform.Row0:f3}, R1 = {i.WorldTransform.Row1:f3}, R2 = {i.WorldTransform.Row2:f3}, R3 = {i.WorldTransform.Row3:f3}, {i.WorldBounds.Min:f3} - {i.WorldBounds.Max:f3} (+: {i.ForceSetPrimFlags}, -: {i.ForceClearPrimFlags})").SelectedOrHovered)
                                VisualizeMeshInstance(meshIndex, instIndex);
                            ++instIndex;
                        }
                    }
                }
            }

            ++meshIndex;
        }
    }

    private EffectMesh.Data GetOrInitVisualizer()
    {
        if (_visu == null)
        {
            int nv = 0, np = 0, ni = 0;
            foreach (var mesh in _extractor.Meshes.Values)
            {
                foreach (var part in mesh.Parts)
                {
                    nv += part.Vertices.Count;
                    np += part.Primitives.Count;
                }
                ni += mesh.Instances.Count;
            }

            _visu = new(_dd.RenderContext, nv, np, ni, false);
            using var builder = _visu.Map(_dd.RenderContext);

            var timer = Timer.Create();
            nv = np = ni = 0;
            foreach (var mesh in _extractor.Meshes.Values)
            {
                var color = MeshColor(mesh);
                int nvm = 0, npm = 0;
                foreach (var part in mesh.Parts)
                {
                    foreach (var v in part.Vertices)
                        builder.AddVertex(v);
                    foreach (var p in part.Primitives)
                        builder.AddTriangle(nvm + p.V1, nvm + p.V3, nvm + p.V2); // dx winding
                    nvm += part.Vertices.Count;
                    npm += part.Primitives.Count;
                }
                foreach (var inst in mesh.Instances)
                {
                    builder.AddInstance(new(inst.WorldTransform, color));
                }
                builder.AddMesh(nv, np, npm, ni, mesh.Instances.Count);
                nv += nvm;
                np += npm;
                ni += mesh.Instances.Count;
            }
            Service.Log.Debug($"mesh visualization build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return _visu;
    }

    private void Visualize()
    {
        _dd.EffectMesh?.Draw(_dd.RenderContext, GetOrInitVisualizer());
    }

    private void VisualizeMeshInstances(int meshIndex)
    {
        _dd.EffectMesh?.DrawSingle(_dd.RenderContext, GetOrInitVisualizer(), meshIndex);
    }

    private void VisualizeMeshPart(SceneExtractor.Mesh mesh, int meshIndex, int partIndex)
    {
        if (_dd.EffectMesh == null)
            return;
        var visu = GetOrInitVisualizer();
        var visuMesh = visu.Meshes[meshIndex];
        visuMesh.FirstPrimitive += mesh.Parts.Take(partIndex).Sum(part => part.Primitives.Count);
        visuMesh.NumPrimitives = mesh.Parts[partIndex].Primitives.Count;
        _dd.EffectMesh.Bind(_dd.RenderContext, false, false);
        visu.Bind(_dd.RenderContext);
        visu.DrawManual(_dd.RenderContext, visuMesh);
    }

    private void VisualizeMeshInstance(int meshIndex, int instIndex)
    {
        if (_dd.EffectMesh == null)
            return;
        var visu = GetOrInitVisualizer();
        var visuMesh = visu.Meshes[meshIndex];
        visuMesh.FirstInstance += instIndex;
        visuMesh.NumInstances = 1;
        _dd.EffectMesh.Bind(_dd.RenderContext, false, false);
        visu.Bind(_dd.RenderContext);
        visu.DrawManual(_dd.RenderContext, visuMesh);
    }

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

    private void ExportMesh()
    {
        var key = NavmeshManager.GetCacheKey(_scene);
        var outFile = new FileInfo($"{_configDirectory}/export/{key}.obj");

        var verts = new List<Vector3>();
        var polys = new List<SceneExtractor.Primitive>();

        foreach (var mesh in _extractor.Meshes.Values)
        {

            foreach (var inst in mesh.Instances)
            {
                var transform = inst.WorldTransform;

                foreach (var part in mesh.Parts)
                {
                    var voff = verts.Count;

                    verts.AddRange(part.Vertices.Select(transform.TransformCoordinate));
                    polys.AddRange(part.Primitives.Select(p => new SceneExtractor.Primitive(p.V1 + voff, p.V2 + voff, p.V3 + voff, p.Flags)));
                }
            }
        }

        using var stream = outFile.OpenWrite();
        using var writer = new StreamWriter(stream);

        foreach (var v in verts)
            writer.WriteLine($"v {v.X:f6} {v.Y:f6} {v.Z:f6}");

        foreach (var p in polys)
            writer.WriteLine($"f {p.V1 + 1} {p.V2 + 1} {p.V3 + 1}");

        Service.Log.Debug($"wrote DotRecast geom file to {outFile}");
    }
}
