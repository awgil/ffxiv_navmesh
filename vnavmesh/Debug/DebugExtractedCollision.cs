using Navmesh.Render;
using System;
using System.Linq;
using System.Numerics;
using Matrix4x3 = FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3;

namespace Navmesh.Debug;

public class DebugExtractedCollision : IDisposable
{
    private CollisionGeometryExtractor _geom;
    private UITree _tree;
    private DebugDrawer _dd;
    private EffectMesh.Data? _visu;

    public DebugExtractedCollision(CollisionGeometryExtractor geometry, UITree tree, DebugDrawer dd)
    {
        _geom = geometry;
        _tree = tree;
        _dd = dd;
    }

    public void Dispose()
    {
        _visu?.Dispose();
    }

    public void Draw()
    {
        using var nr = _tree.Node("Extracted geometry");
        if (nr.SelectedOrHovered)
            Visualize();
        if (!nr.Opened)
            return;

        _tree.LeafNode($"Bounds: {_geom.BoundsMin:f3} - {_geom.BoundsMax:f3}");
        int meshIndex = 0;
        foreach (var (name, mesh) in _geom.Meshes)
        {
            using var nm = _tree.Node($"{name}: flags={mesh.Flags}");
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
                                            var v1 = p.Vertices[t.v1];
                                            var v2 = p.Vertices[t.v2];
                                            var v3 = p.Vertices[t.v3];
                                            if (_tree.LeafNode($"{j++}: {t.v1}x{t.v2}x{t.v3} ({v1:f3} x {v2:f3} x {v3:f3})").SelectedOrHovered)
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
                            if (_tree.LeafNode($"{instIndex}: R0 = {i.WorldTransform.Row0:f3}, R1 = {i.WorldTransform.Row1:f3}, R2 = {i.WorldTransform.Row2:f3}, R3 = {i.WorldTransform.Row3:f3}, {i.WorldBounds.Min:f3} - {i.WorldBounds.Max:f3}").SelectedOrHovered)
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
            foreach (var mesh in _geom.Meshes.Values)
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
            foreach (var mesh in _geom.Meshes.Values)
            {
                var color = MeshColor(mesh);
                int nvm = 0, npm = 0;
                foreach (var part in mesh.Parts)
                {
                    foreach (var v in part.Vertices)
                        builder.AddVertex(v);
                    foreach (var p in part.Primitives)
                        builder.AddTriangle(nvm + p.v1, nvm + p.v3, nvm + p.v2); // dx winding
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
        _dd.EffectMesh.Draw(_dd.RenderContext, GetOrInitVisualizer());
    }

    private void VisualizeMeshInstances(int meshIndex)
    {
        _dd.EffectMesh.DrawSingle(_dd.RenderContext, GetOrInitVisualizer(), meshIndex);
    }

    private void VisualizeMeshPart(CollisionGeometryExtractor.Mesh mesh, int meshIndex, int partIndex)
    {
        var visu = GetOrInitVisualizer();
        var visuMesh = visu.Meshes[meshIndex];
        visuMesh.FirstPrimitive += mesh.Parts.Take(partIndex).Sum(part => part.Primitives.Count);
        visuMesh.NumPrimitives = mesh.Parts[partIndex].Primitives.Count;
        _dd.EffectMesh.Bind(_dd.RenderContext, false);
        visu.Bind(_dd.RenderContext);
        visu.DrawManual(_dd.RenderContext, visuMesh);
    }

    private void VisualizeMeshInstance(int meshIndex, int instIndex)
    {
        var visu = GetOrInitVisualizer();
        var visuMesh = visu.Meshes[meshIndex];
        visuMesh.FirstInstance += instIndex;
        visuMesh.NumInstances = 1;
        _dd.EffectMesh.Bind(_dd.RenderContext, false);
        visu.Bind(_dd.RenderContext);
        visu.DrawManual(_dd.RenderContext, visuMesh);
    }

    private void VisualizeVertex(CollisionGeometryExtractor.Mesh mesh, Vector3 v)
    {
        foreach (var i in mesh.Instances)
            _dd.DrawWorldPoint(i.WorldTransform.TransformCoordinate(v), 5, 0xff0000ff);
    }

    private void VisualizeTriangle(CollisionGeometryExtractor.Mesh mesh, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        foreach (var i in mesh.Instances)
            _dd.DrawWorldTriangle(i.WorldTransform.TransformCoordinate(v1), i.WorldTransform.TransformCoordinate(v2), i.WorldTransform.TransformCoordinate(v3), 0xff0000ff);
    }

    private Vector4 MeshColor(CollisionGeometryExtractor.Mesh mesh) =>
        mesh.Flags.HasFlag(CollisionGeometryExtractor.Flags.FromStreamed) ? new(0, 1, 0, 0.75f) :
        mesh.Flags.HasFlag(CollisionGeometryExtractor.Flags.FromFileMesh) ? new(1, 1, 0, 0.75f) :
        new(1, 0, 0, 0.75f);
}
