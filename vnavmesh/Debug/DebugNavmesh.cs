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
    private UITree _tree = new();
    private DebugDrawer _dd;
    private DebugExtractedCollision _drawExtracted;
    private NavmeshBuilder _navmesh;
    private Vector3 _target;
    private List<Vector3> _waypoints = new();

    public DebugNavmesh(DebugDrawer dd, NavmeshBuilder navmesh)
    {
        _dd = dd;
        _drawExtracted = new(_tree, dd);
        _navmesh = navmesh;
    }

    public void Dispose()
    {
        _drawExtracted.Dispose();
    }

    public void Draw()
    {
        DrawConfig();

        var state = _navmesh.CurrentState;
        using (var d = ImRaii.Disabled(state == NavmeshBuilder.State.InProgress))
        {
            if (ImGui.Button("Rebuild navmesh"))
            {
                _navmesh.Rebuild();
                _waypoints.Clear();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"State: {state}");
        }

        if (state != NavmeshBuilder.State.Ready)
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
        _drawExtracted.Draw(_navmesh.CollisionGeometry);
        DrawSolidHeightfield(intermediates.GetSolidHeightfield());
        DrawCompactHeightfield(intermediates.GetCompactHeightfield());
        DrawContourSet(intermediates.GetContourSet());
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

    private void DrawSolidHeightfield(RcHeightfield hf)
    {
        using var n = _tree.Node("Solid heightfield");
        if (n.SelectedOrHovered)
            VisualizeSolidHeightfield(hf);
        if (!n.Opened)
            return;

        var playerPos = Service.ClientState.LocalPlayer?.Position ?? default;
        _tree.LeafNode($"Num cells: {hf.width}x{hf.height}");
        _tree.LeafNode($"Bounds: [{hf.bmin}] - [{hf.bmax}]");
        _tree.LeafNode($"Cell size: {hf.cs}x{hf.ch}");
        _tree.LeafNode($"Border size: {hf.borderSize}");
        _tree.LeafNode($"Player's cell: {(playerPos.X - hf.bmin.X) / hf.cs}x{(playerPos.Y - hf.bmin.Y) / hf.ch}x{(playerPos.Z - hf.bmin.Z) / hf.cs}");
        using var nc = _tree.Node("Cells");
        if (!nc.Opened)
            return;

        for (int z = 0; z < hf.height; ++z)
        {
            UITree.NodeRaii? nz = null;
            for (int x = 0; x < hf.width; ++x)
            {
                var span = hf.spans[z * hf.width + x];
                if (span == null)
                    continue;

                nz ??= _tree.Node($"[*x{z}]");
                if (!nz.Value.Opened)
                    break;

                using var nx = _tree.Node($"[{x}x{z}]");
                if (nx.SelectedOrHovered)
                    VisualizeSolidHeightfieldCell(hf, x, z);
                if (nx.Opened)
                {
                    while (span != null)
                    {
                        if (_tree.LeafNode($"{span.smin}-{span.smax} = {span.area:X}").SelectedOrHovered)
                            VisualizeSolidHeightfieldSpan(hf, x, z, span);
                        span = span.next;
                    }
                }
            }
            nz?.Dispose();
        }
    }

    private void DrawCompactHeightfield(RcCompactHeightfield hf)
    {
        using var n = _tree.Node("Compact heightfield");
        if (n.SelectedOrHovered)
            VisualizeCompactHeightfield(hf);
        if (!n.Opened)
            return;

        var playerPos = Service.ClientState.LocalPlayer?.Position ?? default;
        _tree.LeafNode($"Num cells: {hf.width}x{hf.height}");
        _tree.LeafNode($"Bounds: [{hf.bmin}] - [{hf.bmax}]");
        _tree.LeafNode($"Cell size: {hf.cs}x{hf.ch}");
        _tree.LeafNode($"Config: walkable height={hf.walkableHeight}, walkable climb={hf.walkableClimb}, border={hf.borderSize}");
        _tree.LeafNode($"Count: spans={hf.spanCount}, max dist={hf.maxDistance}, max region id={hf.maxRegions}");
        _tree.LeafNode($"Player's cell: {(playerPos.X - hf.bmin.X) / hf.cs}x{(playerPos.Y - hf.bmin.Y) / hf.ch}x{(playerPos.Z - hf.bmin.Z) / hf.cs}");

        using (var nc = _tree.Node("Cells"))
        {
            if (nc.Opened)
            {
                for (int z = 0; z < hf.height; ++z)
                {
                    UITree.NodeRaii? nz = null;
                    for (int x = 0; x < hf.width; ++x)
                    {
                        ref var cell = ref hf.cells[z * hf.width + x];
                        if (cell.count == 0)
                            continue;

                        nz ??= _tree.Node($"[*x{z}]");
                        if (!nz.Value.Opened)
                            break;

                        using var nx = _tree.Node($"[{x}x{z}]: {cell.count} spans starting from {cell.index}");
                        if (nx.SelectedOrHovered)
                            VisualizeCompactHeightfieldCell(hf, x, z, true);
                        if (nx.Opened)
                        {
                            for (int i = 0; i < cell.count; ++i)
                            {
                                ref var span = ref hf.spans[cell.index + i];
                                if (_tree.LeafNode($"y={span.y}+{span.h}, conn={RcCommons.GetCon(ref span, 0)} {RcCommons.GetCon(ref span, 1)} {RcCommons.GetCon(ref span, 2)} {RcCommons.GetCon(ref span, 3)}, reg={span.reg}, dist={hf.dist[i]}, area={hf.areas[i]}").SelectedOrHovered)
                                    VisualizeCompactHeightfieldSpan(hf, x, z, cell.index + i, true);
                            }
                        }
                    }
                    nz?.Dispose();
                }
            }
        }

        using (var nr = _tree.Node($"Regions ({hf.maxRegions})###regions"))
        {
            if (nr.Opened)
            {
                for (int i = 0; i < hf.maxRegions; ++i)
                {
                    using var nreg = _tree.Node($"Region {i}");
                    if (nreg.SelectedOrHovered)
                        VisualizeCompactHeightfieldRegion(hf, i);
                    if (!nreg.Opened)
                        continue;
                    for (int z = 0; z < hf.height; ++z)
                    {
                        for (int x = 0; x < hf.width; ++x)
                        {
                            ref var cell = ref hf.cells[z * hf.width + x];
                            for (int idx = 0; idx < cell.count; ++idx)
                            {
                                ref var span = ref hf.spans[cell.index + idx];
                                if (span.reg != i)
                                    continue;

                                if (_tree.LeafNode($"[{x}x{z}]: y={span.y}+{span.h}").SelectedOrHovered)
                                    VisualizeCompactHeightfieldSpan(hf, x, z, cell.index + idx, true);
                            }
                        }
                    }
                }
            }
        }
    }

    private void DrawContourSet(RcContourSet cs)
    {
        using var n = _tree.Node("Contour set");
        if (!n.Opened)
            return;

        var playerPos = Service.ClientState.LocalPlayer?.Position ?? default;
        _tree.LeafNode($"Num cells: {cs.width}x{cs.height}");
        _tree.LeafNode($"Bounds: [{cs.bmin}] - [{cs.bmax}]");
        _tree.LeafNode($"Cell size: {cs.cs}x{cs.ch}");
        _tree.LeafNode($"Misc: border size={cs.borderSize}, max error={cs.maxError}");
        _tree.LeafNode($"Player's cell: {(playerPos.X - cs.bmin.X) / cs.cs}x{(playerPos.Y - cs.bmin.Y) / cs.ch}x{(playerPos.Z - cs.bmin.Z) / cs.cs}");

        using var nc = _tree.Node($"Contours ({cs.conts.Count})###contours");
        if (!nc.Opened)
            return;

        int i = 0;
        foreach (var c in cs.conts)
        {
            using var ncont = _tree.Node($"Contour {i++}: area={c.area}, region={c.reg}");
            if (ncont.SelectedOrHovered)
            {
                VisualizeContour(cs, c.verts, c.nverts, 0xff00ff00);
                VisualizeContour(cs, c.rverts, c.nrverts, 0xff00ffff);
            }
            if (!ncont.Opened)
                continue;

            using (var ns = _tree.Node($"Simplified vertices ({c.nverts})###simp"))
            {
                if (ns.SelectedOrHovered)
                    VisualizeContour(cs, c.verts, c.nverts, 0xff00ff00);
                if (ns.Opened)
                {
                    for (int iv = 0; iv < c.nverts; ++iv)
                    {
                        var reg = c.verts[iv * 4 + 3];
                        if (_tree.LeafNode($"{iv}: {c.verts[iv * 4]}x{c.verts[iv * 4 + 1]}x{c.verts[iv * 4 + 2]}, reg={reg & RcConstants.RC_CONTOUR_REG_MASK}, border={(reg & RcConstants.RC_BORDER_VERTEX) != 0}, areaborder={(reg & RcConstants.RC_AREA_BORDER) != 0}").SelectedOrHovered)
                            VisualizeContourVertex(cs, c.verts, iv);
                    }
                }
            }

            using (var nr = _tree.Node($"Raw vertices ({c.nrverts})###raw"))
            {
                if (nr.SelectedOrHovered)
                    VisualizeContour(cs, c.rverts, c.nrverts, 0xff00ffff);
                if (nr.Opened)
                {
                    for (int iv = 0; iv < c.nrverts; ++iv)
                    {
                        var reg = c.rverts[iv * 4 + 3];
                        if (_tree.LeafNode($"{iv}: {c.rverts[iv * 4]}x{c.rverts[iv * 4 + 1]}x{c.rverts[iv * 4 + 2]}, reg={reg & RcConstants.RC_CONTOUR_REG_MASK}, border={(reg & RcConstants.RC_BORDER_VERTEX) != 0}, areaborder={(reg & RcConstants.RC_AREA_BORDER) != 0}").SelectedOrHovered)
                            VisualizeContourVertex(cs, c.rverts, iv);
                    }
                }
            }
        }
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

    private void VisualizeSolidHeightfield(RcHeightfield hf)
    {
        for (int z = 0; z < hf.height; ++z)
            for (int x = 0; x < hf.width; ++x)
                VisualizeSolidHeightfieldCell(hf, x, z);
    }

    private void VisualizeSolidHeightfieldCell(RcHeightfield hf, int x, int z)
    {
        var span = hf.spans[z * hf.width + x];
        while (span != null)
        {
            VisualizeSolidHeightfieldSpan(hf, x, z, span);
            span = span.next;
        }
    }

    private void VisualizeSolidHeightfieldSpan(RcHeightfield hf, int x, int z, RcSpan span) => VisualizeAABB(hf.bmin, hf.cs, hf.ch, x, z, span.smin, span.smax, new(1, span.area == 0 ? 0 : 1, 0, 1));

    private void VisualizeCompactHeightfield(RcCompactHeightfield hf)
    {
        for (int z = 0; z < hf.height; ++z)
            for (int x = 0; x < hf.width; ++x)
                VisualizeCompactHeightfieldCell(hf, x, z, false);
    }

    private void VisualizeCompactHeightfieldRegion(RcCompactHeightfield hf, int reg)
    {
        for (int z = 0; z < hf.height; ++z)
        {
            for (int x = 0; x < hf.width; ++x)
            {
                ref var cell = ref hf.cells[z * hf.width + x];
                for (int idx = 0; idx < cell.count; ++idx)
                    if (hf.spans[cell.index + idx].reg == reg)
                        VisualizeCompactHeightfieldSpan(hf, x, z, cell.index + idx, true);
            }
        }
    }

    private void VisualizeCompactHeightfieldCell(RcCompactHeightfield hf, int x, int z, bool showConnections)
    {
        ref var cell = ref hf.cells[z * hf.width + x];
        for (int i = 0; i < cell.count; ++i)
            VisualizeCompactHeightfieldSpan(hf, x, z, cell.index + i, showConnections);
    }

    private void VisualizeCompactHeightfieldSpan(RcCompactHeightfield hf, int x, int z, int spanIndex, bool showConnections)
    {
        ref var span = ref hf.spans[spanIndex];
        var y1 = span.y + span.h;
        if (y1 == RcConstants.SPAN_MAX_HEIGHT)
            y1 = span.y + 1;
        VisualizeAABB(hf.bmin, hf.cs, hf.ch, x, z, span.y, y1, new(1, hf.areas[spanIndex] == 0 ? 0 : 1, 0, 1));
        if (!showConnections)
            return;
        for (int dir = 0; dir < 4; ++dir)
        {
            var conn = RcCommons.GetCon(ref span, dir);
            if (conn == RcConstants.RC_NOT_CONNECTED)
                continue;
            int nx = x + RcCommons.GetDirOffsetX(dir);
            int nz = z + RcCommons.GetDirOffsetY(dir);
            ref var nc = ref hf.cells[nz * hf.width + nx];
            ref var ns = ref hf.spans[nc.index + conn];
            var ny1 = ns.y + ns.h;
            if (ny1 == RcConstants.SPAN_MAX_HEIGHT)
                ny1 = ns.y + 1;
            var from = hf.bmin.RecastToSystem() + new Vector3(hf.cs * (x + 0.5f), hf.ch * (span.y + y1) * 0.5f, hf.cs * (z + 0.5f));
            var to = hf.bmin.RecastToSystem() + new Vector3(hf.cs * (nx + 0.5f), hf.ch * (ns.y + ny1) * 0.5f, hf.cs * (nz + 0.5f));
            _dd.DrawWorldLine(from, to, 0xff00ffff);
        }
    }

    private void VisualizeContour(RcContourSet cs, int[] contourVertices, int numVerts, uint color)
    {
        if (numVerts <= 0)
            return;
        var from = GetContourVertex(cs, contourVertices, numVerts - 1);
        for (int i = 0; i < numVerts; ++i)
        {
            var to = GetContourVertex(cs, contourVertices, i);
            _dd.DrawWorldLine(from, to, color);
            from = to;
        }
    }

    private void VisualizeContourVertex(RcContourSet cs, int[] vertices, int index)
    {
        _dd.DrawWorldSphere(GetContourVertex(cs, vertices, index), 1, 0xff0000ff);
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

    private Vector3 GetContourVertex(RcContourSet cs, int[] verts, int index) => cs.bmin.RecastToSystem() + new Vector3(cs.cs, cs.ch, cs.cs) * new Vector3(verts[4 * index], verts[4 * index + 1], verts[4 * index + 2]);
    private Vector3 GetMeshVertex(RcPolyMesh mesh, int index) => mesh.bmin.RecastToSystem() + new Vector3(mesh.cs, mesh.ch, mesh.cs) * new Vector3(mesh.verts[3 * index], mesh.verts[3 * index + 1], mesh.verts[3 * index + 2]);
}
