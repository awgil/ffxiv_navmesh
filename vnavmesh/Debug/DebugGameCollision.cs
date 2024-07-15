using Dalamud.Hooking;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using ImGuiNET;
using Navmesh.Render;
using System;
using System.Collections.Generic;
using System.Text;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Navmesh.Debug;

public unsafe class DebugGameCollision : IDisposable
{
    private UITree _tree = new();
    private DebugDrawer _dd;
    private BitMask _shownLayers = new(1);
    private BitMask _materialMask;
    private BitMask _materialId;
    private bool _showZeroLayer = true;
    private bool _showOnlyFlagRaycast;
    private bool _showOnlyFlagVisit;

    private HashSet<nint> _streamedMeshes = new();
    private BitMask _availableLayers;
    private BitMask _availableMaterials;

    private EffectMesh.Data _meshDynamicData;
    private EffectMesh.Data.Builder? _meshDynamicBuilder;

    private delegate bool RaycastDelegate(SceneWrapper* self, RaycastHit* result, ulong layerMask, RaycastParams* param);
    private Hook<RaycastDelegate>? _raycastHook;

    public DebugGameCollision(DebugDrawer dd)
    {
        _dd = dd;
        _meshDynamicData = new(dd.RenderContext, 4 * 1024 * 1024, 4 * 1024 * 1024, 512 * 1024, true);

        foreach (var s in Framework.Instance()->BGCollisionModule->SceneManager->Scenes)
        {
            _raycastHook = Service.Hook.HookFromAddress<RaycastDelegate>((nint)s->VirtualTable->Raycast, RaycastDetour);
            break;
        }
    }

    public void Dispose()
    {
        _raycastHook?.Dispose();
        _meshDynamicBuilder?.Dispose();
        _meshDynamicData.Dispose();
    }

    public void Draw()
    {
        if (_raycastHook != null)
        {
            bool hook = _raycastHook.IsEnabled;
            if (ImGui.Checkbox("Log raycasts", ref hook))
                if (hook)
                    _raycastHook.Enable();
                else
                    _raycastHook.Disable();
        }

        var module = Framework.Instance()->BGCollisionModule;
        ImGui.TextUnformatted($"Module: {(nint)module:X}->{(nint)module->SceneManager:X} ({module->SceneManager->NumScenes} scenes, {module->LoadInProgressCounter} loads)");
        ImGui.TextUnformatted($"Streaming: {SphereStr(module->ForcedStreamingSphere)} / {SphereStr(module->SceneManager->StreamingSphere)}");
        //module->ForcedStreamingSphere.W = 10000;

        GatherInfo();
        DrawSettings();

        int i = 0;
        foreach (var s in module->SceneManager->Scenes)
        {
            DrawSceneColliders(s->Scene, i);
            DrawSceneQuadtree(s->Scene->Quadtree, i);
            DrawSceneRaycasts(s, i);
            ++i;
        }
    }

    public void DrawVisualizers()
    {
        if (_meshDynamicBuilder != null)
        {
            _meshDynamicBuilder.Dispose();
            _meshDynamicBuilder = null;
            _dd.EffectMesh?.Draw(_dd.RenderContext, _meshDynamicData);
        }
    }

    private void GatherInfo()
    {
        _streamedMeshes.Clear();
        _availableLayers.Reset();
        _availableMaterials.Reset();
        foreach (var s in Framework.Instance()->BGCollisionModule->SceneManager->Scenes)
        {
            foreach (var coll in s->Scene->Colliders)
            {
                _availableLayers |= new BitMask(coll->LayerMask);
                _availableMaterials |= new BitMask(coll->ObjectMaterialValue);

                var collType = coll->GetColliderType();
                if (collType == ColliderType.Streamed)
                {
                    var cast = (ColliderStreamed*)coll;
                    if (cast->Header != null && cast->Elements != null)
                    {
                        for (int i = 0; i < cast->Header->NumMeshes; ++i)
                        {
                            var m = cast->Elements[i].Mesh;
                            if (m != null)
                                _streamedMeshes.Add((nint)m);
                        }
                    }
                }
                else if (collType == ColliderType.Mesh)
                {
                    var cast = (ColliderMesh*)coll;
                    if (!cast->MeshIsSimple && cast->Mesh != null)
                    {
                        var mesh = (MeshPCB*)cast->Mesh;
                        var mask = new BitMask(coll->ObjectMaterialMask);
                        GatherMeshNodeMaterials(mesh->RootNode, ~mask);
                    }
                }
            }
        }
    }

    private bool FilterCollider(Collider* coll)
    {
        if (coll->LayerMask == 0 ? !_showZeroLayer : (_shownLayers.Raw & coll->LayerMask) == 0)
            return false;
        if (_showOnlyFlagRaycast && (coll->VisibilityFlags & 1) == 0)
            return false;
        if (_showOnlyFlagVisit && (coll->VisibilityFlags & 2) == 0)
            return false;
        var matFilter = _availableMaterials & _materialMask;
        if (matFilter.Any() && coll->GetColliderType() != ColliderType.Mesh)
            return /*_materialId.None() ? (matFilter.Raw & coll->ObjectMaterialValue) != 0 :*/ (matFilter.Raw & (coll->ObjectMaterialValue ^ _materialId.Raw)) == 0;
        return true;
    }

    private void DrawSettings()
    {
        using var n = _tree.Node("Settings");
        if (!n.Opened)
            return;

        ImGui.Checkbox("Show objects with zero layer", ref _showZeroLayer);
        {
            var shownLayers = _availableLayers & _shownLayers;
            using var layers = ImRaii.Combo("Shown layers", shownLayers == _availableLayers ? "All" : shownLayers.None() ? "None" : string.Join(", ", shownLayers.SetBits()));
            if (layers)
            {
                foreach (var i in _availableLayers.SetBits())
                {
                    var shown = _shownLayers[i];
                    if (ImGui.Checkbox($"Layer {i}", ref shown))
                        _shownLayers[i] = shown;
                }
            }
        }

        {
            var matMask = _materialMask & _availableMaterials;
            using var materials = ImRaii.Combo("Material mask", matMask.None() ? "None" : matMask.Raw.ToString("X"));
            if (materials)
            {
                foreach (var i in _availableMaterials.SetBits())
                {
                    var filter = _materialMask[i];
                    if (ImGui.Checkbox($"Material {1u << i:X16}", ref filter))
                        _materialMask[i] = filter;
                }
            }
        }

        {
            var matId = _materialId & _availableMaterials;
            using var materials = ImRaii.Combo("Material id", matId.None() ? "None" : matId.Raw.ToString("X"));
            if (materials)
            {
                foreach (var i in _availableMaterials.SetBits())
                {
                    var filter = _materialId[i];
                    if (ImGui.Checkbox($"Material {1u << i:X16}", ref filter))
                        _materialId[i] = filter;
                }
            }
        }

        {
            using var flags = ImRaii.Combo("Flag filter", _showOnlyFlagRaycast ? _showOnlyFlagVisit ? "Only when both flags are set" : "Only if raycast flag is set" : _showOnlyFlagVisit ? "Only if global visit flag is set" : "Show everything");
            if (flags)
            {
                ImGui.Checkbox("Hide objects without raycast flag (0x1)", ref _showOnlyFlagRaycast);
                ImGui.Checkbox("Hide objects without global viist flag (0x2)", ref _showOnlyFlagVisit);
            }
        }
    }

    private void DrawSceneColliders(Scene* s, int index)
    {
        using var n = _tree.Node($"Scene {index}: {s->NumColliders} colliders, {s->NumLoading} loading, streaming={SphereStr(s->StreamingSphere)}###scene_{index}");
        if (n.SelectedOrHovered || Service.Config.ForceShowGameCollision)
            foreach (var coll in s->Colliders)
                if (FilterCollider(coll))
                    VisualizeCollider(coll, _materialId, _materialMask);
        if (n.Opened)
            foreach (var coll in s->Colliders)
                DrawCollider(coll);
    }

    private void DrawSceneQuadtree(Quadtree* tree, int index)
    {
        using var n = _tree.Node($"Quadtree {index}: {tree->NumLevels} levels ([{tree->MinX}, {tree->MaxX}]x[{tree->MinZ}, {tree->MaxZ}], leaf {tree->LeafSizeX}x{tree->LeafSizeZ}), {tree->NumNodes} nodes###tree_{index}");
        if (!n.Opened)
            return;

        for (int level = 0; level < tree->NumLevels; ++level)
        {
            var cellSizeX = (tree->MaxX - tree->MinX + 1) / (1 << level);
            var cellSizeZ = (tree->MaxZ - tree->MinZ + 1) / (1 << level);
            using var ln = _tree.Node($"Level {level}, {cellSizeX}x{cellSizeZ} cells ({Quadtree.NumNodesAtLevel(level)} nodes starting at {Quadtree.StartingNodeForLevel(level)})");
            if (!ln.Opened)
                continue;

            var nodes = tree->NodesAtLevel(level);
            for (int i = 0; i < nodes.Length; ++i)
            {
                ref var node = ref nodes[i];
                if (node.Node.NodeLink.Next == null)
                    continue;

                var coord = Quadtree.CellCoords((uint)i);
                var cellX = tree->MinX + coord.x * cellSizeX;
                var cellZ = tree->MinZ + coord.z * cellSizeZ;
                using var cn = _tree.Node($"[{coord.x}, {coord.z}] ([{cellX}x{cellZ}]-[{cellX + cellSizeX}x{cellZ + cellSizeZ}])###node_{level}_{i}", node.Node.NodeLink.Next == null);

                if (cn.Opened)
                    foreach (var coll in node.Colliders)
                        DrawCollider(coll);

                if (cn.SelectedOrHovered)
                {
                    // TODO: visualize cell bounds?
                    foreach (var coll in node.Colliders)
                        VisualizeCollider(coll, _materialId, _materialMask);
                }
            }
        }
    }

    private void DrawSceneRaycasts(SceneWrapper* s, int index)
    {
        using var n = _tree.Node($"Scene {index}: raycasts");
        if (!n.Opened)
            return;

        var screenPos = ImGui.GetMousePos() - ImGuiHelpers.MainViewport.Pos;
        var windowSize = ImGuiHelpers.MainViewport.Size;
        if (screenPos.X < 0 || screenPos.X > windowSize.X || screenPos.Y < 0 || screenPos.Y > windowSize.Y)
        {
            _tree.LeafNode("Mouse is outside window");
            return;
        }

        var clipPos = new Vector3(2 * screenPos.X / _dd.ViewportSize.X - 1, 1 - 2 * screenPos.Y / _dd.ViewportSize.Y, 1);
        Matrix4x4.Invert(_dd.ViewProj, out var invViewProj);
        var cameraPosAtPlaneP = Vector4.Transform(clipPos, invViewProj);
        var cameraPosAtPlane = new Vector3(cameraPosAtPlaneP.X / cameraPosAtPlaneP.W, cameraPosAtPlaneP.Y / cameraPosAtPlaneP.W, cameraPosAtPlaneP.Z / cameraPosAtPlaneP.W);
        var dir = Vector3.Normalize(cameraPosAtPlane - _dd.Origin);
        _tree.LeafNode($"Mouse pos: screen={screenPos}, clip={clipPos}, dir={dir}");
        float maxDist = 100000;
        var filter = new RaycastMaterialFilter() { Mask = _materialMask.Raw, Value = _materialId.Raw };
        var res = new RaycastHit();
        var sphere = new Vector4(_dd.Origin, 1);
        var arg = new RaycastParams() { Origin = &sphere, Direction = &dir, MaxDistance = &maxDist, MaterialFilter = &filter };
        if (s->Raycast(&res, _shownLayers.Raw, &arg))
        {
            _tree.LeafNode($"Raycast: {_dd.Origin} + {res.Distance} = {res.Point}");
            var ab = res.V2 - res.V1;
            var ac = res.V3 - res.V1;
            var normal = Vector3.Normalize(Vector3.Cross(ab, ac));
            _tree.LeafNode($"Normal: {normal} (slope={Angle.Acos(normal.Y)})");
            _tree.LeafNode($"Material: {res.Material:X}");
            DrawCollider(res.Object);
            VisualizeCollider(res.Object, _materialId, _materialMask);
            _dd.DrawWorldLine(res.V1, res.V2, 0xff0000ff, 2);
            _dd.DrawWorldLine(res.V2, res.V3, 0xff0000ff, 2);
            _dd.DrawWorldLine(res.V3, res.V1, 0xff0000ff, 2);
        }
        else
        {
            _tree.LeafNode($"Raycast: N/A");
        }
    }

    private void DrawCollider(Collider* coll)
    {
        if (!FilterCollider(coll))
            return;

        var raycastFlag = (coll->VisibilityFlags & 1) != 0;
        var globalVisitFlag = (coll->VisibilityFlags & 2) != 0;
        var flagsText = raycastFlag ? globalVisitFlag ? "raycast, global visit" : "raycast" : globalVisitFlag ? "global visit" : "none";

        var type = coll->GetColliderType();
        var layoutInstance = LayoutUtils.FindInstance(LayoutWorld.Instance()->ActiveLayout, (coll->LayoutObjectId << 32) | (coll->LayoutObjectId >> 32));
        var color = layoutInstance == null || layoutInstance->Id.Type is not InstanceType.BgPart and not InstanceType.CollisionBox ? 0xff00ffff : 0xffffffff;
        if (type == ColliderType.Mesh)
        {
            var collMesh = (ColliderMesh*)coll;
            if (_streamedMeshes.Contains((nint)coll))
                color = 0xff00ff00;
            else if (collMesh->MeshIsSimple)
                color = 0xff0000ff;
        }
        using var n = _tree.Node($"{type} {(nint)coll:X}, layers={coll->LayerMask:X8}, layout-id={coll->LayoutObjectId:X16}, refs={coll->NumRefs}, material={coll->ObjectMaterialValue:X}/{coll->ObjectMaterialMask:X}, flags={flagsText}###{(nint)coll:X}", false, color);
        if (ImGui.BeginPopupContextItem())
        {
            ContextCollider(coll);
            ImGui.EndPopup();
        }
        if (n.SelectedOrHovered)
            VisualizeCollider(coll, _materialId, _materialMask);
        if (!n.Opened)
            return;

        _tree.LeafNode($"Raw flags: {coll->VisibilityFlags:X}");
        switch (type)
        {
            case ColliderType.Streamed:
                {
                    var cast = (ColliderStreamed*)coll;
                    DrawResource(cast->Resource);
                    var path = cast->PathBaseString;
                    _tree.LeafNode($"Path: {path}/{Encoding.UTF8.GetString(cast->PathBase[(path.Length + 1)..])}");
                    _tree.LeafNode($"Streamed: [{cast->StreamedMinX:f3}x{cast->StreamedMinZ:f3}] - [{cast->StreamedMaxX:f3}x{cast->StreamedMaxZ:f3}]");
                    _tree.LeafNode($"Loaded: {cast->Loaded} ({cast->NumMeshesLoading} meshes load in progress)");
                    if (cast->Header != null && cast->Entries != null && cast->Elements != null)
                    {
                        var headerRaw = (float*)cast->Header;
                        _tree.LeafNode($"Header: meshes={cast->Header->NumMeshes}, u={headerRaw[1]:f3} {headerRaw[2]:f3} {headerRaw[3]:f3} {headerRaw[4]:f3} {headerRaw[5]:f3} {headerRaw[6]:f3} {headerRaw[7]:f3}");
                        for (int i = 0; i < cast->Header->NumMeshes; ++i)
                        {
                            var entry = cast->Entries + i;
                            var elem = cast->Elements + i;
                            var entryRaw = (uint*)entry;
                            using var mn = _tree.Node($"Mesh {i}: file=tr{entry->MeshId:d4}.pcb, bounds={AABBStr(entry->Bounds)} == {(nint)elem->Mesh:X}###mesh_{i}", elem->Mesh == null);
                            if (mn.SelectedOrHovered && elem->Mesh != null)
                                VisualizeCollider(&elem->Mesh->Collider, _materialId, _materialMask);
                            if (mn.Opened)
                                DrawColliderMesh(elem->Mesh);
                        }
                    }
                }
                break;
            case ColliderType.Mesh:
                DrawColliderMesh((ColliderMesh*)coll);
                break;
            case ColliderType.Box:
                {
                    var cast = (ColliderBox*)coll;
                    _tree.LeafNode($"Translation: {Vec3Str(cast->Translation)}");
                    _tree.LeafNode($"Rotation: {Vec3Str(cast->Rotation)}");
                    _tree.LeafNode($"Scale: {Vec3Str(cast->Scale)}");
                    DrawMat4x3("World", ref cast->World);
                    DrawMat4x3("InvWorld", ref cast->InvWorld);
                }
                break;
            case ColliderType.Cylinder:
                {
                    var cast = (ColliderCylinder*)coll;
                    _tree.LeafNode($"Translation: {Vec3Str(cast->Translation)}");
                    _tree.LeafNode($"Rotation: {Vec3Str(cast->Rotation)}");
                    _tree.LeafNode($"Scale: {Vec3Str(cast->Scale)}");
                    _tree.LeafNode($"Radius: {cast->Radius:f3}");
                    DrawMat4x3("World", ref cast->World);
                    DrawMat4x3("InvWorld", ref cast->InvWorld);
                }
                break;
            case ColliderType.Sphere:
                {
                    var cast = (ColliderSphere*)coll;
                    _tree.LeafNode($"Translation: {Vec3Str(cast->Translation)}");
                    _tree.LeafNode($"Rotation: {Vec3Str(cast->Rotation)}");
                    _tree.LeafNode($"Scale: {Vec3Str(cast->Scale)}");
                    DrawMat4x3("World", ref cast->World);
                    DrawMat4x3("InvWorld", ref cast->InvWorld);
                }
                break;
            case ColliderType.Plane:
            case ColliderType.PlaneTwoSided:
                {
                    var cast = (ColliderPlane*)coll;
                    _tree.LeafNode($"Normal: {cast->World.Row2 / cast->Scale.Z:f3}");
                    _tree.LeafNode($"Translation: {Vec3Str(cast->Translation)}");
                    _tree.LeafNode($"Rotation: {Vec3Str(cast->Rotation)}");
                    _tree.LeafNode($"Scale: {Vec3Str(cast->Scale)}");
                    DrawMat4x3("World", ref cast->World);
                    DrawMat4x3("InvWorld", ref cast->InvWorld);
                }
                break;
        }

        if (layoutInstance != null)
            DebugLayout.DrawInstance(_tree, "Layout instance:", LayoutWorld.Instance()->ActiveLayout, layoutInstance);
    }

    private void DrawColliderMesh(ColliderMesh* coll)
    {
        DrawResource(coll->Resource);
        _tree.LeafNode($"Translation: {Vec3Str(coll->Translation)}");
        _tree.LeafNode($"Rotation: {Vec3Str(coll->Rotation)}");
        _tree.LeafNode($"Scale: {Vec3Str(coll->Scale)}");
        DrawMat4x3("World", ref coll->World);
        DrawMat4x3("InvWorld", ref coll->InvWorld);
        if (_tree.LeafNode($"Bounding sphere: {SphereStr(coll->BoundingSphere)}").SelectedOrHovered)
            VisualizeSphere(coll->BoundingSphere, 0xff00ff00);
        if (_tree.LeafNode($"Bounding box: {AABBStr(coll->WorldBoundingBox)}").SelectedOrHovered)
            VisualizeOBB(ref coll->WorldBoundingBox, ref Matrix4x3.Identity, 0xff00ff00);
        _tree.LeafNode($"Total size: {coll->TotalPrimitives} prims, {coll->TotalChildren} nodes");
        _tree.LeafNode($"Mesh type: {(coll->MeshIsSimple ? "simple" : coll->MemoryData != null ? "PCB in-memory" : "PCB from file")} {(coll->Loaded ? "" : "(loading)")}");
        if (coll->Mesh == null || coll->MeshIsSimple)
            return;

        var mesh = (MeshPCB*)coll->Mesh;
        DrawColliderMeshPCBNode("Root", mesh->RootNode, ref coll->World, coll->Collider.ObjectMaterialValue & coll->Collider.ObjectMaterialMask, ~coll->Collider.ObjectMaterialMask);
    }

    private void DrawColliderMeshPCBNode(string tag, MeshPCB.FileNode* node, ref Matrix4x3 world, ulong objMatId, ulong objMatInvMask)
    {
        if (node == null)
            return;

        using var n = _tree.Node(tag);
        if (n.SelectedOrHovered)
            VisualizeColliderMeshPCBNode(node, ref world, new(1, 1, 0, 0.7f), objMatId, objMatId, _materialId, _materialMask);
        if (!n.Opened)
            return;

        _tree.LeafNode($"Header: {node->Header:X16}");
        if (_tree.LeafNode($"AABB: {AABBStr(node->LocalBounds)}").SelectedOrHovered)
            VisualizeOBB(ref node->LocalBounds, ref world, 0xff00ff00);

        {
            using var nv = _tree.Node($"Vertices: {node->NumVertsRaw}+{node->NumVertsCompressed}", node->NumVertsRaw + node->NumVertsCompressed == 0);
            if (nv.Opened)
            {
                for (int i = 0; i < node->NumVertsRaw + node->NumVertsCompressed; ++i)
                {
                    var v = node->Vertex(i);
                    if (_tree.LeafNode($"[{i}] ({(i < node->NumVertsRaw ? 'r' : 'c')}): {Vec3Str(v)}").SelectedOrHovered)
                        VisualizeVertex(world.TransformCoordinate(v), 0xff00ffff);
                }
            }
        }
        {
            using var np = _tree.Node($"Primitives: {node->NumPrims}", node->NumPrims == 0);
            if (np.Opened)
            {
                int i = 0;
                foreach (ref var prim in node->Primitives)
                    if (_tree.LeafNode($"[{i++}]: {prim.V1}x{prim.V2}x{prim.V3}, material={prim.Material:X8}").SelectedOrHovered)
                        VisualizeTriangle(node, ref prim, ref world, 0xff00ffff);
            }
        }
        DrawColliderMeshPCBNode($"Child 1 (+{node->Child1Offset})", node->Child1, ref world, objMatId, objMatId);
        DrawColliderMeshPCBNode($"Child 2 (+{node->Child2Offset})", node->Child2, ref world, objMatId, objMatId);
    }

    private void DrawResource(Resource* res)
    {
        if (res != null)
        {
            _tree.LeafNode($"Resource: {(nint)res:X} '{res->PathString}'");
        }
        else
        {
            _tree.LeafNode($"Resource: null");
        }
    }

    public void VisualizeCollider(Collider* coll, BitMask filterId, BitMask filterMask)
    {
        switch (coll->GetColliderType())
        {
            case ColliderType.Streamed:
                {
                    var cast = (ColliderStreamed*)coll;
                    if (cast->Header != null && cast->Elements != null)
                    {
                        for (int i = 0; i < cast->Header->NumMeshes; ++i)
                        {
                            var elem = cast->Elements + i;
                            VisualizeColliderMesh(elem->Mesh, new(0, 1, 0, 0.7f), _materialId, _materialMask);
                        }
                    }
                }
                break;
            case ColliderType.Mesh:
                VisualizeColliderMesh((ColliderMesh*)coll, new(_streamedMeshes.Contains((nint)coll) ? 0 : 1, 1, 0, 0.7f), _materialId, _materialMask);
                break;
            case ColliderType.Box:
                {
                    var cast = (ColliderBox*)coll;
                    //var boxOBB = new AABB() { Min = new(-1), Max = new(+1) };
                    //VisualizeOBB(ref boxOBB, ref cast->World, 0xff0000ff);
                    var render = GetDynamicMeshes();
                    var box = new AnalyticMeshBox(render);
                    var icnt = render.NumInstances;
                    render.AddInstance(new(cast->World, new(1, 0, 0, 0.7f)));
                    render.AddMesh(box.FirstVertex, box.FirstPrimitive, box.NumPrimitives, icnt, 1);
                }
                break;
            case ColliderType.Cylinder:
                {
                    var cast = (ColliderCylinder*)coll;
                    VisualizeCylinder(ref cast->World, 0xff0000ff);
                }
                break;
            case ColliderType.Sphere:
                {
                    var cast = (ColliderSphere*)coll;
                    _dd.DrawWorldSphere(cast->Translation, cast->Scale.X, 0xff0000ff);
                }
                break;
            case ColliderType.Plane:
            case ColliderType.PlaneTwoSided:
                {
                    var cast = (ColliderPlane*)coll;
                    var a = cast->World.TransformCoordinate(new(-1, +1, 0));
                    var b = cast->World.TransformCoordinate(new(-1, -1, 0));
                    var c = cast->World.TransformCoordinate(new(+1, -1, 0));
                    var d = cast->World.TransformCoordinate(new(+1, +1, 0));
                    _dd.DrawWorldLine(a, b, 0xff0000ff);
                    _dd.DrawWorldLine(b, c, 0xff0000ff);
                    _dd.DrawWorldLine(c, d, 0xff0000ff);
                    _dd.DrawWorldLine(d, a, 0xff0000ff);
                }
                break;
        }
    }

    private void VisualizeColliderMesh(ColliderMesh* coll, Vector4 color, BitMask filterId, BitMask filterMask)
    {
        if (coll != null && !coll->MeshIsSimple && coll->Mesh != null)
        {
            var mesh = (MeshPCB*)coll->Mesh;
            VisualizeColliderMeshPCBNode(mesh->RootNode, ref coll->World, color, coll->Collider.ObjectMaterialValue & coll->Collider.ObjectMaterialMask, ~coll->Collider.ObjectMaterialMask, filterId, filterMask);
        }
    }

    private void VisualizeColliderMeshPCBNode(MeshPCB.FileNode* node, ref Matrix4x3 world, Vector4 color, ulong objMatId, ulong objMatInvMask, BitMask filterId, BitMask filterMask)
    {
        if (node == null)
            return;

        if (node->NumPrims > 0)
        {
            var renderer = GetDynamicMeshes();
            renderer.AddMesh(renderer.NumVertices, renderer.NumPrimitives, node->NumPrims, renderer.NumInstances, 1);
            for (int i = 0; i < node->NumVertsRaw + node->NumVertsCompressed; ++i)
                renderer.AddVertex(node->Vertex(i));
            foreach (ref var prim in node->Primitives)
            {
                bool pass = true;
                if (filterMask.Any())
                {
                    var effMat = objMatId | objMatInvMask & prim.Material;
                    pass = /*filterId.None() ? (filterMask.Raw & effMat) != 0 :*/ (filterMask.Raw & (effMat ^ filterId.Raw)) == 0;
                }
                if (pass)
                {
                    renderer.AddTriangle(prim.V1, prim.V3, prim.V2); // change winding to what dx expects
                }
                else
                {
                    renderer.AddTriangle(prim.V1, prim.V1, prim.V1); // TODO: avoid degenerates...
                }
                renderer.AddInstance(new(world, color));
            }
        }

        VisualizeColliderMeshPCBNode(node->Child1, ref world, color, objMatId, objMatInvMask, filterId, filterMask);
        VisualizeColliderMeshPCBNode(node->Child2, ref world, color, objMatId, objMatInvMask, filterId, filterMask);
    }

    private void VisualizeOBB(ref AABB localBB, ref Matrix4x3 world, uint color)
    {
        var aaa = world.TransformCoordinate(new(localBB.Min.X, localBB.Min.Y, localBB.Min.Z));
        var aab = world.TransformCoordinate(new(localBB.Min.X, localBB.Min.Y, localBB.Max.Z));
        var aba = world.TransformCoordinate(new(localBB.Min.X, localBB.Max.Y, localBB.Min.Z));
        var abb = world.TransformCoordinate(new(localBB.Min.X, localBB.Max.Y, localBB.Max.Z));
        var baa = world.TransformCoordinate(new(localBB.Max.X, localBB.Min.Y, localBB.Min.Z));
        var bab = world.TransformCoordinate(new(localBB.Max.X, localBB.Min.Y, localBB.Max.Z));
        var bba = world.TransformCoordinate(new(localBB.Max.X, localBB.Max.Y, localBB.Min.Z));
        var bbb = world.TransformCoordinate(new(localBB.Max.X, localBB.Max.Y, localBB.Max.Z));
        _dd.DrawWorldLine(aaa, aab, color);
        _dd.DrawWorldLine(aab, bab, color);
        _dd.DrawWorldLine(bab, baa, color);
        _dd.DrawWorldLine(baa, aaa, color);
        _dd.DrawWorldLine(aba, abb, color);
        _dd.DrawWorldLine(abb, bbb, color);
        _dd.DrawWorldLine(bbb, bba, color);
        _dd.DrawWorldLine(bba, aba, color);
        _dd.DrawWorldLine(aaa, aba, color);
        _dd.DrawWorldLine(aab, abb, color);
        _dd.DrawWorldLine(baa, bba, color);
        _dd.DrawWorldLine(bab, bbb, color);
    }

    private void VisualizeCylinder(ref Matrix4x3 world, uint color)
    {
        int numSegments = CurveApprox.CalculateCircleSegments(world.Row0.Length(), 360.Degrees(), 0.1f);
        var prev1 = world.TransformCoordinate(new(0, +1, 1));
        var prev2 = world.TransformCoordinate(new(0, -1, 1));
        for (int i = 1; i <= numSegments; ++i)
        {
            var dir = (i * 360.0f / numSegments).Degrees().ToDirection();
            var curr1 = world.TransformCoordinate(new(dir.X, +1, dir.Y));
            var curr2 = world.TransformCoordinate(new(dir.X, -1, dir.Y));
            _dd.DrawWorldLine(curr1, prev1, color);
            _dd.DrawWorldLine(curr2, prev2, color);
            _dd.DrawWorldLine(curr1, curr2, color);
            prev1 = curr1;
            prev2 = curr2;
        }
    }

    private void VisualizeSphere(Vector4 sphere, uint color) => _dd.DrawWorldSphere(new(sphere.X, sphere.Y, sphere.Z), sphere.W, color);

    private void VisualizeVertex(Vector3 worldPos, uint color) => _dd.DrawWorldSphere(worldPos, 0.1f, color);

    private void VisualizeTriangle(MeshPCB.FileNode* node, ref Mesh.Primitive prim, ref Matrix4x3 world, uint color)
    {
        var v1 = world.TransformCoordinate(node->Vertex(prim.V1));
        var v2 = world.TransformCoordinate(node->Vertex(prim.V2));
        var v3 = world.TransformCoordinate(node->Vertex(prim.V3));
        _dd.DrawWorldLine(v1, v2, color);
        _dd.DrawWorldLine(v2, v3, color);
        _dd.DrawWorldLine(v3, v1, color);
    }

    private void GatherMeshNodeMaterials(MeshPCB.FileNode* node, BitMask invMask)
    {
        if (node == null)
            return;
        foreach (ref var prim in node->Primitives)
            _availableMaterials |= invMask & new BitMask(prim.Material);
        GatherMeshNodeMaterials(node->Child1, invMask);
        GatherMeshNodeMaterials(node->Child2, invMask);
    }

    private string SphereStr(Vector4 s) => $"[{s.X:f3}, {s.Y:f3}, {s.Z:f3}] R{s.W:f3}";
    private string Vec3Str(Vector3 v) => $"[{v.X:f3}, {v.Y:f3}, {v.Z:f3}]";
    private string AABBStr(AABB bb) => $"{Vec3Str(bb.Min)} - {Vec3Str(bb.Max)}";

    private void DrawMat4x3(string tag, ref Matrix4x3 mat)
    {
        _tree.LeafNode($"{tag} R0: {Vec3Str(mat.Row0)}");
        _tree.LeafNode($"{tag} R1: {Vec3Str(mat.Row1)}");
        _tree.LeafNode($"{tag} R2: {Vec3Str(mat.Row2)}");
        _tree.LeafNode($"{tag} R3: {Vec3Str(mat.Row3)}");
    }

    private void ContextCollider(Collider* coll)
    {
        var activeLayers = new BitMask(coll->LayerMask);
        foreach (var i in _availableLayers.SetBits())
        {
            var active = activeLayers[i];
            if (ImGui.Checkbox($"Layer {i}", ref active))
            {
                activeLayers[i] = active;
                coll->LayerMask = activeLayers.Raw;
            }
        }

        var raycast = (coll->VisibilityFlags & 1) != 0;
        if (ImGui.Checkbox("Flag: raycast", ref raycast))
            coll->VisibilityFlags ^= 1;

        var globalVisit = (coll->VisibilityFlags & 2) != 0;
        if (ImGui.Checkbox("Flag: global visit", ref globalVisit))
            coll->VisibilityFlags ^= 2;
    }

    private EffectMesh.Data.Builder GetDynamicMeshes() => _meshDynamicBuilder ??= _meshDynamicData.Map(_dd.RenderContext);

    private bool RaycastDetour(SceneWrapper* self, RaycastHit* result, ulong layerMask, RaycastParams* param)
    {
        Service.Log.Debug($"Raycast: layer={layerMask:X}, algo={param->Algorithm}, origin={*param->Origin}, dir={*param->Direction}, maxnorm={param->MaxPlaneNormalY}, maxdist={*param->MaxDistance}, filter={param->MaterialFilter->Value:X}/{param->MaterialFilter->Mask:X}");
        return _raycastHook!.Original(self, result, layerMask, param);
    }
}
