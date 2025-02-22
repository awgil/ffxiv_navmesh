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
    public enum MeshType
    {
        None = 0,
        Terrain = 1 << 0,
        FileMesh = 1 << 1,
        CylinderMesh = 1 << 2,
        AnalyticShape = 1 << 3,
        AnalyticPlane = 1 << 4,

        All = (1 << 5) - 1
    }

    [Flags]
    public enum PrimitiveFlags
    {
        None = 0,
        ForceUnwalkable = 1 << 0, // this primitive can't be walked on, even if normal is fine
        FlyThrough = 1 << 1, // this primitive should not be present in voxel map
        Unlandable = 1 << 2, // this primitive can't be landed on (fly->walk transition)
    }

    public record struct Primitive(int V1, int V2, int V3, PrimitiveFlags Flags);

    public class MeshPart
    {
        public List<Vector3> Vertices = [];
        public List<Primitive> Primitives = [];
    }

    public class MeshInstance(ulong id, Matrix4x3 worldTransform, AABB worldBounds, PrimitiveFlags forceSetPrimFlags, PrimitiveFlags forceClearPrimFlags)
    {
        public ulong Id = id;
        public Matrix4x3 WorldTransform = worldTransform;
        public AABB WorldBounds = worldBounds;
        public PrimitiveFlags ForceSetPrimFlags = forceSetPrimFlags;
        public PrimitiveFlags ForceClearPrimFlags = forceClearPrimFlags;
    }

    public class Mesh
    {
        public List<MeshPart> Parts = [];
        public List<MeshInstance> Instances = [];
        public MeshType MeshType;
    }

    public Dictionary<string, Mesh> Meshes { get; private set; } = [];

    private const string _keyAnalyticBox = "<box>";
    private const string _keyAnalyticSphere = "<sphere>";
    private const string _keyAnalyticCylinder = "<cylinder>";
    private const string _keyAnalyticPlaneSingle = "<plane one-sided>";
    private const string _keyAnalyticPlaneDouble = "<plane two-sided>";
    private const string _keyMeshCylinder = "<mesh cylinder>";

    private static List<MeshPart> _meshBox;
    private static List<MeshPart> _meshSphere;
    private static List<MeshPart> _meshCylinder;
    private static List<MeshPart> _meshPlane;

    static SceneExtractor()
    {
        _meshBox = BuildBoxMesh();
        _meshSphere = BuildSphereMesh(16);
        _meshCylinder = BuildCylinderMesh(16);
        _meshPlane = BuildPlaneMesh();
    }

    public unsafe SceneExtractor(SceneDefinition scene)
    {
        Meshes[_keyAnalyticBox] = new() { Parts = _meshBox, MeshType = MeshType.AnalyticShape };
        Meshes[_keyAnalyticSphere] = new() { Parts = _meshSphere, MeshType = MeshType.AnalyticShape };
        Meshes[_keyAnalyticCylinder] = new() { Parts = _meshCylinder, MeshType = MeshType.AnalyticShape };
        Meshes[_keyAnalyticPlaneSingle] = new() { Parts = _meshPlane, MeshType = MeshType.AnalyticPlane };
        Meshes[_keyAnalyticPlaneDouble] = new() { Parts = _meshPlane, MeshType = MeshType.AnalyticPlane };
        Meshes[_keyMeshCylinder] = new() { Parts = _meshCylinder, MeshType = MeshType.CylinderMesh };
        foreach (var path in scene.MeshPaths.Values)
            AddMesh(path, MeshType.FileMesh);

        foreach (var terr in scene.Terrains)
        {
            var list = Service.DataManager.GetFile(terr + "/list.pcb");
            if (list != null)
            {
                fixed (byte* data = &list.Data[0])
                {
                    var header = (ColliderStreamed.FileHeader*)data;
                    foreach (ref var entry in new Span<ColliderStreamed.FileEntry>(header + 1, header->NumMeshes))
                    {
                        var mesh = AddMesh($"{terr}/tr{entry.MeshId:d4}.pcb", MeshType.Terrain);
                        AddInstance(mesh, 0, ref Matrix4x3.Identity, ref entry.Bounds, 0, 0);
                    }
                }
            }
        }

        foreach (var part in scene.BgParts)
        {
            var info = ExtractBgPartInfo(scene, part.key, part.transform, part.crc, part.analytic);
            if (info.path.Length > 0)
                AddInstance(Meshes[info.path], part.key, ref info.transform, ref info.bounds, part.matId, part.matMask);
        }

        foreach (var coll in scene.Colliders)
        {
            // try to filter out all colliders that become inactive under normal conditions
            // this is basically every material with 0x400 set, except for the invisible walls that surround most overworld zones, which are 0x202411
            if ((coll.matId & 0x410) == 0x400)
                continue;

            var info = ExtractColliderInfo(scene, coll.key, coll.transform, coll.crc, coll.type);
            if (info.path.Length > 0)
                AddInstance(Meshes[info.path], coll.key, ref info.transform, ref info.bounds, coll.matId, coll.matMask);
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
                    FileLayerGroupAnalyticCollider.Type.Plane => (_keyAnalyticPlaneSingle, CalculatePlaneBounds(ref resultingTransform)),
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
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Plane => (_keyAnalyticPlaneSingle, CalculatePlaneBounds(ref transform)),
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Mesh => (scene.MeshPaths[crc], CalculateMeshBounds(Meshes[scene.MeshPaths[crc]], ref transform)),
            FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.PlaneTwoSided => (_keyAnalyticPlaneDouble, CalculatePlaneBounds(ref transform)),
            _ => ("", default)
        };
        return (path, transform, bounds);
    }

    private unsafe Mesh AddMesh(string path, MeshType type)
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
        mesh.MeshType = type;
        Meshes[path] = mesh;
        return mesh;
    }

    private void AddInstance(Mesh mesh, ulong id, ref Matrix4x3 worldTransform, ref AABB worldBounds, ulong matId, ulong matMask)
    {
        var instance = new MeshInstance(id, worldTransform, worldBounds, ExtractMaterialFlags(matMask & matId), ExtractMaterialFlags(matMask & ~matId));
        mesh.Instances.Add(instance);
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

    private static AABB CalculatePlaneBounds(ref Matrix4x3 world)
    {
        var res = new AABB() { Min = new(float.MaxValue), Max = new(float.MinValue) };
        for (int i = 0; i < 4; ++i)
        {
            var p = ((i & 1) != 0 ? world.Row0 : -world.Row0) + ((i & 2) != 0 ? world.Row1 : -world.Row1) + world.Row3;
            res.Min = Vector3.Min(res.Min, p);
            res.Max = Vector3.Max(res.Max, p);
        }
        return res;
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
            part.Primitives.Add(new(p.V1, p.V2, p.V3, ExtractMaterialFlags(p.Material)));
        return part;
    }

    private PrimitiveFlags ExtractMaterialFlags(ulong mat)
    {
        var res = PrimitiveFlags.None;
        if ((mat & 0x200000) != 0)
            res |= PrimitiveFlags.Unlandable;
        if ((mat & 0x2011) == 0x2011)
            res |= PrimitiveFlags.Unlandable;
        if ((mat & 0x100000) != 0)
            res |= PrimitiveFlags.FlyThrough;
        return res;
    }

    private static List<MeshPart> BuildBoxMesh()
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
        mesh.Primitives.Add(new(0, 2, 1, PrimitiveFlags.None));
        mesh.Primitives.Add(new(1, 2, 3, PrimitiveFlags.None));
        // top (y=+1)
        mesh.Primitives.Add(new(5, 7, 4, PrimitiveFlags.None));
        mesh.Primitives.Add(new(4, 7, 6, PrimitiveFlags.None));
        // left (x=-1)
        mesh.Primitives.Add(new(0, 1, 4, PrimitiveFlags.None));
        mesh.Primitives.Add(new(4, 1, 5, PrimitiveFlags.None));
        // right (x=1)
        mesh.Primitives.Add(new(2, 6, 3, PrimitiveFlags.None));
        mesh.Primitives.Add(new(3, 6, 7, PrimitiveFlags.None));
        // front (z=-1)
        mesh.Primitives.Add(new(0, 4, 2, PrimitiveFlags.None));
        mesh.Primitives.Add(new(2, 4, 6, PrimitiveFlags.None));
        // back (z=1)
        mesh.Primitives.Add(new(1, 3, 5, PrimitiveFlags.None));
        mesh.Primitives.Add(new(5, 3, 7, PrimitiveFlags.None));
        return [mesh];
    }

    private static List<MeshPart> BuildSphereMesh(int numSegments)
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
                mesh.Primitives.Add(new(iv, iv + 1, iv + numSegments, PrimitiveFlags.None));
                mesh.Primitives.Add(new(iv + numSegments, iv + 1, iv + numSegments + 1, PrimitiveFlags.None));
            }
            mesh.Primitives.Add(new(ip + numSegments - 1, ip, ip + numSegments * 2 - 1, PrimitiveFlags.None));
            mesh.Primitives.Add(new(ip + numSegments * 2 - 1, ip, ip + numSegments, PrimitiveFlags.None));
        }
        // bottom
        for (int i = 0; i < numSegments - 1; ++i)
            mesh.Primitives.Add(new(i + 1, i, icap, PrimitiveFlags.None));
        mesh.Primitives.Add(new(0, numSegments - 1, icap, PrimitiveFlags.None));
        // top
        var itop = icap - numSegments;
        for (int i = 0; i < numSegments - 1; ++i)
            mesh.Primitives.Add(new(itop + i, itop + i + 1, icap + 1, PrimitiveFlags.None));
        mesh.Primitives.Add(new(itop + numSegments - 1, itop, icap + 1, PrimitiveFlags.None));
        return [mesh];
    }

    private static List<MeshPart> BuildCylinderMesh(int numSegments)
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
            mesh.Primitives.Add(new(iv, iv + 2, iv + 1, PrimitiveFlags.None));
            mesh.Primitives.Add(new(iv + 1, iv + 2, iv + 3, PrimitiveFlags.None));
        }
        var ivn = (numSegments - 1) * 2;
        mesh.Primitives.Add(new(ivn, 0, ivn + 1, PrimitiveFlags.None));
        mesh.Primitives.Add(new(ivn + 1, 0, 1, PrimitiveFlags.None));
        // bottom
        var bcenter = numSegments * 2;
        for (int i = 0; i < numSegments - 1; ++i)
        {
            var iv = i * 2;
            mesh.Primitives.Add(new(iv + 2, iv, bcenter, PrimitiveFlags.None));
        }
        mesh.Primitives.Add(new(0, ivn, bcenter, PrimitiveFlags.None));
        // top
        var tcenter = bcenter + 1;
        for (int i = 0; i < numSegments - 1; ++i)
        {
            var iv = i * 2 + 1;
            mesh.Primitives.Add(new(iv, iv + 2, tcenter, PrimitiveFlags.None));
        }
        mesh.Primitives.Add(new(ivn + 1, 1, tcenter, PrimitiveFlags.None));
        return [mesh];
    }

    private static List<MeshPart> BuildPlaneMesh()
    {
        var mesh = new MeshPart();
        mesh.Vertices.Add(new(-1, +1, 0));
        mesh.Vertices.Add(new(-1, -1, 0));
        mesh.Vertices.Add(new(+1, -1, 0));
        mesh.Vertices.Add(new(+1, +1, 0));
        mesh.Primitives.Add(new(0, 1, 2, PrimitiveFlags.None));
        mesh.Primitives.Add(new(0, 2, 3, PrimitiveFlags.None));
        return [mesh];
    }
}
