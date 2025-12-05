using DotRecast.Detour;
using DotRecast.Recast;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using FFXIVClientStructs.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace Navmesh;

// base class for per-territory navmesh customizations
public class NavmeshCustomization
{
    // every time defaults change, we need to bump global navmesh version - this should be kept at zero
    // every time customization changes, we can bump the local version field, to avoid invalidating whole cache
    // each derived class should set it to non-zero value
    public virtual int Version => 0;

    public NavmeshSettings Settings = new();

    public virtual bool IsFlyingSupported(uint territory) => Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(territory)?.TerritoryIntendedUse.RowId is 1 or 49 or 47; // 1 is normal outdoor, 49 is island, 47 is Diadem

    public virtual void CustomizeTile(Tile tile) { }

    // this impl should be cheap, since it will be called dozens of times per frame
    public virtual bool FilterObject(ulong key, Pointer<ILayoutInstance> inst) => true;

    public virtual void CustomizeSettings(DtNavMeshCreateParams config) { }

    public virtual void CustomizeMesh(DtNavMesh mesh, List<uint> festivalLayers) { }

    protected static void LinkPoints(DtNavMesh mesh, Vector3 startPos, Vector3 endPos, bool bidirectional = false)
    {
        var refstart = InsertPointPoly(mesh, startPos, true);
        var refend = InsertPointPoly(mesh, endPos, false);

        mesh.GetTileAndPolyByRefUnsafe(refstart, out var startTile, out var startPoly);

        // start point -> end point link
        var idx = mesh.AllocLink(startTile);
        DtLink link = startTile.links[idx];
        link.refs = refend;
        link.edge = 0;
        link.side = 0;
        link.bmin = link.bmax = 0;
        link.next = startTile.polyLinks[startPoly.index];
        startTile.polyLinks[startPoly.index] = idx;

        if (bidirectional)
            LinkPoints(mesh, endPos, startPos, false);
    }

    private static long InsertPointPoly(DtNavMesh mesh, Vector3 pos, bool start)
    {
        var query = new DtNavMeshQuery(mesh);

        var status = query.FindNearestPoly(pos.SystemToRecast(), new(5, 5, 5), new DtQueryDefaultFilter(), out var startRef, out var startPolyPoint, out _);
        if (status.Failed() || startRef == 0)
            throw new ArgumentException($"Unable to find a polygon corresponding with input point {pos}");

        mesh.GetTileAndPolyByRefUnsafe(startRef, out var startTile, out var startPoly);
        var p = new DtPoly(startTile.data.header.polyCount, 1)
        {
            vertCount = 1,
            flags = 1
        };
        p.SetArea(Navmesh.AREA_ID_WARP);
        p.SetPolyType(DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION);
        p.verts[0] = startTile.data.header.vertCount;

        startTile.data.header.polyCount += 1;
        startTile.data.header.vertCount += 1;
        Array.Resize(ref startTile.data.polys, startTile.data.header.polyCount);
        Array.Resize(ref startTile.data.verts, startTile.data.header.vertCount * 3);

        // add new poly to mesh
        startTile.data.polys[^1] = p;
        startTile.data.verts[^3] = startPolyPoint.X;
        startTile.data.verts[^2] = startPolyPoint.Y;
        startTile.data.verts[^1] = startPolyPoint.Z;

        Array.Resize(ref startTile.polyLinks, startTile.polyLinks.Length + 1);
        startTile.polyLinks[^1] = DtNavMesh.DT_NULL_LINK;

        var salt = DtNavMesh.DecodePolyIdSalt(startRef);
        var pointRef = DtNavMesh.EncodePolyId(salt, startTile.index, p.index);

        // link point to the polygon it lies inside
        var idx = mesh.AllocLink(startTile);
        var link = startTile.links[idx];
        link.refs = startRef;
        link.edge = 0;
        link.side = 0xff;
        link.bmin = link.bmax = 0;
        startTile.polyLinks[p.index] = idx;

        // link owning polygon to point
        idx = mesh.AllocLink(startTile);
        link = startTile.links[idx];
        link.refs = pointRef;
        link.edge = 0xff;
        link.side = 0xff;
        link.bmin = link.bmax = 0;
        link.next = startTile.polyLinks[startPoly.index];
        startTile.polyLinks[startPoly.index] = idx;

        return pointRef;
    }
}

// attribute that defines which territories particular customization applies to
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class CustomizationTerritoryAttribute : Attribute
{
    public uint TerritoryID;

    public CustomizationTerritoryAttribute(uint territoryID) => TerritoryID = territoryID;
}

// registry containing all customizations
public static class NavmeshCustomizationRegistry
{
    public static NavmeshCustomization Default = new();
    public static Dictionary<uint, NavmeshCustomization> PerTerritory = new();

    static NavmeshCustomizationRegistry()
    {
        var baseType = typeof(NavmeshCustomization);
        foreach (var t in Assembly.GetExecutingAssembly().DefinedTypes.Where(t => t.IsSubclassOf(baseType)))
        {
            var instance = Activator.CreateInstance(t) as NavmeshCustomization;
            if (instance == null)
            {
                Service.Log.Error($"Failed to create instance of customization class {t}");
                continue;
            }

            foreach (var attr in t.GetCustomAttributes<CustomizationTerritoryAttribute>())
            {
                PerTerritory.Add(attr.TerritoryID, instance);
            }
        }
    }

    public static NavmeshCustomization ForTerritory(uint id) => PerTerritory.GetValueOrDefault(id, Default);
}

public static class SceneExtensions
{
    private static void InsertAxisAlignedCollider(this SceneExtractor scene, string meshKey, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default)
    {
        var transform = Matrix4x3.Identity;
        transform.M11 = scale.X;
        transform.M22 = scale.Y;
        transform.M33 = scale.Z;
        transform.Row3 = worldTransform;
        var aabb = new AABB() { Min = transform.Row3 - scale, Max = transform.Row3 + scale };
        var existingMesh = scene.Meshes[meshKey];
        var id = 0xbaadf00d00000001ul + (uint)existingMesh.Instances.Count;
        existingMesh.Instances.Insert(0, new(id, transform, aabb, forceSetFlags, forceClearFlags));
    }

    public static void InsertAABoxCollider(this SceneExtractor scene, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default) => InsertAxisAlignedCollider(scene, "<box>", scale, worldTransform, forceSetFlags, forceClearFlags);

    public static void InsertAABoxCollider(this SceneExtractor scene, AABB bounds, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default)
    {
        var scale = (bounds.Max - bounds.Min) * 0.5f;
        var transform = (bounds.Min + bounds.Max) * 0.5f;
        InsertAABoxCollider(scene, scale, transform, forceSetFlags, forceClearFlags);
    }

    public static void InsertCylinderCollider(this SceneExtractor scene, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default) => InsertAxisAlignedCollider(scene, "<cylinder>", scale, worldTransform, forceSetFlags, forceClearFlags);
    public static void InsertCylinderCollider(this SceneExtractor scene, AABB bounds, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default)
    {
        var scale = (bounds.Max - bounds.Min) * 0.5f;
        var transform = (bounds.Min + bounds.Max) * 0.5f;
        InsertCylinderCollider(scene, scale, transform, forceSetFlags, forceClearFlags);
    }
}

public static class TileExtensions
{
    public static (Vector2 Min, Vector2 Max) GetWorldBounds(this Tile tile)
    {
        var tileUnits = 2048 / tile.Customization.Settings.NumTiles[0];

        var xmin = tileUnits * tile.X - 1024;
        var zmin = tileUnits * tile.Z - 1024;
        return (new(xmin, zmin), new(xmin + tileUnits, zmin + tileUnits));
    }

    public static bool Contains(this Tile tile, AABB objectBounds)
    {
        var (tileMin, tileMax) = tile.GetWorldBounds();
        return objectBounds.Min.X < tileMax.X && objectBounds.Max.X > tileMin.X && objectBounds.Min.Z < tileMax.Y && objectBounds.Max.Z > tileMin.Y;
    }

    public static void AddAxisAlignedCollider(this Tile tile, SceneExtractor.Mesh mesh, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags setFlags = default)
    {
        var transform = Matrix4x3.Identity;
        transform.M11 = scale.X;
        transform.M22 = scale.Y;
        transform.M33 = scale.Z;
        transform.Row3 = worldTransform;
        var aabb = new AABB() { Min = transform.Row3 - scale, Max = transform.Row3 + scale };

        if (!tile.Contains(aabb))
            return;

        tile.Objects.Add((ulong)tile.Objects.Count, new(mesh, new(0ul, transform, aabb, setFlags, default), InstanceType.CollisionBox));
    }
    public static void AddBox(this Tile tile, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags setFlags = default) => AddAxisAlignedCollider(tile, SceneTool.Get().Meshes["<box>"], scale, worldTransform, setFlags);
    public static void AddCylinder(this Tile tile, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags setFlags = default) => AddAxisAlignedCollider(tile, SceneTool.Get().Meshes["<cylinder>"], scale, worldTransform, setFlags);
}

public static class CreateParamsExtensions
{
    public static void AddOffMeshConnection(this DtNavMeshCreateParams config, Vector3 ptA, Vector3 ptB, float radius = 0.5f, bool bidirectional = false, int userID = 0)
    {
        bool insideTile(Vector3 p) => p.X >= config.bmin.X && p.Y >= config.bmin.Y && p.Z >= config.bmin.Z && p.X <= config.bmax.X && p.Y <= config.bmax.Y && p.Z <= config.bmax.Z;

        var aInside = insideTile(ptA);
        var bInside = insideTile(ptB);

        if (aInside != bInside)
        {
            Service.Log.Error("This off-mesh connection would span two tiles, but Recast doesn't support these. Please adjust the endpoints or customize the mesh tile size so that both points are inside one tile.");
            Service.Log.Error($"Bounding box of matched tile: {config.bmin} <=> {config.bmax}");
            throw new ArgumentException("Invalid inter-tile off-mesh connection");
        }

        if (!aInside && !bInside)
            return;

        Extend(ref config.offMeshConVerts, 6);
        config.offMeshConVerts[^6] = ptA.X;
        config.offMeshConVerts[^5] = ptA.Y;
        config.offMeshConVerts[^4] = ptA.Z;
        config.offMeshConVerts[^3] = ptB.X;
        config.offMeshConVerts[^2] = ptB.Y;
        config.offMeshConVerts[^1] = ptB.Z;

        Extend(ref config.offMeshConDir, 1);
        config.offMeshConDir[^1] = bidirectional ? DtNavMesh.DT_OFFMESH_CON_BIDIR : 0;

        Extend(ref config.offMeshConFlags, 1);
        config.offMeshConFlags[^1] = 1;

        config.offMeshConCount++;

        Extend(ref config.offMeshConRad, 1);
        config.offMeshConRad[^1] = radius;

        Extend(ref config.offMeshConAreas, 1);
        config.offMeshConAreas[^1] = RcConstants.RC_WALKABLE_AREA;

        Extend(ref config.offMeshConUserID, 1);
        config.offMeshConUserID[^1] = userID;
    }

    private static void Extend<T>([NotNull] ref T[]? arr, int add)
    {
        arr ??= [];
        Array.Resize(ref arr, arr.Length + add);
    }
}
