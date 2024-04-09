using System;
using System.Collections.Generic;
using System.Linq;
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

    public virtual bool IsFlyingSupported(SceneDefinition definition) => Service.LuminaRow<Lumina.Excel.GeneratedSheets.TerritoryType>(definition.TerritoryID)?.TerritoryIntendedUse is 1 or 49 or 47; // 1 is normal outdoor, 49 is island, 47 is Diadem

    // this is a customization point to add or remove colliders in the scene
    public virtual void CustomizeScene(SceneExtractor scene) 
    {
        /* remove entire mesh and all instances
         * if (scene.Meshes.TryGetValue("bg/ffxiv/wil_w1/dun/w1d1/collision/w1d1_a2_wall1.pcb", out var _))
         *     scene.Meshes.Remove("bg/ffxiv/wil_w1/dun/w1d1/collision/w1d1_a2_wall1.pcb");
         *     
         * remove instance of mesh
         * if (scene.Meshes.TryGetValue("bg/ffxiv/wil_w1/dun/w1d1/collision/w1d1_a2_wall1.pcb", out var mesh))
         *     mesh.Instances.RemoveAt(0);
        */
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
