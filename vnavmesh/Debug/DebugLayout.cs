using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.Interop;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Navmesh.Debug;

public unsafe class DebugLayout : IDisposable
{
    private class InstanceData
    {
        public int LayerGroupId;
        public ushort LayerId;
        public uint InstanceId;
        public uint SubId;
        public InstanceType Type;
        public bool InFile;
        public bool ExpectedToBeInGame;
        public ILayoutInstance* Instance;
        public Collider* Collider;
    }

    private UITree _tree = new();
    private DebugGameCollision _coll;
    private Dictionary<ulong, InstanceData> _insts = new();
    private bool _groupByLayerGroup = true;
    private bool _groupByLayer = true;
    private bool _groupByInstanceType = true;
    private bool _groupByMaterial = false;

    public DebugLayout(DebugGameCollision coll)
    {
        _coll = coll;
    }

    public void Dispose()
    {
    }

    public void Draw()
    {
        DrawWorld(LayoutWorld.Instance());
        var terr = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(Service.ClientState.TerritoryType);
        if (terr != null)
            DrawFile($"Territory {Service.ClientState.TerritoryType}", $"bg/{terr.Value.Bg}.lvb");
        DrawComparison(LayoutWorld.Instance()->ActiveLayout);
        _insts.Clear();
    }

    public static bool DrawInstance(UITree tree, string tag, LayoutManager* layout, ILayoutInstance* inst)
    {
        using var ni = tree.Node($"{tag} {inst->Id.Type} L{inst->Id.LayerKey:X4} I{inst->Id.InstanceKey:X8}.{inst->SubId:X8} ({inst->Id.u0:X2}) = {(nint)inst:X}, pool-idx={inst->IndexInPool}, prefab-index={inst->IndexInPrefab}, nesting={inst->NestingLevel}, u29low={inst->Flags1 & 0xF}, u29hi={(inst->Flags1 >> 7) != 0}, flags={inst->Flags2:X2} {inst->Flags3:X2}###{tag}");
        var collider = inst->GetCollider();
        if (ni.Opened)
        {
            tree.LeafNode($"Primary: {inst->HavePrimary()} '{LayoutUtils.ReadString(inst->GetPrimaryPath())}'");
            tree.LeafNode($"Secondary: {inst->HaveSecondary()} '{LayoutUtils.ReadString(inst->GetSecondaryPath())}'");
            tree.LeafNode($"Translation: {*inst->GetTranslationImpl()}");
            tree.LeafNode($"Rotation: {*inst->GetRotationImpl()}");
            tree.LeafNode($"Scale: {*inst->GetScaleImpl()}");
            tree.LeafNode($"Graphics: {(nint)inst->GetGraphics():X}");
            tree.LeafNode($"Collider: {(nint)collider:X} (loaded={inst->IsColliderLoaded()}, active={inst->IsColliderActive()})");
            tree.LeafNode($"Want to be active: {inst->WantToBeActive()}");
            switch (inst->Id.Type)
            {
                case InstanceType.BgPart:
                    var instBgPart = (BgPartsLayoutInstance*)inst;
                    tree.LeafNode($"Gfx obj: {(nint)instBgPart->GraphicsObject:X}");
                    tree.LeafNode($"Collider: {(nint)instBgPart->Collider:X} ({instBgPart->CollisionMeshPathCrc:X8} / {instBgPart->AnalyticShapeDataCrc:X8})");
                    if (instBgPart->CollisionMeshPathCrc != 0)
                    {
                        foreach (var (k, v) in layout->CrcToPath)
                        {
                            if (k == instBgPart->CollisionMeshPathCrc)
                            {
                                tree.LeafNode($"Collider path: {v.Value->DataString}");
                                break;
                            }
                        }
                    }
                    if (instBgPart->AnalyticShapeDataCrc != 0)
                    {
                        foreach (var (k, v) in layout->CrcToAnalyticShapeData)
                        {
                            if (k.Key == instBgPart->AnalyticShapeDataCrc)
                            {
                                DrawAnalyticShape(tree, "Shape data:", v);
                                break;
                            }
                        }
                    }
                    tree.LeafNode($"Collision material: {instBgPart->CollisionMaterialIdHigh:X8}{instBgPart->CollisionMaterialIdLow:X8} / {instBgPart->CollisionMaterialMaskHigh:X8}{instBgPart->CollisionMaterialMaskLow:X8}");
                    //tree.LeafNode($"unks: {instBgPart->u58} {instBgPart->u5C:X}");
                    break;
                case InstanceType.SharedGroup:
                case InstanceType.HelperObject:
                    var instPrefab = (SharedGroupLayoutInstance*)inst;
                    tree.LeafNode($"Resource: {(instPrefab->ResourceHandle != null ? instPrefab->ResourceHandle->FileName : "<null>")}");
                    tree.LeafNode($"Flags: {instPrefab->PrefabFlags1:X8} {instPrefab->PrefabFlags2:X8}");
                    using (var nc = tree.Node($"Instances ({instPrefab->Instances.Instances.Count})###instances", instPrefab->Instances.Instances.Count == 0))
                    {
                        if (nc.Opened)
                        {
                            int index = 0;
                            foreach (var part in instPrefab->Instances.Instances)
                            {
                                DrawInstance(tree, $"[{index++}]", layout, part.Value->Instance);
                            }
                        }
                    }
                    //using (var nc = tree.Node($"uA8 ({instPrefab->uA8.Instances.Size()})###a8", instPrefab->uA8.Instances.Size() == 0))
                    //{
                    //    if (nc.Opened)
                    //    {
                    //        int index = 0;
                    //        foreach (var part in instPrefab->uA8.Instances.Span)
                    //        {
                    //            DrawInstance(tree, $"[{index++}]", layout, part.Value->Instance);
                    //        }
                    //    }
                    //}
                    break;
                case InstanceType.CollisionBox:
                    var instCollGeneric = (CollisionBoxLayoutInstance*)inst;
                    var pcbPath = instCollGeneric->PcbPathCrc != 0 ? LayoutUtils.FindPtr(ref layout->CrcToPath, instCollGeneric->PcbPathCrc) : null;
                    tree.LeafNode($"Type: {instCollGeneric->TriggerBoxLayoutInstance.Type} (pcb={instCollGeneric->PcbPathCrc:X} '{(pcbPath != null ? pcbPath->DataString : "")}')");
                    tree.LeafNode($"Layer: {instCollGeneric->GetLayerMask():X} (is-43h={instCollGeneric->LayerMaskIs43h})");
                    tree.LeafNode($"Material: {instCollGeneric->MaterialIdHigh:X8}{instCollGeneric->MaterialIdLow:X8}/{instCollGeneric->MaterialMaskHigh:X8}{instCollGeneric->MaterialMaskLow:X8}");
                    tree.LeafNode($"Misc: active-by-default={instCollGeneric->TriggerBoxLayoutInstance.ActiveByDefault}");
                    //tree.LeafNode($"Unk: {instCollGeneric->ColliderLayoutInstance.u70}");
                    break;
            }
        }
        return ni.SelectedOrHovered;
    }

    private UITree.NodeRaii DrawManagerBase(string tag, IManagerBase* manager, string extra) => _tree.Node($"{tag} {(nint)manager:X}{(manager != null ? $" (owner={(nint)manager->Owner:X}, id={manager->Id:X})" : "")} {extra}###{tag}_{(nint)manager:X}", manager == null);

    private static (ulong mat, ulong mask) GetMaterial(ILayoutInstance* inst)
    {
        if (inst == null)
            return (0, 0);

        switch (inst->Id.Type)
        {
            case InstanceType.BgPart:
                var instBgPart = (BgPartsLayoutInstance*)inst;
                return ((instBgPart->CollisionMaterialIdHigh << 32) | instBgPart->CollisionMaterialIdLow, (instBgPart->CollisionMaterialMaskHigh << 32) | instBgPart->CollisionMaterialMaskLow);
            case InstanceType.CollisionBox:
                var instCollGeneric = (CollisionBoxLayoutInstance*)inst;
                return ((instCollGeneric->MaterialIdHigh << 32) | instCollGeneric->MaterialIdLow, (instCollGeneric->MaterialMaskHigh << 32) | instCollGeneric->MaterialMaskLow);
            case InstanceType.SharedGroup:
                ulong mat = 0;
                ulong mask = 0;
                SumMaterials((SharedGroupLayoutInstance*)inst, ref mat, ref mask);
                return (mat, mask);
            default:
                return (0, 0);
        }
    }

    private static void SumMaterials(SharedGroupLayoutInstance* inst, ref ulong mat, ref ulong mask)
    {
        foreach (var part in inst->Instances.Instances)
        {
            var (mat1, mask1) = GetMaterial(part.Value->Instance);
            mat |= mat1;
            mask |= mask1;
        }
    }

    private void DrawWorld(LayoutWorld* w)
    {
        using var nw = DrawManagerBase("World", &w->IManagerBase, $"t={w->MillisecondsSinceLastUpdate}ms");
        if (!nw.Opened)
            return;

        DrawLayout("Global", w->GlobalLayout);
        DrawLayout("Active", w->ActiveLayout);
        //DrawLayout("u28", w->UnkLayout28);
        //DrawLayout("u30", w->UnkLayout30);

        using (var n = _tree.Node($"Loaded layouts: {w->LoadedLayouts.Count}###loaded", w->LoadedLayouts.Count == 0))
        {
            if (n.Opened)
            {
                foreach (var (k, v) in w->LoadedLayouts)
                {
                    DrawLayout($"Terr {(int)k} (lvb crc={k >> 32:X})", v);
                }
            }
        }

        //using (var n = _tree.Node($"u90 layouts: {w->UnkLayouts90.Count}###u90", w->UnkLayouts90.Count == 0))
        //{
        //    if (n.Opened)
        //    {
        //        foreach (var (k, v) in w->UnkLayouts90)
        //        {
        //            DrawLayout($"Terr {(int)k} (lvb crc={k >> 32:X})", v);
        //        }
        //    }
        //}
    }

    private void DrawLayout(string tag, LayoutManager* manager)
    {
        using var nr = DrawManagerBase($"{tag} layout", &manager->IManagerBase, "");
        if (!nr.Opened)
            return;

        _tree.LeafNode($"Init state: {manager->InitState}");
        _tree.LeafNode($"Init args: type={manager->Type}, terrType={manager->TerritoryTypeId}, cfc={manager->CfcId}, filter-key={manager->LayerFilterKey:X}");
        _tree.LeafNode($"Festivals: status={manager->FestivalStatus} [{LayoutUtils.FestivalsString(manager->ActiveFestivals)}]");
        _tree.LeafNode($"Streaming mgr: {(nint)manager->StreamingManager:X}");
        _tree.LeafNode($"Env mgr: {(nint)manager->Environment:X}");
        _tree.LeafNode($"OBSet mgr: {(nint)manager->OBSetManager:X}");
        _tree.LeafNode($"Streaming params: force-update={manager->ForceUpdateAllStreaming}, skip-terrain-coll={manager->SkipAddingTerrainCollider}");
        _tree.LeafNode($"Streaming origin: forced={manager->ForcedStreamingOrigin}, last={manager->LastUpdatedStreamingOrigin}, type={manager->StreamingOriginType}");
        _tree.LeafNode($"Last-update: time={manager->LastUpdateDT:f3}, flip={manager->LastUpdateOdd}");
        DrawStringTable(ref manager->ResourcePaths);

        using (var n = _tree.Node("Resources"))
        {
            if (n.Opened)
            {
                DrawResourceHandle("LVB", manager->LvbResourceHandle);
                DrawResourceHandle("SVB", manager->SvbResourceHandle);
                DrawResourceHandle("LCB", manager->LcbResourceHandle);
                DrawResourceHandle("UWB", manager->UwbResourceHandle);
                int i = 0;
                foreach (var rsrc in manager->LayerGroupResourceHandles)
                    DrawResourceHandle($"LGB {i++}", rsrc.Value);
            }
        }

        using (var n = _tree.Node($"Terrain ({manager->Terrains.Count})###terrains", manager->Terrains.Count == 0))
        {
            if (n.Opened)
            {
                foreach (var (k, v) in manager->Terrains)
                {
                    var nterr = _tree.LeafNode($"{k:X8} = {(nint)v.Value:X}, path={v.Value->PathString}, coll={(nint)v.Value->Collider:X}");
                    if (nterr.SelectedOrHovered && v.Value->Collider != null)
                        _coll.VisualizeCollider(&v.Value->Collider->Collider, default, default);
                }
            }
        }
        using (var n = _tree.Node($"Layers ({manager->Layers.Count})###layers", manager->Layers.Count == 0))
        {
            if (n.Opened)
            {
                foreach (var (lk, lv) in manager->Layers)
                {
                    DrawLayer($"{lk:X4}", manager, lv.Value);
                }
            }
        }
        using (var n = _tree.Node($"Instances ({manager->InstancesByType.Count} types)###insts", manager->InstancesByType.Count == 0))
        {
            if (n.Opened)
            {
                foreach (var (itk, itv) in manager->InstancesByType)
                {
                    using var nt = _tree.Node($"{itk} ({itv.Value->Count} instances)##{itk}");
                    if (nt.Opened)
                    {
                        foreach (var (ik, iv) in *itv.Value)
                        {
                            DrawInstance($"{ik >> 32:X8}.{(uint)ik:X8}", manager, iv.Value);
                        }
                    }
                }
            }
        }

        using (var n = _tree.Node($"Paths ({manager->CrcToPath.Count})###paths", manager->CrcToPath.Count == 0))
        {
            if (n.Opened)
            {
                foreach (var (k, v) in manager->CrcToPath)
                {
                    _tree.LeafNode($"{k:X8} = [{v.Value->NumRefs}] {v.Value->DataString}");
                }
            }
        }
        using (var n = _tree.Node($"Analytic shapes ({manager->CrcToAnalyticShapeData.Count})###shapes", manager->CrcToAnalyticShapeData.Count == 0))
        {
            if (n.Opened)
            {
                foreach (var (k, v) in manager->CrcToAnalyticShapeData)
                {
                    DrawAnalyticShape(_tree, $"{k.Key:X8} =", v);
                }
            }
        }

        using (var n = _tree.Node($"Filters ({manager->Filters.Count})", manager->Filters.Count == 0))
        {
            if (n.Opened)
            {
                var activeFilter = LayoutUtils.FindFilter(manager);
                foreach (var (k, v) in manager->Filters)
                {
                    _tree.LeafNode($"{k:X} = terr={v.Value->TerritoryTypeId} cfc={v.Value->CfcId}{(v.Value == activeFilter ? " (active)" : "")}");
                }
            }
        }
    }

    private void DrawLayer(string tag, LayoutManager* layout, LayerManager* layer)
    {
        var unks = ""; // $", u1F={layer->u1F}, u20={layer->u20:X4}";
        using var nl = _tree.Node($"[{tag}] LG{layer->LayerGroupId}, festival={layer->FestivalId}/{layer->FestivalSubId}, flags={layer->Flags:X}{unks}, {layer->Instances.Count} instances == {(nint)layer:X}", layer->Instances.Count == 0);
        if (!nl.Opened)
            return;
        foreach (var (ik, iv) in layer->Instances)
        {
            DrawInstance($"{ik:X8}", layout, iv.Value);
        }
    }

    private void DrawInstance(string tag, LayoutManager* layout, ILayoutInstance* inst)
    {
        if (DrawInstance(_tree, tag, layout, inst))
        {
            var collider = inst->GetCollider();
            if (collider != null)
            {
                _coll.VisualizeCollider(collider, default, default);
            }
        }
    }

    private void DrawStringTable(ref StringTable strings)
    {
        using var n = _tree.Node($"Strings ({strings.Strings.Count}, {strings.NumNulls} nulls)###strings", strings.Strings.Count == 0);
        if (!n.Opened)
            return;
        foreach (var str in strings.Strings)
            _tree.LeafNode($"[{str.Value->NumRefs}] {str.Value->DataString}");
    }

    private void DrawResourceHandle(string tag, ResourceHandle* rsrc)
    {
        if (rsrc == null)
            _tree.LeafNode($"{tag}: null");
        else
            _tree.LeafNode($"{tag}: {rsrc->FileName}");
    }

    private void DrawFile(string tag, string path)
    {
        using var n = _tree.Node($"{tag}: '{path}'");
        if (!n.Opened)
            return;

        var lgb = Service.DataManager.GetFile(path);
        if (lgb == null)
            return;

        fixed (byte* data = &lgb.Data[0])
            DrawFileData((FileHeader*)data);
    }

    private void DrawFileData(FileHeader* header)
    {
        using var n = _tree.Node($"{(char)(header->Magic & 0xFF)}{(char)((header->Magic >> 8) & 0xFF)}{(char)((header->Magic >> 16) & 0xFF)}{(char)((header->Magic >> 24) & 0xFF)} (size={header->TotalSize}): {header->NumSections} sections");
        if (!n.Opened)
            return;

        foreach (var section in header->Sections)
        {
            var tag = $"{(char)(section->Magic & 0xFF)}{(char)((section->Magic >> 8) & 0xFF)}{(char)((section->Magic >> 16) & 0xFF)}{(char)((section->Magic >> 24) & 0xFF)} (size={section->TotalSize})";
            var _ = section->Magic switch
            {
                0x314E4353 => DrawFileSectionScene(tag, section->Data<FileSceneHeader>()),
                0x3150474C => DrawFileSectionLayerGroup(tag, section->Data<FileLayerGroupHeader>()),
                _ => _tree.LeafNode($"{tag}: unknown").Opened
            };
        }
    }

    private bool DrawFileSectionScene(string tag, FileSceneHeader* header)
    {
        using var n = _tree.Node($"{tag}: general at +{header->OffsetGeneral}");
        if (!n.Opened)
            return false;
        var general = header->General;
        _tree.LeafNode($"Have layer groups: {general->HaveLayerGroups}");
        _tree.LeafNode($"Terrain: {LayoutUtils.ReadString(general->PathTerrain)}");
        _tree.LeafNode($"Env spaces: {general->NumEnvSpaces} at +{general->OffsetEnvSpaces}");
        _tree.LeafNode($"Sky visibility: {LayoutUtils.ReadString(general->PathSkyVisibility)}");
        _tree.LeafNode($"LCB: {LayoutUtils.ReadString(general->PathLCB)} (uw={general->HaveLCBUW})");
        using (var ng = _tree.Node($"Embedded layer groups ({header->NumEmbeddedLayerGroups} at +{header->OffsetEmbeddedLayerGroups})", header->NumEmbeddedLayerGroups == 0))
        {
            if (ng.Opened)
            {
                for (int i = 0; i < header->NumEmbeddedLayerGroups; ++i)
                {
                    DrawFileSectionLayerGroup(i.ToString(), header->EmbeddedLayerGroups.GetPointer(i));
                }
            }
        }
        using (var ng = _tree.Node($"Layer group resources ({header->NumLayerGroupResources} at +{header->OffsetLayerGroupResources})", header->NumLayerGroupResources == 0))
        {
            if (ng.Opened)
            {
                for (int i = 0; i < header->NumLayerGroupResources; ++i)
                {
                    DrawFile($"Layer group {i}", LayoutUtils.ReadString(header->LayerGroupResource(header->LayerGroupResourceOffsets[i])));
                }
            }
        }

        var filterList = header->Filters;
        if (filterList != null)
        {
            using (var nl = _tree.Node($"Filters ({filterList->NumEntries} at +{header->OffsetFilters}+{filterList->OffsetEntries})", filterList->NumEntries == 0))
            {
                if (nl.Opened)
                {
                    foreach (ref var f in filterList->Entries)
                    {
                        var unks = "";// $"; unks: {f.u0} {f.u8} {f.uC} {f.u14} {f.u18}";
                        _tree.LeafNode($"[{f.Key:X}] = terr={f.TerritoryTypeId} cfc={f.CfcId}{unks}");
                    }
                }
            }
        }
        return true;
    }

    private bool DrawFileSectionLayerGroup(string tag, FileLayerGroupHeader* header)
    {
        using var n = _tree.Node($"{tag}: {header->Id} '{LayoutUtils.ReadString(header->LayerGroupName)}', {header->NumLayers} layers at +{header->OffsetLayers}", header->NumLayers == 0);
        if (!n.Opened)
            return false;
        foreach (var layerOffset in header->LayerOffsets)
        {
            var layer = header->Layer(layerOffset);

            var filter = layer->Filter;
            var filterString = filter != null ? $"{filter->Operation} [{string.Join(',', Enumerable.Range(0, filter->NumListEntries).Select(j => $"{filter->Entries[j]:X}"))}]" : "<none>";

            var layerUnks = "";// $", u20={layer->u20}, u1C={layer->u1C}, u10={layer->u10}, u11={layer->u11}";
            using var nl = _tree.Node($"[{layer->Key:X4}] '{LayoutUtils.ReadString(layer->LayerName)}': festival={layer->Festival.Id}/{layer->Festival.Phase}, filter={filterString}, {layer->NumInstances} instances at +{layer->OffsetInstances}{layerUnks}, offset=+{header->OffsetLayers}+{layerOffset}", layer->NumInstances == 0/*, layer->u20 != 0 || layer->u1C != 0 ? 0xff0000ff : 0xffffffff*/);
            if (nl.Opened)
            {
                foreach (var instOffset in layer->InstanceOffsets)
                {
                    var instance = layer->Instance(instOffset);
                    var instUnk = "";// $", u8={instance->u8}";
                    using var ni = _tree.Node($"[{instance->Key:X}] '{LayoutUtils.ReadString(instance->Name)}': type={instance->Type}{instUnk}, trans={instance->Transform.Translation}, rot={instance->Transform.Rotation}, scale={instance->Transform.Scale}, offset=+{layer->OffsetInstances}+{instOffset}");
                    if (ni.Opened)
                    {
                        switch (instance->Type)
                        {
                            case InstanceType.BgPart:
                                var instanceBgPart = (FileLayerGroupInstanceBgPart*)instance;
                                _tree.LeafNode($"Mdl: {LayoutUtils.ReadString(instanceBgPart->PathMdl)}");
                                _tree.LeafNode($"Pcb: {LayoutUtils.ReadString(instanceBgPart->PathPcb)}");
                                _tree.LeafNode($"Collider type: {instanceBgPart->ColliderType}");
                                _tree.LeafNode($"Material: {instanceBgPart->MaterialIdHigh:X8}{instanceBgPart->MaterialIdLow:X8}/{instanceBgPart->MaterialMaskHigh:X8}{instanceBgPart->MaterialMaskLow:X8}");
                                //_tree.LeafNode($"unks: {instanceBgPart->u50} {instanceBgPart->u51} {instanceBgPart->u52} {instanceBgPart->u53} {instanceBgPart->u54} {instanceBgPart->u58}");
                                using (var ns = _tree.Node($"Shape data: +{instanceBgPart->OffsetColliderAnalyticData}", instanceBgPart->OffsetColliderAnalyticData == 0))
                                {
                                    if (ns.Opened)
                                    {
                                        var data = instanceBgPart->ColliderAnalyticData;
                                        _tree.LeafNode($"Type: {data->ColliderType}");
                                        _tree.LeafNode($"Material: {data->MaterialId:X} / {data->MaterialMask:X}");
                                        _tree.LeafNode($"Transform: {data->Transform.Translation} {data->Transform.Rotation} {data->Transform.Scale}");
                                        _tree.LeafNode($"Bounds: {data->Bounds}");
                                        //_tree.LeafNode($"unks: {data->u8} {data->uC}");
                                    }
                                }
                                break;
                            case InstanceType.SharedGroup:
                            case InstanceType.HelperObject:
                                var instancePrefab = (FileLayerGroupInstanceSharedGroup*)instance;
                                DrawFile("Path", LayoutUtils.ReadString(instancePrefab->Path));
                                //_tree.LeafNode($"Unks: types={instancePrefab->u34} {instancePrefab->u40}, other={instancePrefab->u38} {instancePrefab->u3C}");
                                break;
                            case InstanceType.CollisionBox:
                                var instanceCollGen = (FileLayerGroupInstanceCollisionBox*)instance;
                                _tree.LeafNode($"Type: {instanceCollGen->ColliderType}");
                                _tree.LeafNode($"Material: {instanceCollGen->MaterialIdHigh:X8}{instanceCollGen->MaterialIdLow:X8}/{instanceCollGen->MaterialMaskHigh:X8}{instanceCollGen->MaterialMaskLow:X8}");
                                _tree.LeafNode($"Misc: active={instanceCollGen->ActiveByDefault}, layer43h={instanceCollGen->Layer43h}");
                                //_tree.LeafNode($"Unks: u34={instanceCollGen->u34}");
                                _tree.LeafNode($"Pcb: {LayoutUtils.ReadString(instanceCollGen->Path)}");
                                break;
                        }
                    }
                }
            }
        }
        return true;
    }

    private void DrawComparison(LayoutManager* layout)
    {
        var activeFilter = LayoutUtils.FindFilter(layout);
        var terrId = activeFilter != null ? activeFilter->TerritoryTypeId : layout->TerritoryTypeId;
        var cfcId = activeFilter != null ? activeFilter->CfcId : layout->CfcId;

        var terr = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(terrId);
        if (terr == null || layout == null)
            return;

        using var n = _tree.Node($"Comparison: Territory {terrId}/{cfcId} '{terr.Value.Bg}'###comparison");
        if (!n.Opened)
            return;

        var lvb = Service.DataManager.GetFile($"bg/{terr.Value.Bg}.lvb");
        if (lvb != null)
            fixed (byte* lvbData = &lvb.Data[0])
                FillInstancesFromFileScene(FindSection<FileSceneHeader>((FileHeader*)lvbData, 0x314E4353), activeFilter != null ? activeFilter->Key : 0, layout->ActiveFestivals);

        FillInstancesFromGame(layout);

        ImGui.Checkbox("Group by layer group", ref _groupByLayerGroup);
        ImGui.Checkbox("Group by layer", ref _groupByLayer);
        ImGui.Checkbox("Group by instance type", ref _groupByInstanceType);
        ImGui.Checkbox("Group by material", ref _groupByMaterial);
        DrawInstancesByLayerGroup(_insts.Values);
    }

    private T* FindSection<T>(FileHeader* header, uint magic) where T : unmanaged
    {
        foreach (var s in header->Sections)
            if (s->Magic == magic)
                return s->Data<T>();
        return null;
    }

    private void FillInstancesFromFileScene(FileSceneHeader* scene, uint filterId, Span<GameMain.Festival> festivals)
    {
        if (scene == null)
            return;

        for (int i = 0; i < scene->NumEmbeddedLayerGroups; ++i)
        {
            FillInstancesFromFileLayerGroup(scene->EmbeddedLayerGroups.GetPointer(i), filterId, festivals);
        }
        foreach (var off in scene->LayerGroupResourceOffsets)
        {
            var lcb = Service.DataManager.GetFile(LayoutUtils.ReadString(scene->LayerGroupResource(off)));
            if (lcb != null)
                fixed (byte* lcbData = &lcb.Data[0])
                    FillInstancesFromFileLayerGroup(FindSection<FileLayerGroupHeader>((FileHeader*)lcbData, 0x3150474C), filterId, festivals);
        }
    }

    private void FillInstancesFromFileLayerGroup(FileLayerGroupHeader* lg, uint filterId, Span<GameMain.Festival> festivals)
    {
        if (lg == null)
            return;
        foreach (var layerOff in lg->LayerOffsets)
        {
            var layer = lg->Layer(layerOff);
            bool expectedInGame = LayoutUtils.LayerActiveFestival(layer, festivals) && LayoutUtils.LayerActiveFilter(layer, filterId);
            FillInstancesFromFileLayer(layer, lg->Id, layer->Key, 0, 32, expectedInGame);
        }
    }

    private void FillInstancesFromFilePrefab(FileSceneHeader* scene, int layerGroupId, ushort layerId, ulong prefabKey, int subShift, bool expectedInGame)
    {
        if (scene == null || subShift < 0)
            return;
        if (scene->NumLayerGroupResources != 0)
            Service.Log.Error($"Prefab {prefabKey:X} has {scene->NumLayerGroupResources} layer group resources");

        if (scene->NumEmbeddedLayerGroups != 1)
        {
            Service.Log.Error($"Prefab {prefabKey:X} has {scene->NumEmbeddedLayerGroups} embedded layer groups");
            return;
        }
        ref var lg = ref scene->EmbeddedLayerGroups[0];
        if (lg.NumLayers != 1)
        {
            Service.Log.Error($"Prefab {prefabKey:X} has {lg.NumLayers} layers");
            return;
        }
        FillInstancesFromFileLayer(lg.Layer(lg.LayerOffsets[0]), layerGroupId, layerId, prefabKey, subShift, expectedInGame);
    }

    private void FillInstancesFromFileLayer(FileLayerGroupLayer* layer, int layerGroupId, ushort layerId, ulong prefabKey, int subShift, bool expectedInGame)
    {
        foreach (var instOffset in layer->InstanceOffsets)
        {
            var inst = layer->Instance(instOffset);
            var key = prefabKey == 0 ? ((ulong)inst->Key) << 32 : prefabKey | (inst->Key << subShift);
            if (_insts.ContainsKey(key))
            {
                Service.Log.Error($"Duplicate instances with key {key:X16}");
                continue;
            }
            _insts.Add(key, new() { LayerGroupId = layerGroupId, LayerId = layerId, InstanceId = (uint)(key >> 32), SubId = (uint)key, Type = inst->Type, InFile = true, ExpectedToBeInGame = expectedInGame });

            if (inst->Type is InstanceType.SharedGroup or InstanceType.HelperObject)
            {
                var instPrefab = (FileLayerGroupInstanceSharedGroup*)inst;
                var sgb = Service.DataManager.GetFile(LayoutUtils.ReadString(instPrefab->Path));
                if (sgb != null)
                    fixed (byte* sgbData = &sgb.Data[0])
                        FillInstancesFromFilePrefab(FindSection<FileSceneHeader>((FileHeader*)sgbData, 0x314E4353), layerGroupId, layerId, key, subShift - 8, expectedInGame);
            }
        }
    }

    private void FillInstancesFromGame(LayoutManager* layout)
    {
        foreach (var (ikt, ikv) in layout->InstancesByType)
        {
            foreach (var (ik, iv) in *ikv.Value)
            {
                var inst = _insts.GetValueOrDefault(ik);
                if (inst == null)
                    _insts[ik] = inst = new() { LayerGroupId = iv.Value->Layer->LayerGroupId, LayerId = iv.Value->Id.LayerKey, InstanceId = iv.Value->Id.InstanceKey, SubId = iv.Value->SubId, Type = iv.Value->Id.Type, ExpectedToBeInGame = true };
                inst.Instance = iv.Value;
                inst.Collider = iv.Value->GetCollider();
            }
        }
    }

    private void DrawInstancesByLayerGroup(IEnumerable<InstanceData> insts)
    {
        if (_groupByLayerGroup)
        {
            foreach (var g in insts.GroupBy(i => i.LayerGroupId))
            {
                using var n = _tree.Node($"Layer group {g.Key}");
                if (n.Opened)
                {
                    DrawInstancesByLayer(g);
                }
            }
        }
        else
        {
            DrawInstancesByLayer(insts);
        }
    }

    private void DrawInstancesByLayer(IEnumerable<InstanceData> insts)
    {
        if (_groupByLayer)
        {
            foreach (var g in insts.GroupBy(i => i.LayerId))
            {
                using var n = _tree.Node($"Layer {g.Key:X}");
                if (n.Opened)
                {
                    DrawInstancesByType(g);
                }
            }
        }
        else
        {
            DrawInstancesByType(insts);
        }
    }

    private void DrawInstancesByType(IEnumerable<InstanceData> insts)
    {
        if (_groupByInstanceType)
        {
            foreach (var g in insts.GroupBy(i => i.Type))
            {
                using var n = _tree.Node($"Type {g.Key}");
                if (n.Opened)
                {
                    DrawInstancesByMaterial(g);
                }
            }
        }
        else
        {
            DrawInstancesByMaterial(insts);
        }
    }

    private void DrawInstancesByMaterial(IEnumerable<InstanceData> insts)
    {
        if (_groupByMaterial)
        {
            foreach (var m in insts.GroupBy(i =>
            {
                var (m1, m2) = GetMaterial(i.Instance);
                return $"{m1:X}/{m2:X}";
            }))
            {
                using var n = _tree.Node($"Material {m.Key}");
                if (n.Opened)
                    DrawInstances(m);
            }
        }
        else
        {
            DrawInstances(insts);
        }
    }

    private void DrawInstances(IEnumerable<InstanceData> insts)
    {
        foreach (var inst in insts)
        {
            bool inGame = inst.Instance != null;
            var color = !inst.InFile ? 0xff0000ff : inGame != inst.ExpectedToBeInGame ? 0xffff00ff : !inGame ? 0xff00ffff : inst.Collider == null ? 0xff00ff00 : 0xffffffff;
            using var n = _tree.Node($"{inst.Type} {inst.InstanceId:X8}.{inst.SubId:X8} L{inst.LayerId:X4} LG{inst.LayerGroupId:X}: in-file={inst.InFile}, in-game={(nint)inst.Instance:X}, coll={(nint)inst.Collider:X}###{inst.InstanceId:X}.{inst.SubId:X}", inst.Instance == null, color);
            if (n.SelectedOrHovered && inst.Collider != null)
                _coll.VisualizeCollider(inst.Collider, default, default);
            if (!n.Opened)
                continue;
            DrawInstance("Game", inst.Instance->Layout, inst.Instance);
        }
    }

    private static void DrawAnalyticShape(UITree tree, string tag, AnalyticShapeData v)
    {
        var unks = "";//$" {v.u8:X} {v.uC} {v.u3C} {v.u60} {v.u64}";
        tree.LeafNode($"{tag} [{v.NumRefs}] {v.Type}{unks} trans=[{v.Transform.Translation} {v.Transform.Rotation} {v.Transform.Scale}] bb=[{v.BoundsMin}-{v.BoundsMax}], mat={v.MaterialId:X}/{v.MaterialMask:X}");
    }
}
