using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

// compact scene definition as extracted from the game's layout
// the goal is to fill it quickly on main thread, and then extract real data in background thread
public class SceneDefinition
{
    public SortedSet<uint> FestivalLayers = new();
    public List<string> Terrains = new();
    public Dictionary<uint, (Transform transform, Vector3 bbMin, Vector3 bbMax)> AnalyticShapes = new(); // key = crc; used by bgparts
    public Dictionary<uint, string> MeshPaths = new(); // key = crc, value = pcb path; used by all colliders
    public List<(ulong key, Transform transform, uint crc, ulong matId, ulong matMask, bool analytic)> BgParts = new();
    public List<(ulong key, Transform transform, uint crc, ulong matId, ulong matMask, ColliderType type)> Colliders = new();

    public unsafe void FillFromActiveLayout() => FillFromLayout(LayoutWorld.Instance()->ActiveLayout);

    public unsafe void FillFromLayout(LayoutManager* layout)
    {
        if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
            return;

        foreach (var (k, v) in layout->Layers)
        {
            if (v.Value->FestivalId != 0)
            {
                FestivalLayers.Add(((uint)v.Value->FestivalSubId << 16) | v.Value->FestivalId);
            }
        }

        foreach (var (k, v) in layout->CrcToAnalyticShapeData)
        {
            AnalyticShapes[k.Key] = (v.Transform, v.BoundsMin, v.BoundsMax);
        }

        foreach (var (k, v) in layout->Terrains)
        {
            Terrains.Add($"{LayoutUtils.ReadString(v.Value->Path)}/collision");
        }

        var bgParts = LayoutUtils.FindPtr(ref layout->InstancesByType, InstanceType.BgPart);
        if (bgParts != null)
        {
            foreach (var (k, v) in *bgParts)
            {
                var cast = (BgPartsLayoutInstance*)v.Value;
                if (cast->AnalyticShapeDataCrc != 0)
                {
                    BgParts.Add((k, *v.Value->GetTransformImpl(), cast->AnalyticShapeDataCrc, ((ulong)cast->CollisionMaterialIdHigh << 32) | cast->CollisionMaterialIdLow, ((ulong)cast->CollisionMaterialMaskHigh << 32) | cast->CollisionMaterialMaskLow, true));
                }
                else if (cast->CollisionMeshPathCrc != 0)
                {
                    if (!MeshPaths.ContainsKey(cast->CollisionMeshPathCrc))
                        MeshPaths[cast->CollisionMeshPathCrc] = LayoutUtils.ReadString(LayoutUtils.FindPtr(ref layout->CrcToPath, cast->CollisionMeshPathCrc));
                    BgParts.Add((k, *v.Value->GetTransformImpl(), cast->CollisionMeshPathCrc, ((ulong)cast->CollisionMaterialIdHigh << 32) | cast->CollisionMaterialIdLow, ((ulong)cast->CollisionMaterialMaskHigh << 32) | cast->CollisionMaterialMaskLow, false));
                }
            }
        }

        var colliders = LayoutUtils.FindPtr(ref layout->InstancesByType, InstanceType.ColliderGeneric);
        if (colliders != null)
        {
            foreach (var (k, v) in *colliders)
            {
                var cast = (ColliderGenericLayoutInstance*)v.Value;
                if ((cast->MaterialIdLow & 0x400) != 0)
                    continue; // TODO: reconsider... (this aims to filter out doors that are opened when you get near them, not sure whether it's the right condition)
                if (cast->PcbPathCrc != 0 && !MeshPaths.ContainsKey(cast->PcbPathCrc))
                    MeshPaths[cast->PcbPathCrc] = LayoutUtils.ReadString(LayoutUtils.FindPtr(ref layout->CrcToPath, cast->PcbPathCrc));
                Colliders.Add((k, cast->ColliderLayoutInstance.Transform, cast->PcbPathCrc, ((ulong)cast->MaterialIdHigh << 32) | cast->MaterialIdLow, ((ulong)cast->MaterialMaskHigh << 32) | cast->MaterialMaskLow, cast->ColliderLayoutInstance.Type));
            }
        }
    }
}
