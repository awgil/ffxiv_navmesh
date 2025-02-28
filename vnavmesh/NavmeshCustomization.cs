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

    public static (Matrix4x3 Transform, AABB AABB) GenerateTransformAABB(Vector3 scale, Vector3 transform)
    {
        var t4 = Matrix4x3.Identity;
        t4.M11 = scale.X;
        t4.M22 = scale.Y;
        t4.M33 = scale.Z;
        t4.Row3 = transform;
        return (t4, new AABB() { Min = t4.Row3 - scale, Max = t4.Row3 + scale });
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
