using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Navmesh.Debug;

public class DebugExportObj
{
    private class MegaMesh
    {
        public List<Vector3> Vertices = new();
        public List<(int v1, int v2, int v3)> Triangles = new();

        public unsafe void AddPCB(MeshPCB.FileNode* node, ref FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3 world)
        {
            if (node == null)
                return;
            int firstVertex = Vertices.Count;
            for (int i = 0; i < node->NumVertsRaw + node->NumVertsCompressed; ++i)
                Vertices.Add(world.TransformCoordinate(node->Vertex(i)));
            foreach (ref var p in node->Primitives)
                Triangles.Add((p.V1 + firstVertex, p.V2 + firstVertex, p.V3 + firstVertex));
            AddPCB(node->Child1, ref world);
            AddPCB(node->Child2, ref world);
        }
    }

    public unsafe string BuildObjFromScene(bool includeStreamedMeshes, bool includeStandaloneMeshes)
    {
        var res = new MegaMesh();

        // first pass - mark streamed meshes (so that we can ignore them on standalone mesh pass) and manually load & add full streamable meshes
        HashSet<nint> streamedMeshes = new();
        foreach (var s in FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->BGCollisionModule->SceneManager->Scenes)
        {
            foreach (var coll in s->Scene->Colliders)
            {
                if (coll->GetColliderType() != ColliderType.Streamed)
                    continue;
                var cast = (ColliderStreamed*)coll;
                if (cast->Header == null || cast->Elements == null)
                    continue;
                var basePath = MemoryHelper.ReadStringNullTerminated((nint)cast->PathBase);
                var elements = new Span<ColliderStreamed.Element>(cast->Elements, cast->Header->NumMeshes);
                foreach (ref var e in elements)
                {
                    if (includeStandaloneMeshes && e.Mesh != null)
                    {
                        streamedMeshes.Add((nint)e.Mesh);
                    }
                    if (includeStreamedMeshes)
                    {
                        var f = Service.DataManager.GetFile($"{basePath}/tr{e.MeshId:d4}.pcb");
                        if (f != null)
                        {
                            var data = (MeshPCB.FileHeader*)Unsafe.AsPointer(ref f.Data[0]);
                            if (data->Version is 1 or 4)
                            {
                                res.AddPCB((MeshPCB.FileNode*)(data + 1), ref FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3.Identity);
                            }
                        }
                    }
                }
            }
        }

        // second pass - add standalone meshes
        if (includeStandaloneMeshes)
        {
            foreach (var s in FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->BGCollisionModule->SceneManager->Scenes)
            {
                foreach (var coll in s->Scene->Colliders)
                {
                    if (coll->GetColliderType() != ColliderType.Mesh || streamedMeshes.Contains((nint)coll))
                        continue;
                    var cast = (ColliderMesh*)coll;
                    if (cast->MeshIsSimple || cast->Mesh == null)
                        continue;
                    var mesh = (MeshPCB*)cast->Mesh;
                    res.AddPCB(mesh->RootNode, ref cast->World);
                }
            }
        }

        // print out to clipboard in .obj format
        var obj = new StringBuilder();
        foreach (var v in res.Vertices)
            obj.AppendLine($"v {v.X} {v.Y} {v.Z}");
        foreach (var tri in res.Triangles)
            obj.AppendLine($"f {tri.Item1 + 1} {tri.Item2 + 1} {tri.Item3 + 1}");
        return obj.ToString();
    }
}
