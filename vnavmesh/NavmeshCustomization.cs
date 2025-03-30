using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System;
using System.Collections.Generic;
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

    public virtual bool IsFlyingSupported(SceneDefinition definition) => Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(definition.TerritoryID)?.TerritoryIntendedUse.RowId is 1 or 49 or 47; // 1 is normal outdoor, 49 is island, 47 is Diadem

    // this is a customization point to add or remove colliders in the scene
    public virtual void CustomizeScene(SceneExtractor scene) { }
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

public static class SceneExtensions {
    private static void InsertAxisAlignedCollider(this SceneExtractor scene, string meshKey, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default) {
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

    public static void InsertAABoxCollider(this SceneExtractor scene, AABB bounds, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default) {
        var scale = (bounds.Max - bounds.Min) * 0.5f;
        var transform = (bounds.Min + bounds.Max) * 0.5f;
        InsertAABoxCollider(scene, scale, transform, forceSetFlags, forceClearFlags);
    }

    public static void InsertCylinderCollider(this SceneExtractor scene, Vector3 scale, Vector3 worldTransform, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default) => InsertAxisAlignedCollider(scene, "<cylinder>", scale, worldTransform, forceSetFlags, forceClearFlags);
    public static void InsertCylinderCollider(this SceneExtractor scene, AABB bounds, SceneExtractor.PrimitiveFlags forceSetFlags = default, SceneExtractor.PrimitiveFlags forceClearFlags = default) {
        var scale = (bounds.Max - bounds.Min) * 0.5f;
        var transform = (bounds.Min + bounds.Max) * 0.5f;
        InsertCylinderCollider(scene, scale, transform, forceSetFlags, forceClearFlags);
    }
}
