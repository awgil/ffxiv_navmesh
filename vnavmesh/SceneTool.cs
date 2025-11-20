using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using FFXIVClientStructs.STD;
using FFXIVClientStructs.STD.Helper;
using System;
using System.Collections.Generic;
using System.Numerics;
using static Navmesh.SceneExtractor;
using ColliderType = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType;
using Mesh = Navmesh.SceneExtractor.Mesh;

namespace Navmesh;

public record class InstanceWithMesh(Mesh Mesh, MeshInstance Instance);

public class SceneTool
{
    public Dictionary<string, Mesh> Meshes { get; private set; } = [];
    public Dictionary<uint, List<InstanceWithMesh>> Terrains { get; private set; } = [];

    private static readonly List<MeshPart> _meshBox;
    private static readonly List<MeshPart> _meshSphere;
    private static readonly List<MeshPart> _meshCylinder;
    private static readonly List<MeshPart> _meshPlane;

    static SceneTool()
    {
        _meshBox = BuildBoxMesh();
        _meshSphere = BuildSphereMesh(16);
        _meshCylinder = BuildCylinderMesh(16);
        _meshPlane = BuildPlaneMesh();
    }

    public static List<MeshPart> BuildBoxMesh()
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

    private static SceneTool? _instance;

    private const string _keyAnalyticBox = "<box>";
    private const string _keyAnalyticSphere = "<sphere>";
    private const string _keyAnalyticCylinder = "<cylinder>";
    private const string _keyAnalyticPlaneSingle = "<plane one-sided>";
    private const string _keyAnalyticPlaneDouble = "<plane two-sided>";
    private const string _keyMeshCylinder = "<mesh cylinder>";

    private readonly Dictionary<uint, string> MeshPaths = [];
    private readonly Dictionary<uint, AnalyticShape?> AnalyticShapes = [];
    private record struct AnalyticShape(Transform Transform, Vector3 BoundsMin, Vector3 BoundsMax);

    private SceneTool()
    {
        Meshes[_keyAnalyticBox] = new() { Path = _keyAnalyticBox, Parts = _meshBox, MeshType = MeshType.AnalyticShape };
        Meshes[_keyAnalyticSphere] = new() { Path = _keyAnalyticSphere, Parts = _meshSphere, MeshType = MeshType.AnalyticShape };
        Meshes[_keyAnalyticCylinder] = new() { Path = _keyAnalyticCylinder, Parts = _meshCylinder, MeshType = MeshType.AnalyticShape };
        Meshes[_keyAnalyticPlaneSingle] = new() { Path = _keyAnalyticPlaneSingle, Parts = _meshPlane, MeshType = MeshType.AnalyticPlane };
        Meshes[_keyAnalyticPlaneDouble] = new() { Path = _keyAnalyticPlaneDouble, Parts = _meshPlane, MeshType = MeshType.AnalyticPlane };
        Meshes[_keyMeshCylinder] = new() { Path = _keyMeshCylinder, Parts = _meshCylinder, MeshType = MeshType.CylinderMesh };
    }

    public static SceneTool Get() => _instance ??= new();

    public static unsafe ulong GetKey(ILayoutInstance* obj) => (ulong)obj->Id.InstanceKey << 32 | obj->SubId;

    public unsafe InstanceWithMesh? CreateInstance(BgPartsLayoutInstance* bgPart)
    {
        var key = GetKey(&bgPart->ILayoutInstance);

        ulong mat = (ulong)bgPart->CollisionMaterialIdHigh << 32 | bgPart->CollisionMaterialIdLow;
        var matFlags = ExtractMaterialFlags(mat);

        if (bgPart->AnalyticShapeDataCrc != 0)
        {
            if (TryGetAnalyticShapeData(bgPart->AnalyticShapeDataCrc, bgPart->Layout, out var shape))
            {
                var transform = *bgPart->GetTransformImpl();
                var mtxBounds = Matrix4x4.CreateScale((shape.BoundsMax - shape.BoundsMin) * 0.5f);
                mtxBounds.Translation = (shape.BoundsMax + shape.BoundsMin) * 0.5f;
                var fullTransform = mtxBounds * shape.Transform.Compose() * transform.Compose();
                var resultingTransform = new Matrix4x3(fullTransform);

                switch ((FileLayerGroupAnalyticCollider.Type)shape.Transform.Type)
                {
                    case FileLayerGroupAnalyticCollider.Type.Box:
                        return new(Meshes[_keyAnalyticBox], new MeshInstance(key, resultingTransform, CalculateBoxBounds(ref resultingTransform), matFlags, default));
                    case FileLayerGroupAnalyticCollider.Type.Sphere:
                        return new(Meshes[_keyAnalyticSphere], new(key, resultingTransform, CalculateSphereBounds(key, ref resultingTransform), matFlags, default));
                    case FileLayerGroupAnalyticCollider.Type.Cylinder:
                        return new(Meshes[_keyMeshCylinder], new(key, resultingTransform, CalculateBoxBounds(ref resultingTransform), matFlags, default));
                    case FileLayerGroupAnalyticCollider.Type.Plane:
                        return new(Meshes[_keyAnalyticPlaneSingle], new(key, resultingTransform, CalculatePlaneBounds(ref resultingTransform), matFlags, default));
                }
            }
            else
            {
                Service.Log.Warning($"Unable to fetch analytic shape data for object {key} crc {bgPart->AnalyticShapeDataCrc}, object will not be created");
            }
        }

        if (bgPart->CollisionMeshPathCrc != 0)
        {
            var path = GetCollisionMeshPathByCrc(bgPart->CollisionMeshPathCrc, bgPart->Layout);
            var mesh = GetMeshByPath(path, MeshType.FileMesh);

            var transform = *bgPart->GetTransformImpl();
            var t2 = new Matrix4x3(transform.Compose());
            var bounds = CalculateMeshBounds(mesh, ref t2);

            return new(mesh, new(key, t2, bounds, matFlags, default));
        }

        return null;
    }

    public unsafe InstanceWithMesh? CreateInstance(CollisionBoxLayoutInstance* cast)
    {
        var key = GetKey(&cast->TriggerBoxLayoutInstance.ILayoutInstance);

        ulong mat = (ulong)cast->MaterialIdHigh << 32 | cast->MaterialIdLow;
        var matFlags = ExtractMaterialFlags(mat);

        var transform = new Matrix4x3(cast->Transform.Compose());
        switch (cast->TriggerBoxLayoutInstance.Type)
        {
            case ColliderType.Box:
                return new(Meshes[_keyAnalyticBox], new(key, transform, CalculateBoxBounds(ref transform), matFlags, default));
            case ColliderType.Sphere:
                return new(Meshes[_keyAnalyticSphere], new(key, transform, CalculateSphereBounds(key, ref transform), matFlags, default));
            case ColliderType.Cylinder:
                return new(Meshes[_keyAnalyticCylinder], new(key, transform, CalculateBoxBounds(ref transform), matFlags, default));
            case ColliderType.Plane:
                return new(Meshes[_keyAnalyticPlaneSingle], new(key, transform, CalculatePlaneBounds(ref transform), matFlags, default));
            case ColliderType.PlaneTwoSided:
                return new(Meshes[_keyAnalyticPlaneDouble], new(key, transform, CalculatePlaneBounds(ref transform), matFlags, default));
            case ColliderType.Mesh:
                if (cast->PcbPathCrc != 0)
                {
                    var path = GetCollisionMeshPathByCrc(cast->PcbPathCrc, cast->Layout);
                    var mesh = GetMeshByPath(path, MeshType.FileMesh);
                    return new(mesh, new(key, transform, CalculateMeshBounds(mesh, ref transform), matFlags, default));
                }
                break;
        }

        return null;
    }

    public unsafe List<InstanceWithMesh> GetTerrain(uint zoneId)
    {
        if (Terrains.TryGetValue(zoneId, out var t))
            return t;

        if (Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(zoneId) is not { } tt)
            return Terrains[zoneId] = [];

        List<InstanceWithMesh> obj = [];

        var ttBg = tt.Bg.ToString();
        var bgPrefix = "bg/" + ttBg[..ttBg.IndexOf("level/")];
        var terr = bgPrefix + "collision";
        if (Service.DataManager.GetFile(terr + "/list.pcb") is { } list)
        {
            fixed (byte* data = &list.Data[0])
            {
                var header = (ColliderStreamed.FileHeader*)data;
                foreach (ref var entry in new Span<ColliderStreamed.FileEntry>(header + 1, header->NumMeshes))
                {
                    var path = $"{terr}/tr{entry.MeshId:d4}.pcb";
                    var mesh = GetMeshByPath(path, MeshType.Terrain);
                    obj.Add(new(mesh, new(0, Matrix4x3.Identity, entry.Bounds, 0ul, 0ul)));
                }
            }
        }

        return Terrains[zoneId] = obj;
    }

    private unsafe bool TryGetAnalyticShapeData(uint crc, LayoutManager* layout, out AnalyticShape shape)
    {
        shape = default;

        if (AnalyticShapes.TryGetValue(crc, out var sh))
        {
            if (sh != null)
            {
                shape = sh.Value;
                return true;
            }
            return false;
        }

        if (TryGetValuePointer(layout->CrcToAnalyticShapeData, crc, out var data))
        {
            shape = new AnalyticShape(data->Transform, data->BoundsMin, data->BoundsMax);
            AnalyticShapes[crc] = shape;
            return true;
        }
        else
        {
            Service.Log.Warning($"analytic shape {crc:X} is missing from layout shape data and won't be loaded - this is a bug!");
            AnalyticShapes[crc] = null;
            return false;
        }
    }

    private unsafe bool TryGetValuePointer(StdMap<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData> map, uint key, out AnalyticShapeData* value)
    {
        var fr = FindLowerBound(map.WithOps.Tree, key);
        var flag = fr.Bound->_Myval.Item1.Key == key;
        if (flag)
        {
            value = &fr.Bound->_Myval.Item2;
        }
        else
        {
            value = null;
        }
        return flag;
    }

    // sure would be nice if AnalyticShapeDataKey was IComparable
    private unsafe RedBlackTree<StdPair<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>, LayoutManager.AnalyticShapeDataKey, PairKeyExtractor<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>>.FindResult FindLowerBound(RedBlackTree<StdPair<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>, LayoutManager.AnalyticShapeDataKey, PairKeyExtractor<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>> map, uint key)
    {
        if (map.Head == null)
            return default;

        RedBlackTree<StdPair<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>, LayoutManager.AnalyticShapeDataKey, PairKeyExtractor<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>>.FindResult findResult = default;
        findResult.Location = new()
        {
            Parent = map.Head->_Parent,
            Child = RedBlackTree<StdPair<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>, LayoutManager.AnalyticShapeDataKey, PairKeyExtractor<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>>.TreeChild.Right
        };
        findResult.Bound = map.Head;
        var result = findResult;
        var ptr = result.Location.Parent;
        while (!ptr->_Isnil)
        {
            result.Location.Parent = ptr;
            if (CompareKey(key, PairKeyExtractor<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>.ExtractKey(in ptr->_Myval)) <= 0)
            {
                result.Location.Child = RedBlackTree<StdPair<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>, LayoutManager.AnalyticShapeDataKey, PairKeyExtractor<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>>.TreeChild.Left;
                result.Bound = ptr;
                ptr = ptr->_Left;
            }
            else
            {
                result.Location.Child = RedBlackTree<StdPair<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>, LayoutManager.AnalyticShapeDataKey, PairKeyExtractor<LayoutManager.AnalyticShapeDataKey, AnalyticShapeData>>.TreeChild.Right;
                ptr = ptr->_Right;
            }
        }

        return result;
    }

    private static int CompareKey(uint key, LayoutManager.AnalyticShapeDataKey key2) => key.CompareTo(key2.Key);

    private unsafe string GetCollisionMeshPathByCrc(uint crc, LayoutManager* layout)
    {
        if (MeshPaths.TryGetValue(crc, out var path))
            return path;

        return MeshPaths[crc] = LayoutUtils.ReadString(layout->CrcToPath.FindPtr(crc));
    }

    private unsafe Mesh GetMeshByPath(string path, MeshType meshType)
    {
        if (Meshes.TryGetValue(path, out var cached))
            return cached;

        var mesh = new Mesh() { Path = path };
        var f = Service.DataManager.GetFile(path);
        if (f != null)
        {
            fixed (byte* rawData = &f.Data[0])
            {
                var data = (MeshPCB.FileHeader*)rawData;
                if (data->Version is 1 or 4)
                    FillFromFileNode(mesh.Parts, (MeshPCB.FileNode*)(data + 1));
            }
        }
        mesh.MeshType = meshType;
        return Meshes[path] = mesh;
    }
}
