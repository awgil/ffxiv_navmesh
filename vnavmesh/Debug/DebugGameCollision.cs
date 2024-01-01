using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Math;
using ImGuiNET;
using System.Collections.Generic;
using Vector4 = System.Numerics.Vector4;

namespace Navmesh.Debug;

public unsafe class DebugGameCollision
{
    private UITree _tree = new();
    private DebugDrawer _debugGeom;
    private BitMask _shownLayers = new(ulong.MaxValue);
    private bool _showZeroLayer = true;
    private bool _showOnlyFlagRaycast;
    private bool _showOnlyFlagVisit;

    private HashSet<nint> _streamedMeshes = new();
    private BitMask _availableLayers;

    public DebugGameCollision(DebugDrawer geom)
    {
        _debugGeom = geom;
    }

    public void Draw()
    {
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
            ++i;
        }
    }

    private void GatherInfo()
    {
        _streamedMeshes.Clear();
        _availableLayers.Reset();
        foreach (var s in Framework.Instance()->BGCollisionModule->SceneManager->Scenes)
        {
            foreach (var coll in s->Scene->Colliders)
            {
                _availableLayers |= new BitMask(coll->LayerMask);
                if (coll->GetColliderType() == ColliderType.Streamed)
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
        if (n.SelectedOrHovered)
            foreach (var coll in s->Colliders)
                if (FilterCollider(coll))
                    VisualizeCollider(coll);
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
                        VisualizeCollider(coll);
                }
            }
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
        var color = 0xffffffff;
        if (type == ColliderType.Mesh)
        {
            var collMesh = (ColliderMesh*)coll;
            if (_streamedMeshes.Contains((nint)coll))
                color = 0xff00ff00;
            else if (collMesh->MeshIsSimple)
                color = 0xff0000ff;
            else if (collMesh->Resource == null)
                color = 0xff00ffff;
        }
        using var n = _tree.Node($"{type} {(nint)coll:X}, layers={coll->LayerMask:X8}, refs={coll->NumRefs}, material={coll->ObjectMaterialValue:X}/{coll->ObjectMaterialMask:X}, flags={flagsText}", false, color);
        if (ImGui.BeginPopupContextItem())
        {
            ContextCollider(coll);
            ImGui.EndPopup();
        }
        if (n.SelectedOrHovered)
            VisualizeCollider(coll);
        if (!n.Opened)
            return;

        _tree.LeafNode($"Raw flags: {coll->VisibilityFlags:X}");
        switch (type)
        {
            case ColliderType.Streamed:
                {
                    var cast = (ColliderStreamed*)coll;
                    DrawResource(cast->Resource);
                    var path = MemoryHelper.ReadStringNullTerminated((nint)cast->PathBase);
                    _tree.LeafNode($"Path: {path}/{MemoryHelper.ReadStringNullTerminated((nint)cast->PathBase + path.Length + 1)}");
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
                                VisualizeCollider(&elem->Mesh->Collider);
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
                    _tree.LeafNode($"Translation: {Vec3Str(cast->Translation)}");
                    _tree.LeafNode($"Rotation: {Vec3Str(cast->Rotation)}");
                    _tree.LeafNode($"Scale: {Vec3Str(cast->Scale)}");
                    DrawMat4x3("World", ref cast->World);
                    DrawMat4x3("InvWorld", ref cast->InvWorld);
                }
                break;
        }
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
        DrawColliderMeshPCBNode("Root", mesh->RootNode, ref coll->World);
    }

    private void DrawColliderMeshPCBNode(string tag, MeshPCB.FileNode* node, ref Matrix4x3 world)
    {
        if (node == null)
            return;

        using var n = _tree.Node(tag);
        if (n.SelectedOrHovered)
            VisualizeColliderMeshPCBNode(node, ref world, 0xff00ffff);
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
        DrawColliderMeshPCBNode($"Child 1 (+{node->Child1Offset})", node->Child1, ref world);
        DrawColliderMeshPCBNode($"Child 2 (+{node->Child2Offset})", node->Child2, ref world);
    }

    private void DrawResource(Resource* res)
    {
        if (res != null)
        {
            _tree.LeafNode($"Resource: {(nint)res:X} '{MemoryHelper.ReadStringNullTerminated((nint)res->Path)}'");
        }
        else
        {
            _tree.LeafNode($"Resource: null");
        }
    }

    private void VisualizeCollider(Collider* coll)
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
                            VisualizeColliderMesh(elem->Mesh, 0xff00ff00);
                        }
                    }
                }
                break;
            case ColliderType.Mesh:
                VisualizeColliderMesh((ColliderMesh*)coll, _streamedMeshes.Contains((nint)coll) ? 0xff00ff00 : 0xff00ffff);
                break;
            case ColliderType.Box:
                {
                    var cast = (ColliderBox*)coll;
                    //var boxOBB = new AABB() { Min = new(-1), Max = new(+1) };
                    //VisualizeOBB(ref boxOBB, ref cast->World, 0xff0000ff);
                    _debugGeom.DrawBox(ref cast->World, new(1, 0, 0, 1));
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
                    _debugGeom.DrawWorldSphere(cast->Translation, cast->Scale.X, 0xff0000ff);
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
                    _debugGeom.DrawWorldLine(a, b, 0xff0000ff);
                    _debugGeom.DrawWorldLine(b, c, 0xff0000ff);
                    _debugGeom.DrawWorldLine(c, d, 0xff0000ff);
                    _debugGeom.DrawWorldLine(d, a, 0xff0000ff);
                }
                break;
        }
    }

    private void VisualizeColliderMesh(ColliderMesh* coll, uint color)
    {
        if (coll != null && !coll->MeshIsSimple && coll->Mesh != null)
        {
            var mesh = (MeshPCB*)coll->Mesh;
            VisualizeColliderMeshPCBNode(mesh->RootNode, ref coll->World, color);
        }
    }

    private void VisualizeColliderMeshPCBNode(MeshPCB.FileNode* node, ref Matrix4x3 world, uint color)
    {
        if (node == null)
            return;
        //foreach (ref var prim in node->Primitives)
        //    VisualizeTriangle(node, ref prim, ref world, color);
        _debugGeom.DrawMesh(new PCBMesh(node), ref world, new Vector4(color & 0xFF, color >> 8 & 0xFF, color >> 16 & 0xFF, color >> 24 & 0xFF) / 255.0f);
        VisualizeColliderMeshPCBNode(node->Child1, ref world, color);
        VisualizeColliderMeshPCBNode(node->Child2, ref world, color);
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
        _debugGeom.DrawWorldLine(aaa, aab, color);
        _debugGeom.DrawWorldLine(aab, bab, color);
        _debugGeom.DrawWorldLine(bab, baa, color);
        _debugGeom.DrawWorldLine(baa, aaa, color);
        _debugGeom.DrawWorldLine(aba, abb, color);
        _debugGeom.DrawWorldLine(abb, bbb, color);
        _debugGeom.DrawWorldLine(bbb, bba, color);
        _debugGeom.DrawWorldLine(bba, aba, color);
        _debugGeom.DrawWorldLine(aaa, aba, color);
        _debugGeom.DrawWorldLine(aab, abb, color);
        _debugGeom.DrawWorldLine(baa, bba, color);
        _debugGeom.DrawWorldLine(bab, bbb, color);
    }

    private void VisualizeCylinder(ref Matrix4x3 world, uint color)
    {
        int numSegments = CurveApprox.CalculateCircleSegments(world.Row0.Magnitude, 360.Degrees(), 0.1f);
        var prev1 = world.TransformCoordinate(new(0, +1, 1));
        var prev2 = world.TransformCoordinate(new(0, -1, 1));
        for (int i = 1; i <= numSegments; ++i)
        {
            var dir = (i * 360.0f / numSegments).Degrees().ToDirection();
            var curr1 = world.TransformCoordinate(new(dir.X, +1, dir.Y));
            var curr2 = world.TransformCoordinate(new(dir.X, -1, dir.Y));
            _debugGeom.DrawWorldLine(curr1, prev1, color);
            _debugGeom.DrawWorldLine(curr2, prev2, color);
            _debugGeom.DrawWorldLine(curr1, curr2, color);
            prev1 = curr1;
            prev2 = curr2;
        }
    }

    private void VisualizeSphere(Vector4 sphere, uint color) => _debugGeom.DrawWorldSphere(new(sphere.X, sphere.Y, sphere.Z), sphere.W, color);

    private void VisualizeVertex(Vector3 worldPos, uint color) => _debugGeom.DrawWorldSphere(worldPos, 0.1f, color);

    private void VisualizeTriangle(MeshPCB.FileNode* node, ref Mesh.Primitive prim, ref Matrix4x3 world, uint color)
    {
        var v1 = world.TransformCoordinate(node->Vertex(prim.V1));
        var v2 = world.TransformCoordinate(node->Vertex(prim.V2));
        var v3 = world.TransformCoordinate(node->Vertex(prim.V3));
        _debugGeom.DrawWorldLine(v1, v2, color);
        _debugGeom.DrawWorldLine(v2, v3, color);
        _debugGeom.DrawWorldLine(v3, v1, color);
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
}
