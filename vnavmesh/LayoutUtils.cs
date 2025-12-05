using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Navmesh;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct ExdZoneSharedGroup
{
    uint LGBSharedGroup;
    uint RequirementRow0;
    uint RequirementRow1;
    uint RequirementRow2;
    uint RequirementRow3;
    uint RequirementRow4;
    uint RequirementRow5;
    uint Unknown0;
    uint RequirementQuestSequence0;
    uint RequirementQuestSequence1;
    uint RequirementQuestSequence2;
    uint RequirementQuestSequence3;
    uint RequirementQuestSequence4;
    uint RequirementQuestSequence5;
    uint Unknown1;
    byte RequirementType0;
    byte RequirementType1;
    byte RequirementType2;
    byte RequirementType3;
    byte RequirementType4;
    byte RequirementType5;
    byte Unknown8;
    byte Unknown9;
    byte Unknown10;
    byte Unknown11;
    byte Unknown12;
    byte Unknown13;
    byte Unknown14;
    byte Unknown15;

    public static implicit operator ExdZoneSharedGroup(Lumina.Excel.Sheets.ZoneSharedGroup sg) => new()
    {
        LGBSharedGroup = sg.LGBSharedGroup,
        RequirementRow0 = sg.RequirementRow[0].RowId,
        RequirementRow1 = sg.RequirementRow[1].RowId,
        RequirementRow2 = sg.RequirementRow[2].RowId,
        RequirementRow3 = sg.RequirementRow[3].RowId,
        RequirementRow4 = sg.RequirementRow[4].RowId,
        RequirementRow5 = sg.RequirementRow[5].RowId,
        Unknown0 = sg.Unknown0,
        RequirementQuestSequence0 = sg.RequirementQuestSequence[0],
        RequirementQuestSequence1 = sg.RequirementQuestSequence[1],
        RequirementQuestSequence2 = sg.RequirementQuestSequence[2],
        RequirementQuestSequence3 = sg.RequirementQuestSequence[3],
        RequirementQuestSequence4 = sg.RequirementQuestSequence[4],
        RequirementQuestSequence5 = sg.RequirementQuestSequence[5],
        Unknown1 = sg.Unknown1,
        RequirementType0 = sg.RequirementType[0],
        RequirementType1 = sg.RequirementType[1],
        RequirementType2 = sg.RequirementType[2],
        RequirementType3 = sg.RequirementType[3],
        RequirementType4 = sg.RequirementType[4],
        RequirementType5 = sg.RequirementType[5],
        Unknown8 = sg.Unknown8,
        Unknown9 = sg.Unknown9 ? (byte)1 : (byte)0,
        Unknown10 = sg.Unknown10 ? (byte)1 : (byte)0,
        Unknown11 = sg.Unknown11 ? (byte)1 : (byte)0,
        Unknown12 = sg.Unknown12 ? (byte)1 : (byte)0,
        Unknown13 = sg.Unknown13 ? (byte)1 : (byte)0,
        Unknown14 = sg.Unknown14 ? (byte)1 : (byte)0,
        Unknown15 = sg.Unknown15 ? (byte)1 : (byte)0,
    };
}

public static class Utils
{
    public static void Time(string label, Action t)
    {
        var s = Stopwatch.StartNew();
        t();
        s.Stop();
        Service.Log.Verbose($"[Timer] {label} took {s.ElapsedMilliseconds:f3}ms");
    }

    public static T Time<T>(string label, Func<T> t)
    {
        var s = Stopwatch.StartNew();
        var res = t();
        s.Stop();
        Service.Log.Verbose($"[Timer] {label} took {s.ElapsedMilliseconds:f3}ms");
        return res;
    }
}

public unsafe static class LayoutUtils
{
    private static delegate* unmanaged<ExdZoneSharedGroup*, uint> _getEnabledRequirementIndex;

    static LayoutUtils()
    {
        _getEnabledRequirementIndex = (delegate* unmanaged<ExdZoneSharedGroup*, uint>)Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 0F B6 53 6C");
    }

    public static uint[] GetZoneSharedGroupsEnabled(uint territoryType)
    {
        var tt = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(territoryType);
        if (tt == null)
            return [];

        var rows = tt.Value.ZoneSharedGroup.Value.ToList();
        var indices = new uint[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            ExdZoneSharedGroup exd = rows[i];
            indices[i] = _getEnabledRequirementIndex(&exd);
        }
        return indices;
    }

    public static string ReadString(byte* data) => data != null ? MemoryHelper.ReadStringNullTerminated((nint)data) : "";
    public static string ReadString(RefCountedString* data) => data != null ? data->DataString : "";

    public static V* FindPtr<K, V>(ref this StdMap<K, Pointer<V>> map, K key) where K : unmanaged, IComparable where V : unmanaged
    {
        return map.TryGetValuePointer(key, out var ptr) && ptr != null ? ptr->Value : null;
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
        return layout->TerritoryTypeId == 0 ? FindPtr(ref layout->Filters, layout->LayerFilterKey) : null;
    }

    public static bool LayerActiveFestival(FileLayerGroupLayer* layer, Span<GameMain.Festival> festivals)
    {
        if (layer->Festival.Id == 0)
            return true; // non-festival, always active

        if (layer->Festival.Phase != 0)
        {
            foreach (var f in festivals)
                if (f.Id == layer->Festival.Id && f.Phase == layer->Festival.Phase)
                    return true;
            return false;
        }
        else
        {
            foreach (var f in festivals)
                if (f.Id == layer->Festival.Id)
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

    public static string FestivalString(GameMain.Festival f) => $"{(uint)(f.Phase << 16) | f.Id:X}";
    public static string FestivalsString(ReadOnlySpan<GameMain.Festival> f) => $"{FestivalString(f[0])}.{FestivalString(f[1])}.{FestivalString(f[2])}.{FestivalString(f[3])}";
    public static unsafe T ReadField<T>(void* address, int offset) where T : unmanaged => *(T*)((IntPtr)address + offset);

    public static string DumpBytes(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i++)
        {
            sb.Append(data[i].ToString("X2"));
            sb.Append(' ');
        }
        return sb.ToString();
    }

    public static void DumpVt(void* ptr)
    {
        if (ptr == null)
            return;
        var vt = *(nint*)ptr;
        var m = Process.GetCurrentProcess().MainModule!.BaseAddress;
        Service.Log.Debug($"VT of {(nint)ptr:X}: +{vt - m:X}");
    }
}

static class LayoutExtensions
{
    public static bool HasCollision(ref readonly this BgPartsLayoutInstance p) => p.AnalyticShapeDataCrc > 0 || p.CollisionMeshPathCrc > 0;
    public static bool HasEnabledFlag(ref readonly this BgPartsLayoutInstance p) => p.ILayoutInstance.HasEnabledFlag();
    public static bool HasEnabledFlag(ref readonly this CollisionBoxLayoutInstance p) => p.TriggerBoxLayoutInstance.ILayoutInstance.HasEnabledFlag();
    public static bool HasEnabledFlag(ref readonly this ILayoutInstance p) => (p.Flags3 & 0x10) != 0;

    public static string Display(this AABB b) => $"AABB {{ Min = {b.Min}, Max = {b.Max} }}";
    public static string Display(this Transform t) => $"Transform {{ Translation = {t.Translation}, Type = {t.Type}, Rotation = {t.Rotation}, Scale = {t.Scale} }}";
    public static string Display(this Matrix4x3 m) => $"Matrix {{ {m.M11}, {m.M12}, {m.M13}; {m.M21}, {m.M22}, {m.M23}; {m.M31}, {m.M32}, {m.M33}; {m.M41}, {m.M42}, {m.M43} }}";
}

public static class Matrix
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix4x4 CreateTransform(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        var tx = Matrix4x4.CreateTranslation(translation);
        var rx = Matrix4x4.CreateFromQuaternion(rotation);
        var sx = Matrix4x4.CreateScale(scale);

        return sx * rx * tx;
    }
}
