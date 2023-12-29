using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast.Toolset;
using DotRecast.Recast.Toolset.Builder;
using DotRecast.Recast.Toolset.Geom;
using DotRecast.Recast.Toolset.Tools;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Navmesh;

internal class DebugNavmesh : IDisposable
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

    private DebugGeometry _geom;
    private Task<NavMeshBuildResult>? _navmesh;
    private Vector3 _target;
    private List<Vector3> _waypoints = new();

    public DebugNavmesh(DebugGeometry geom)
    {
        _geom = geom;
    }

    public void Dispose()
    {

    }

    public void Draw()
    {
        if (_navmesh == null)
        {
            if (ImGui.Button("Build navmesh"))
                _navmesh = Task.Run(BuildNavmesh);
            return;
        }
        else if (!_navmesh.IsCompleted)
        {
            using var d = ImRaii.Disabled();
            ImGui.Button("Navmesh is being built...");
            return;
        }
        else
        {
            if (ImGui.Button("Rebuild navmesh"))
            {
                _waypoints.Clear();
                _navmesh = Task.Run(BuildNavmesh);
                return;
            }

            if (_navmesh.IsFaulted)
            {
                ImGui.TextUnformatted($"Failed to build navmesh: {_navmesh.Exception}");
                return;
            }

            var navmesh = _navmesh.Result;
            if (!navmesh.Success)
            {
                ImGui.TextUnformatted($"Failed to build navmesh");
                return;
            }

            if (ImGui.Button("Set target to current pos"))
                _target = Service.ClientState.LocalPlayer?.Position ?? default;
            ImGui.SameLine();
            if (ImGui.Button("Set target to target pos"))
                _target = Service.TargetManager.Target?.Position ?? default;
            ImGui.SameLine();
            ImGui.TextUnformatted($"Current target: {_target}");

            if (ImGui.Button("Pathfind to target"))
                Pathfind(navmesh);

            if (_waypoints.Count > 0)
            {
                var from = _waypoints[0];
                foreach (var to in _waypoints.Skip(1))
                {
                    _geom.DrawWorldLine(from, to, 0xff00ff00);
                    from = to;
                }
            }
        }
    }

    private NavMeshBuildResult BuildNavmesh()
    {
        Service.Log.Debug("[navmesh] start");
        var geom = ExtractGeometry(true, true);

        Service.Log.Debug("[navmesh] to-prov");
        var v = new float[geom.Vertices.Count * 3];
        var f = new int[geom.Triangles.Count * 3];
        int i = 0;
        foreach (var vert in geom.Vertices)
        {
            v[i++] = vert.X;
            v[i++] = vert.Y;
            v[i++] = vert.Z;
        }
        i = 0;
        foreach (var tri in geom.Triangles)
        {
            f[i++] = tri.v1;
            f[i++] = tri.v2;
            f[i++] = tri.v3;
        }
        var geomProv = new DemoInputGeomProvider(v, f);

        Service.Log.Debug("[navmesh] build");
        var settings = new RcNavMeshBuildSettings();
        var navmesh = new SoloNavMeshBuilder().Build(geomProv, settings);

        Service.Log.Debug("[navmesh] end");
        return navmesh;
    }

    private unsafe MegaMesh ExtractGeometry(bool includeStreamedMeshes, bool includeStandaloneMeshes)
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
        //var obj = new StringBuilder();
        //foreach (var v in res.Vertices)
        //    obj.AppendLine($"v {v.X} {v.Y} {v.Z}");
        //foreach (var tri in res.Triangles)
        //    obj.AppendLine($"f {tri.Item1 + 1} {tri.Item2 + 1} {tri.Item3 + 1}");
        //ImGui.SetClipboardText(obj.ToString());

        return res;
    }

    private void Pathfind(NavMeshBuildResult navmesh)
    {
        _waypoints.Clear();
        var src = Service.ClientState.LocalPlayer?.Position ?? default;
        if (_target != default && src != default && navmesh.Success)
        {
            var tool = new RcTestNavMeshTool();
            var query = new DtNavMeshQuery(navmesh.NavMesh);
            var filter = new DtQueryDefaultFilter();
            var success = query.FindNearestPoly(new(src.X, src.Y, src.Z), new(2, 2, 2), filter, out var startRef, out _, out _);
            Service.Log.Debug($"[pathfind] findsrc={success.Value:X} {startRef}");
            success = query.FindNearestPoly(new(_target.X, _target.Y, _target.Z), new(2, 2, 2), filter, out var endRef, out _, out _);
            Service.Log.Debug($"[pathfind] finddst={success.Value:X} {endRef}");
            List<long> polys = new();
            List<RcVec3f> smooth = new();
            success = tool.FindFollowPath(navmesh.NavMesh, query, startRef, endRef, new(src.X, src.Y, src.Z), new(_target.X, _target.Y, _target.Z), filter, true, ref polys, ref smooth);
            Service.Log.Debug($"[pathfind] findpath={success.Value:X}");
            if (success.Succeeded())
            {
                _waypoints.AddRange(smooth.Select(v => new Vector3(v.X, v.Y, v.Z)));
            }
        }
    }
}
