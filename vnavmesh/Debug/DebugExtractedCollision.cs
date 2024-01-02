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
    // TODO: cache visualization

    public DebugExtractedCollision(CollisionGeometryExtractor geometry, UITree tree, DebugDrawer dd)
    {
        _geom = geometry;
        _tree = tree;
        _dd = dd;
    }

    public void Dispose()
    {
    }

    public void Draw()
    {
        using var nr = _tree.Node("Extracted geometry");
        if (nr.SelectedOrHovered)
            Visualize();
        if (!nr.Opened)
            return;

        _tree.LeafNode($"Bounds: {_geom.BoundsMin:f3} - {_geom.BoundsMax:f3}");
        foreach (var (name, mesh) in _geom.Meshes)
        {
            using var nm = _tree.Node($"{name}: flags={mesh.Flags}");
            if (nm.SelectedOrHovered)
                VisualizeMeshInstances(mesh);
            if (!nm.Opened)
                continue;

            using (var np = _tree.Node($"Parts ({mesh.Parts.Count})###parts", mesh.Parts.Count == 0))
            {
                if (np.Opened)
                {
                    int i = 0;
                    foreach (var p in mesh.Parts)
                    {
                        using var npi = _tree.Node(i++.ToString());
                        if (npi.SelectedOrHovered)
                            VisualizeMeshPart(mesh, p);
                        if (!npi.Opened)
                            continue;

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
                }
            }

            using (var ni = _tree.Node($"Instances ({mesh.Instances.Count})###instances", mesh.Instances.Count == 0))
            {
                if (ni.Opened)
                {
                    int j = 0;
                    foreach (var w in mesh.Instances)
                        if (_tree.LeafNode($"{j++}: R0 = {w.Row0:f3}, R1 = {w.Row1:f3}, R2 = {w.Row2:f3}, R3 = {w.Row3:f3}").SelectedOrHovered)
                            VisualizeMeshInstance(mesh, w);
                }
            }
        }
    }

    private void Visualize()
    {
        foreach (var mesh in _geom.Meshes.Values)
            VisualizeMeshInstances(mesh);
    }

    private void VisualizeMeshInstances(CollisionGeometryExtractor.Mesh mesh)
    {
        foreach (var p in mesh.Parts)
            VisualizeMeshPart(mesh, p);
    }

    private void VisualizeMeshPart(CollisionGeometryExtractor.Mesh mesh, CollisionGeometryExtractor.MeshPart part)
    {
        var imesh = new ExtractedMesh(part);
        var color = MeshColor(mesh);
        _dd.DrawMeshes(imesh, mesh.Instances.Select(w => new Render.EffectMesh.Instance(w, color)));
    }

    private void VisualizeMeshInstance(CollisionGeometryExtractor.Mesh mesh, Matrix4x3 world)
    {
        var color = MeshColor(mesh);
        foreach (var p in mesh.Parts)
            _dd.DrawMesh(new ExtractedMesh(p), ref world, color);
    }

    private void VisualizeVertex(CollisionGeometryExtractor.Mesh mesh, Vector3 v)
    {
        foreach (var i in mesh.Instances)
            _dd.DrawWorldPoint(i.TransformCoordinate(v), 5, 0xff0000ff);
    }

    private void VisualizeTriangle(CollisionGeometryExtractor.Mesh mesh, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        foreach (var i in mesh.Instances)
            _dd.DrawWorldTriangle(i.TransformCoordinate(v1), i.TransformCoordinate(v2), i.TransformCoordinate(v3), 0xff0000ff);
    }

    private Vector4 MeshColor(CollisionGeometryExtractor.Mesh mesh) =>
        mesh.Flags.HasFlag(CollisionGeometryExtractor.Flags.FromStreamed) ? new(0, 1, 0, 1) :
        mesh.Flags.HasFlag(CollisionGeometryExtractor.Flags.FromFileMesh) ? new(1, 1, 0, 1) :
        new(1, 0, 0, 1);
}
