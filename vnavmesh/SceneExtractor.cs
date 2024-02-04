using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

// extract geometry from scene definition; does not interact with game state, so safe to run in background
public class SceneExtractor
{
    [Flags]
    public enum Flags
    {
        None = 0,
        FromTerrain = 1 << 0,
        FromFileMesh = 1 << 1,
        FromCylinderMesh = 1 << 2,
        FromAnalyticShape = 1 << 3,
    }

    public class MeshPart
    {
        public List<Vector3> Vertices = new();
        public List<(int v1, int v2, int v3)> Primitives = new();
    }

    public record struct MeshInstance(ulong Id, Matrix4x3 WorldTransform, AABB WorldBounds);

    public class Mesh
    {
        public List<MeshPart> Parts = new();
        public List<MeshInstance> Instances = new();
        public Flags Flags;
    }

    public Dictionary<string, Mesh> Meshes { get; private set; } = new();
    public Vector3 BoundsMin { get; private set; } = new(float.MaxValue);
    public Vector3 BoundsMax { get; private set; } = new(float.MinValue);

    private const string _keyAnalyticBox = "<box>";
    private const string _keyAnalyticSphere = "<sphere>";
    private const string _keyAnalyticCylinder = "<cylinder>";
    private const string _keyMeshCylinder = "<mesh cylinder>";

    private static Mesh _meshBox;
    private static Mesh _meshSphere;
    private static Mesh _meshCylinder;

    static SceneExtractor()
    {
        _meshBox = BuildBoxMesh();
        _meshSphere = BuildSphereMesh(16);
        _meshCylinder = BuildCylinderMesh(16);
    }

    public unsafe SceneExtractor(SceneDefinition scene)
    {
        Meshes[_keyAnalyticBox] = _meshBox;
        Meshes[_keyAnalyticSphere] = _meshSphere;
        Meshes[_keyAnalyticCylinder] = _meshCylinder;
        Meshes[_keyMeshCylinder] = new() { Parts = _meshCylinder.Parts, Flags = Flags.FromCylinderMesh };
        foreach (var path in scene.MeshPaths.Values)
            AddMesh(path);

        foreach (var terr in scene.Terrains)
        {
            var list = Service.DataManager.GetFile(terr + "/list.pcb");
            if (list != null)
            {
                fixed (byte* data = &list.Data[0])
                {
                    var header = (ColliderStreamed.FileHeader*)data;
                    AddBounds(ref header->Bounds);
                    foreach (ref var entry in new Span<ColliderStreamed.FileEntry>(header + 1, header->NumMeshes))
                    {
                        var mesh = AddMesh($"{terr}/tr{entry.MeshId:d4}.pcb");
                        mesh.Flags |= Flags.FromTerrain;
                        AddInstance(mesh, 0, ref Matrix4x3.Identity, ref entry.Bounds);
                    }
                }
            }
        }

        foreach (var part in scene.BgParts)
        {
            var info = ExtractBgPartInfo(scene, part.key, part.transform, part.crc, part.analytic);
            if (info.path.Length > 0)
                AddInstance(Meshes[info.path], part.key, ref info.transform, ref info.bounds);
        }

        foreach (var coll in scene.Colliders)
        {
            var info = ExtractColliderInfo(scene, coll.key, coll.transform, coll.crc, coll.type);
            if (info.path.Length > 0)
                AddInstance(Meshes[info.path], coll.key, ref info.transform, ref info.bounds);
        }
    }

    public (string path, Matrix4x3 transform, AABB bounds) ExtractBgPartInfo(SceneDefinition scene, ulong key, Transform instanceTransform, uint crc, bool analytic)
    {
        if (analytic)
        {
            if (scene.AnalyticShapes.TryGetValue(crc, out var shape))
            {
                // see Client::LayoutEngine::Layer::BgPartsLayoutInstance_calculateSRT
                // S1*T1 * S*R*T = (S1*S*R,    0
                //                  T1*SR + T, 1)
                var mtxBounds = Matrix4x4.CreateScale((shape.bbMax - shape.bbMin) * 0.5f);
                mtxBounds.Translation = (shape.bbMin + shape.bbMax) * 0.5f;
                var fullTransform = mtxBounds * shape.transform.Compose() * instanceTransform.Compose();
                var resultingTransform = new Matrix4x3(fullTransform);
                var (path, bounds) = (FileLayerGroupAnalyticCollider.Type)shape.transform.Type switch
                {
                    FileLayerGroupAnalyticCollider.Type.Box => (_keyAnalyticBox, CalculateBoxBounds(ref resultingTransform)),
                    FileLayerGroupAnalyticCollider.Type.Sphere => (_keyAnalyticSphere, CalculateSphereBounds(key, ref resultingTransform)),
                    FileLayerGroupAnalyticCollider.Type.Cylinder => (_keyMeshCylinder, CalculateBoxBounds(ref resultingTransform)), // TODO: we can probably do a tighter fit for cylinders...
                    _ => ("", default)
                };
                return (path, resultingTransform, bounds);
            }
            return ("", Matrix4x3.Identity, default);
        }
        else
        {
            var path = scene.MeshPaths[crc];
            var transform = new Matrix4x3(instanceTransform.Compose());
            var bounds = CalculateMeshBounds(Meshes[path], ref transform);
            return (path, transform, bounds);
        }
    }

    public (string path, Matrix4x3 transform, AABB bounds) ExtractColliderInfo(SceneDefinition scene, ulong key, Transform instanceTransform, uint crc, FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType type)
    {
        var transform = new Matrix4x3(instanceTransform.Compose());
        var (path, bounds) = type switch
        {
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Box => (_keyAnalyticBox, CalculateBoxBounds(ref transform)),
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Sphere => (_keyAnalyticSphere, CalculateSphereBounds(key, ref transform)),
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Cylinder => (_keyAnalyticCylinder, CalculateBoxBounds(ref transform)),
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Mesh => (scene.MeshPaths[crc], CalculateMeshBounds(Meshes[scene.MeshPaths[crc]], ref transform)),
            _ => ("", default)
        };
        return (path, transform, bounds);
    }

    private unsafe Mesh AddMesh(string path)
    {
        var mesh = new Mesh();
        var f = Service.DataManager.GetFile(path);
        if (f != null)
        {
            fixed (byte* rawData = &f.Data[0])
            {
                var data = (MeshPCB.FileHeader*)rawData;
                if (data->Version is 1 or 4)
                {
                    FillFromFileNode(mesh.Parts, (MeshPCB.FileNode*)(data + 1));
                }
            }
        }
        mesh.Flags = Flags.FromFileMesh;
        Meshes[path] = mesh;
        return mesh;
    }

    private void AddInstance(Mesh mesh, ulong id, ref Matrix4x3 worldTransform, ref AABB worldBounds)
    {
        mesh.Instances.Add(new(id, worldTransform, worldBounds));
        AddBounds(ref worldBounds);
    }

    private static AABB CalculateBoxBounds(ref Matrix4x3 world)
    {
        var res = new AABB() { Min = new(float.MaxValue), Max = new(float.MinValue) };
        for (int i = 0; i < 8; ++i)
        {
            var p = ((i & 1) != 0 ? world.Row0 : -world.Row0) + ((i & 2) != 0 ? world.Row1 : -world.Row1) + ((i & 4) != 0 ? world.Row2 : -world.Row2) + world.Row3;
            res.Min = Vector3.Min(res.Min, p);
            res.Max = Vector3.Max(res.Max, p);
        }
        return res;
    }

    private static AABB CalculateSphereBounds(ulong id, ref Matrix4x3 world)
    {
        var scale = world.Row0.Length(); // note: a lot of code assumes it's uniform...
        if (Math.Abs(scale - world.Row1.Length()) > 0.1 || Math.Abs(scale - world.Row2.Length()) > 0.1)
            Service.Log.Error($"Sphere {id:X} has non-uniform scale");
        var vscale = new Vector3(scale);
        return new AABB() { Min = world.Row3 - vscale, Max = world.Row3 + vscale };
    }

    private static AABB CalculateMeshBounds(Mesh mesh, ref Matrix4x3 world)
    {
        var res = new AABB() { Min = new(float.MaxValue), Max = new(float.MinValue) };
        foreach (var part in mesh.Parts)
        {
            foreach (var v in part.Vertices)
            {
                var p = world.TransformCoordinate(v);
                res.Min = Vector3.Min(res.Min, p);
                res.Max = Vector3.Max(res.Max, p);
            }
        }
        return res;
    }

    private void AddBounds(ref AABB worldBB)
    {
        BoundsMin = Vector3.Min(BoundsMin, worldBB.Min);
        BoundsMax = Vector3.Max(BoundsMax, worldBB.Max);
    }

    private unsafe void FillFromFileNode(List<MeshPart> parts, MeshPCB.FileNode* node)
    {
        if (node == null)
            return;
        parts.Add(BuildMeshFromNode(node));
        FillFromFileNode(parts, node->Child1);
        FillFromFileNode(parts, node->Child2);
    }

    private unsafe MeshPart BuildMeshFromNode(MeshPCB.FileNode* node)
    {
        var part = new MeshPart();
        for (int i = 0; i < node->NumVertsRaw + node->NumVertsCompressed; ++i)
            part.Vertices.Add(node->Vertex(i));
        foreach (ref var p in node->Primitives)
            part.Primitives.Add((p.V1, p.V2, p.V3));
        return part;
    }

    private static Mesh BuildBoxMesh()
    {
        var mesh = new MeshPart();
        mesh.Vertices.Add(new(-1, -1, -1));
        mesh.Vertices.Add(new(-1, -1, +1));
        mesh.Vertices.Add(new(+1, -1, -1));
        mesh.Vertices.Add(new(+1, -1, +1));
        mesh.Vertices.Add(new(-1, +1, -1));
        mesh.Vertices.Add(new(-1, +1, +1));
        mesh.Vertices.Add(new(+1, +1, -1));
        mesh.Vertices.Add(new(+1, +1, +1));
        // bottom (y=-1)
        mesh.Primitives.Add((0, 2, 1));
        mesh.Primitives.Add((1, 2, 3));
        // top (y=+1)
        mesh.Primitives.Add((5, 7, 4));
        mesh.Primitives.Add((4, 7, 6));
        // left (x=-1)
        mesh.Primitives.Add((0, 1, 4));
        mesh.Primitives.Add((4, 1, 5));
        // right (x=1)
        mesh.Primitives.Add((2, 6, 3));
        mesh.Primitives.Add((3, 6, 7));
        // front (z=-1)
        mesh.Primitives.Add((0, 4, 2));
        mesh.Primitives.Add((2, 4, 6));
        // back (z=1)
        mesh.Primitives.Add((1, 3, 5));
        mesh.Primitives.Add((5, 3, 7));
        return new() { Parts = { mesh }, Flags = Flags.FromAnalyticShape };
    }

    private static Mesh BuildSphereMesh(int numSegments)
    {
        var mesh = new MeshPart();
        var angle = 360.Degrees() / numSegments;
        var maxParallel = numSegments / 4 - 1;
        for (int p = -maxParallel; p <= maxParallel; ++p)
        {
            var r = (p * angle).ToDirection();
            for (int i = 0; i < numSegments; ++i)
            {
                var v = (i * angle).ToDirection() * r.Y;
                mesh.Vertices.Add(new(v.X, r.X, v.Y));
            }
        }
        var icap = mesh.Vertices.Count;
        mesh.Vertices.Add(new(0, -1, 0));
        mesh.Vertices.Add(new(0, +1, 0));
        // sides
        for (int p = 0; p < maxParallel * 2; ++p)
        {
            var ip = p * numSegments;
            for (int i = 0; i < numSegments - 1; ++i)
            {
                var iv = ip + i;
                mesh.Primitives.Add((iv, iv + 1, iv + numSegments));
                mesh.Primitives.Add((iv + numSegments, iv + 1, iv + numSegments + 1));
            }
            mesh.Primitives.Add((ip + numSegments - 1, ip, ip + numSegments * 2 - 1));
            mesh.Primitives.Add((ip + numSegments * 2 - 1, ip, ip + numSegments));
        }
        // bottom
        for (int i = 0; i < numSegments - 1; ++i)
            mesh.Primitives.Add((i + 1, i, icap));
        mesh.Primitives.Add((0, numSegments - 1, icap));
        // top
        var itop = icap - numSegments;
        for (int i = 0; i < numSegments - 1; ++i)
            mesh.Primitives.Add((itop + i, itop + i + 1, icap + 1));
        mesh.Primitives.Add((itop + numSegments - 1, itop, icap + 1));
        return new() { Parts = { mesh }, Flags = Flags.FromAnalyticShape };
    }

    private static Mesh BuildCylinderMesh(int numSegments)
    {
        var mesh = new MeshPart();
        var angle = 360.Degrees() / numSegments;
        for (int i = 0; i < numSegments; ++i)
        {
            // note: we try to emulate hardcoded pcb mesh, that's why we do an extra +5 here...
            var p = ((i + 5) * angle).ToDirection();
            mesh.Vertices.Add(new(p.X, -1, p.Y));
            mesh.Vertices.Add(new(p.X, +1, p.Y));
        }
        mesh.Vertices.Add(new(0, -1, 0));
        mesh.Vertices.Add(new(0, +1, 0));
        // sides
        for (int i = 0; i < numSegments - 1; ++i)
        {
            var iv = i * 2;
            mesh.Primitives.Add((iv, iv + 2, iv + 1));
            mesh.Primitives.Add((iv + 1, iv + 2, iv + 3));
        }
        var ivn = (numSegments - 1) * 2;
        mesh.Primitives.Add((ivn, 0, ivn + 1));
        mesh.Primitives.Add((ivn + 1, 0, 1));
        // bottom
        var bcenter = numSegments * 2;
        for (int i = 0; i < numSegments - 1; ++i)
        {
            var iv = i * 2;
            mesh.Primitives.Add((iv + 2, iv, bcenter));
        }
        mesh.Primitives.Add((0, ivn, bcenter));
        // top
        var tcenter = bcenter + 1;
        for (int i = 0; i < numSegments - 1; ++i)
        {
            var iv = i * 2 + 1;
            mesh.Primitives.Add((iv, iv + 2, tcenter));
        }
        mesh.Primitives.Add((ivn + 1, 1, tcenter));
        return new() { Parts = { mesh }, Flags = Flags.FromAnalyticShape };
    }
}
