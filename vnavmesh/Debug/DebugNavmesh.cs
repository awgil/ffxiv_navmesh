using Dalamud.Interface.Utility.Raii;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Navmesh.Debug;

internal class DebugNavmesh : IDisposable
{
    private NavmeshBuilder _navmesh;
    private Vector3 _target;
    private List<Vector3> _waypoints = new();

    private UITree _tree = new();
    private DebugDrawer _dd;
    private DebugExtractedCollision? _drawExtracted;
    private DebugSolidHeightfield? _drawSolidHeightfield;
    private DebugCompactHeightfield? _drawCompactHeightfield;
    private DebugContourSet? _drawContourSet;

    public DebugNavmesh(DebugDrawer dd, NavmeshBuilder navmesh)
    {
        _navmesh = navmesh;
        _dd = dd;
    }

    public void Dispose()
    {
        _drawExtracted?.Dispose();
        _drawSolidHeightfield?.Dispose();
        _drawCompactHeightfield?.Dispose();
        _drawContourSet?.Dispose();
    }

    public void Draw()
    {
        DrawConfig();

        using (var d = ImRaii.Disabled(_navmesh.CurrentState == NavmeshBuilder.State.InProgress))
        {
            if (ImGui.Button("Rebuild navmesh"))
            {
                _drawExtracted?.Dispose();
                _drawExtracted = null;
                _drawSolidHeightfield?.Dispose();
                _drawSolidHeightfield = null;
                _drawCompactHeightfield?.Dispose();
                _drawCompactHeightfield = null;
                _drawContourSet?.Dispose();
                _drawContourSet = null;
                _navmesh.Rebuild();
                _waypoints.Clear();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"State: {_navmesh.CurrentState}");
        }

        if (_navmesh.CurrentState != NavmeshBuilder.State.Ready)
            return;

        if (ImGui.Button("Set target to current pos"))
            _target = Service.ClientState.LocalPlayer?.Position ?? default;
        ImGui.SameLine();
        if (ImGui.Button("Set target to target pos"))
            _target = Service.TargetManager.Target?.Position ?? default;
        ImGui.SameLine();
        ImGui.TextUnformatted($"Current target: {_target}");

        if (ImGui.Button("Pathfind to target"))
            _waypoints = _navmesh.Pathfind(Service.ClientState.LocalPlayer?.Position ?? default, _target);

        if (_waypoints.Count > 0)
        {
            var from = _waypoints[0];
            foreach (var to in _waypoints.Skip(1))
            {
                _dd.DrawWorldLine(from, to, 0xff00ff00);
                from = to;
            }
        }

        var intermediates = _navmesh.Intermediates!;
        _drawExtracted ??= new(_navmesh.CollisionGeometry, _tree, _dd);
        _drawExtracted.Draw();
        _drawSolidHeightfield ??= new(intermediates.GetSolidHeightfield(), _tree, _dd);
        _drawSolidHeightfield.Draw();
        _drawCompactHeightfield ??= new(intermediates.GetCompactHeightfield(), _tree, _dd);
        _drawCompactHeightfield.Draw();
        _drawContourSet ??= new(intermediates.GetContourSet(), _tree, _dd);
        _drawContourSet.Draw();
        DrawPolyMesh(intermediates.GetMesh());
    }

    private void DrawConfig()
    {
        using var n = _tree.Node("Navmesh properties");
        if (!n.Opened)
            return;

        ImGui.InputFloat("The xz-plane cell size to use for fields. [Limit: > 0] [Units: wu]", ref _navmesh.CellSize);
        ImGui.InputFloat("The y-axis cell size to use for fields. [Limit: > 0] [Units: wu]", ref _navmesh.CellHeight);
        ImGui.InputFloat("The maximum slope that is considered walkable. [Limits: 0 <= value < 90] [Units: Degrees]", ref _navmesh.AgentMaxSlopeDeg);
        ImGui.InputFloat("Maximum ledge height that is considered to still be traversable. [Limit: >= 0] [Units: wu]", ref _navmesh.AgentMaxClimb);
        ImGui.InputFloat("Minimum floor to 'ceiling' height that will still allow the floor area to be considered walkable. [Limit: >= 3 * CellHeight] [Units: wu]", ref _navmesh.AgentHeight);
        ImGui.Checkbox("Filter low-hanging obstacles", ref _navmesh.FilterLowHangingObstacles);
        ImGui.Checkbox("Filter ledges", ref _navmesh.FilterLedgeSpans);
        ImGui.Checkbox("Filter low-height spans", ref _navmesh.FilterWalkableLowHeightSpans);
    }

    private void DrawPolyMesh(RcPolyMesh mesh)
    {
        using var n = _tree.Node("Poly mesh");
        if (n.SelectedOrHovered)
            VisualizeMesh(mesh);
        if (!n.Opened)
            return;

        var playerPos = Service.ClientState.LocalPlayer?.Position ?? default;
        _tree.LeafNode($"Bounds: [{mesh.bmin}] - [{mesh.bmax}]");
        _tree.LeafNode($"Cell size: {mesh.cs}x{mesh.ch}");
        _tree.LeafNode($"Misc: border size={mesh.borderSize}, max edge error={mesh.maxEdgeError}, max vertices/poly={mesh.nvp}");
        _tree.LeafNode($"Player's cell: {(playerPos.X - mesh.bmin.X) / mesh.cs}x{(playerPos.Y - mesh.bmin.Y) / mesh.ch}x{(playerPos.Z - mesh.bmin.Z) / mesh.cs}");

        using (var nv = _tree.Node($"Vertices ({mesh.nverts})###verts"))
        {
            if (nv.Opened)
            {
                for (int i = 0; i < mesh.nverts; ++i)
                {
                    if (_tree.LeafNode($"{i}: {mesh.verts[3 * i]}x{mesh.verts[3 * i + 1]}x{mesh.verts[3 * i + 2]}").SelectedOrHovered)
                        VisualizeMeshVertex(mesh, i);
                }
            }
        }

        using (var np = _tree.Node($"Polygons ({mesh.npolys})###polys"))
        {
            if (np.Opened)
            {
                for (int i = 0; i < mesh.npolys; ++i)
                {
                    using var nprim = _tree.Node(i.ToString());
                    if (nprim.SelectedOrHovered)
                        VisualizeMeshPolygon(mesh, i);
                    if (!nprim.Opened)
                        continue;
                    for (int j = 0; j < mesh.nvp; ++j)
                    {
                        var vertex = mesh.polys[i * 2 * mesh.nvp + j];
                        if (_tree.LeafNode($"Vertex {j}: #{vertex}").SelectedOrHovered && vertex != RcConstants.RC_MESH_NULL_IDX)
                            VisualizeMeshVertex(mesh, vertex);
                    }
                    for (int j = 0; j < mesh.nvp; ++j)
                        _tree.LeafNode($"Adjacency {j}: {mesh.polys[i * 2 * mesh.nvp + mesh.nvp + j]:X}");
                }
            }
        }
    }

    private void VisualizeMesh(RcPolyMesh mesh)
    {
        for (int i = 0; i < mesh.npolys; ++i)
            VisualizeMeshPolygon(mesh, i);
    }

    private void VisualizeMeshPolygon(RcPolyMesh mesh, int index)
    {
        //var off = index * 2 * mesh.nvp;
        //if (mesh.polys[off] == RcConstants.RC_MESH_NULL_IDX)
        //    return;
        //var from = GetMeshVertex(mesh, mesh.polys[off]);
        //for (int i = 1; i < mesh.nvp; ++i)
        //{
        //    if (mesh.polys[off + i] == RcConstants.RC_MESH_NULL_IDX)
        //        break;
        //    var to = GetMeshVertex(mesh, mesh.polys[off + i]);
        //    _geom.DrawWorldLine(from, to, 0xff00ff00);
        //    from = to;
        //}
        //_geom.DrawWorldLine(from, GetMeshVertex(mesh, mesh.polys[off]), 0xff00ff00);
        _dd.DrawMesh(new RcPolyMeshPrimitive(mesh, index), ref FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3.Identity, new(0, 1, 0, 1));
    }

    private void VisualizeMeshVertex(RcPolyMesh mesh, int index)
    {
        _dd.DrawWorldSphere(GetMeshVertex(mesh, index), 1, 0xff0000ff);
    }

    private void VisualizeAABB(RcVec3f min, float cs, float ch, int x, int z, int y0, int y1, Vector4 color)
    {
        var mtx = new FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3();
        mtx.M11 = mtx.M33 = 0.5f * cs;
        mtx.M22 = (y1 - y0) * 0.5f * ch;
        mtx.M41 = min.X + (x + 0.5f) * cs;
        mtx.M42 = min.Y + (y0 + y1) * 0.5f * ch;
        mtx.M43 = min.Z + (z + 0.5f) * cs;
        _dd.DrawBox(ref mtx, color);
    }

    private Vector3 GetMeshVertex(RcPolyMesh mesh, int index) => mesh.bmin.RecastToSystem() + new Vector3(mesh.cs, mesh.ch, mesh.cs) * new Vector3(mesh.verts[3 * index], mesh.verts[3 * index + 1], mesh.verts[3 * index + 2]);
}
