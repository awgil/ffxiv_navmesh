using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using System;

namespace Navmesh;

public unsafe static class LayoutUtils
{
    public static string ReadString(byte* data) => data != null ? MemoryHelper.ReadStringNullTerminated((nint)data) : "";
    public static string ReadString(RefCountedString* data) => data != null ? ReadString(data->Data) : "";

    public static V* Find<K, V>(ref this StdMap<K, V> map, K key) where K : unmanaged, IComparable where V : unmanaged
    {
        var result = map.Head;
        var trynode = map.Head->Parent;
        while (!trynode->IsNil)
        {
            if (trynode->KeyValuePair.Item1.CompareTo(key) < 0)
            {
                trynode = trynode->Right;
            }
            else
            {
                result = trynode;
                trynode = trynode->Left;
            }
        }
        if (result->IsNil || key.CompareTo(result->KeyValuePair.Item1) < 0)
            result = map.Head;
        return result != map.Head ? &result->KeyValuePair.Item2 : null;
    }

    public static V* FindPtr<K, V>(ref this StdMap<K, Pointer<V>> map, K key) where K : unmanaged, IComparable where V : unmanaged
    {
        var res = Find(ref map, key);
        return res != null ? res->Value : null;
    }

    public static ILayoutInstance* FindInstance(LayoutManager* layout, ulong key)
    {
        foreach (var (ikt, ikv) in layout->InstancesByType)
        {
            var iter = ikv.Value->FindPtr(key);
            if (iter != null)
                return iter;
        }
        return null;
    }

    public static LayoutManager.Filter* FindFilter(LayoutManager* layout)
    {
        if (layout->CfcId != 0) // note: some code paths check cfcid match only if TerritoryTypeId != 0; don't think it actually matters
            foreach (var (k, v) in layout->Filters)
                if (v.Value->CfcId == layout->CfcId)
                    return v.Value;
        if (layout->TerritoryTypeId != 0)
            foreach (var (k, v) in layout->Filters)
                if (v.Value->TerritoryTypeId == layout->TerritoryTypeId)
                    return v.Value;
        return layout->TerritoryTypeId == 0 ? FindPtr(ref layout->Filters, layout->FilterKey) : null;
    }

    public static bool LayerActiveFestival(FileLayerGroupLayer* layer, Span<uint> festivals)
    {
        if (layer->FestivalId == 0)
            return true; // non-festival, always active

        if (layer->FestivalSubId != 0)
        {
            return festivals.Contains((uint)((layer->FestivalSubId << 16) | layer->FestivalId));
        }
        else
        {
            foreach (var f in festivals)
                if ((ushort)f == layer->FestivalId)
                    return true;
            return false;
        }
    }

    public static bool LayerActiveFilter(FileLayerGroupLayer* layer, uint filterId)
    {
        var filter = layer->Filter;
        return filter == null || filter->Operation switch
        {
            FileLayerGroupLayerFilter.Op.Match => filter->Entries.Contains(filterId),
            FileLayerGroupLayerFilter.Op.NoMatch => !filter->Entries.Contains(filterId),
            _ => true
        };
    }
}
