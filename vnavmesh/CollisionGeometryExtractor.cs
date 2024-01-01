using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using System;
using System.Collections.Generic;
using System.Numerics;
using AABB = FFXIVClientStructs.FFXIV.Common.Math.AABB;
using Matrix4x3 = FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3;

namespace Navmesh;

// the main purpose of this is to quickly scan the game collision scene(s) on the main thread to find all relevant geometry, and then do the loading in background later
// we can't really access game collision scene from background threads, since it might be modified
public class CollisionGeometryExtractor
{
    [Flags]
    public enum Flags
    {
        None = 0,
        Filled = 1 << 0,
        FromStreamed = 1 << 1,
        FromFileMesh = 1 << 2,
        FromCylinderMesh = 1 << 3,
        FromAnalyticShape = 1 << 4,
    }

    public class MeshPart
    {
        public List<Vector3> Vertices = new();
        public List<(int v1, int v2, int v3)> Primitives = new();
    }

    public class Mesh
    {
        public List<MeshPart> Parts = new();
        public List<Matrix4x3> Instances = new(); // world transforms
        public Flags Flags;
    }

    public Dictionary<string, Mesh> Meshes { get; private set; } = new();
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

    private const string _keyAnalyticBox = "<box>";
    private const string _keyAnalyticSphere = "<sphere>";
    private const string _keyAnalyticCylinder = "<cylinder>";
    private const string _keyMeshCylinder = "<mesh cylinder>";

    private static Mesh _meshBox;
    private static Mesh _meshSphere;
    private static Mesh _meshCylinder;

    static CollisionGeometryExtractor()
    {
        _meshBox = BuildBoxMesh();
        _meshSphere = BuildSphereMesh(16);
        _meshCylinder = BuildCylinderMesh(16);
    }

    public CollisionGeometryExtractor()
    {
        Clear();
    }

    public void Clear()
    {
        Meshes.Clear();
        Meshes[_keyAnalyticBox] = _meshBox;
        Meshes[_keyAnalyticSphere] = _meshSphere;
        Meshes[_keyAnalyticCylinder] = _meshCylinder;
        Meshes[_keyMeshCylinder] = new() { Parts = _meshCylinder.Parts, Flags = Flags.Filled | Flags.FromCylinderMesh };
        BoundsMin = new(float.MaxValue);
        BoundsMax = new(float.MinValue);
    }

    // this has to be called from main thread (or while shared lock on collision module is held), so it has to be fast
    public unsafe void FillFromGame(ulong layers)
    {
        foreach (var s in Framework.Instance()->BGCollisionModule->SceneManager->Scenes)
        {
            foreach (var coll in s->Scene->Colliders)
            {
                if ((coll->LayerMask & layers) == 0)
                    continue; // don't care
                switch (coll->GetColliderType())
                {
                    case ColliderType.Streamed:
                        var collStreamed = (ColliderStreamed*)coll;
                        if (collStreamed->Header == null || collStreamed->Elements == null)
                            continue; // not loaded yet; TODO: consider just grabbing filename and loading it manually in background?..
                        var basePath = MemoryHelper.ReadStringNullTerminated((nint)collStreamed->PathBase);
                        AddBounds(ref collStreamed->Header->Bounds);
                        foreach (ref var e in new Span<ColliderStreamed.Element>(collStreamed->Elements, collStreamed->Header->NumMeshes))
                            AddInstance($"{basePath}/tr{e.MeshId:d4}.pcb", ref Matrix4x3.Identity, e.Mesh == null, Flags.FromFileMesh | Flags.FromStreamed); // add only if it's not streamed in, otherwise we'll add it while processing other collider
                        break;
                    case ColliderType.Mesh:
                        var collMesh = (ColliderMesh*)coll;
                        if (collMesh->MeshIsSimple)
                            continue; // TODO: if we ever get such meshes, reverse the format and add them too
                        AddBounds(ref collMesh->WorldBoundingBox);
                        if (collMesh->Resource != null)
                            AddInstance(MemoryHelper.ReadStringNullTerminated((nint)collMesh->Resource->Path), ref collMesh->World, true, Flags.FromFileMesh);
                        else // assume it's a cylinder placeholder
                            AddInstance(_keyMeshCylinder, ref collMesh->World, true, Flags.None);
                        break;
                    case ColliderType.Box:
                        var collBox = (ColliderBox*)coll;
                        AddBounds(coll);
                        AddInstance(_keyAnalyticBox, ref collBox->World, true, Flags.None);
                        break;
                    case ColliderType.Cylinder:
                        var collCylinder = (ColliderCylinder*)coll;
                        AddBounds(coll);
                        AddInstance(_keyAnalyticCylinder, ref collCylinder->World, true, Flags.None);
                        break;
                    case ColliderType.Sphere:
                        var collSphere = (ColliderSphere*)coll;
                        AddBounds(coll);
                        AddInstance(_keyAnalyticSphere, ref collSphere->World, true, Flags.None);
                        break;
                }
            }
        }
    }

    // can be called from the background thread safely (but obviously not safe to call anything else on the instance concurrently)
    public void Extract()
    {
        foreach (var (name, value) in Meshes)
            if (!value.Flags.HasFlag(Flags.Filled))
                FillFromFile(name, value);
    }

    private void AddInstance(string key, ref Matrix4x3 world, bool add, Flags flags)
    {
        if (!Meshes.TryGetValue(key, out var mesh))
            Meshes[key] = mesh = new();
        if (add)
            mesh.Instances.Add(world);
        mesh.Flags |= flags;
    }

    private unsafe void AddBounds(Collider* coll)
    {
        AABB bb = new();
        coll->GetWorldBB(&bb);
        AddBounds(ref bb);
    }

    private void AddBounds(ref AABB worldBB)
    {
        BoundsMin = Vector3.Min(BoundsMin, worldBB.Min);
        BoundsMax = Vector3.Max(BoundsMax, worldBB.Max);
    }

    private unsafe void FillFromFile(string path, Mesh mesh)
    {
        if (!mesh.Flags.HasFlag(Flags.FromFileMesh))
            throw new ArgumentException($"Mesh {path} is not from file");
        if (mesh.Parts.Count > 0)
            throw new ArgumentException($"Mesh {path} already contains data");
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
        mesh.Flags |= Flags.Filled;
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
        return new() { Parts = { mesh }, Flags = Flags.Filled | Flags.FromAnalyticShape };
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
        return new() { Parts = { mesh }, Flags = Flags.Filled | Flags.FromAnalyticShape };
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
        return new() { Parts = { mesh }, Flags = Flags.Filled | Flags.FromAnalyticShape };
    }
}
