﻿using DotRecast.Recast;
using Navmesh.Render;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh.Debug;

public class DebugCompactHeightfield : DebugRecast
{
    private RcCompactHeightfield _chf;
    private UITree _tree;
    private DebugDrawer _dd;
    private EffectMesh.Data? _visuSolid;
    private EffectMesh.Data? _visuDistance;
    private EffectMesh.Data? _visuRegion;
    private int[] _regionsNumSpans;
    private int[] _regionsStartOffset;

    private static int _heightOffset = 0;

    private static Vector4 _colAreaNull = new(0, 0, 0, 0.5f);
    private static Vector4 _colAreaWalkable = new(0, 0.75f, 1.0f, 0.5f);
    private static Vector4 AreaColor(int area) => area == 0 ? _colAreaNull : _colAreaWalkable; // TODO: other colors for other areas
    private static Vector4 RegionColor(int region) => region != 0 ? IntColor(region, 0.75f) : _colAreaNull;

    public DebugCompactHeightfield(RcCompactHeightfield chf, UITree tree, DebugDrawer dd)
    {
        _chf = chf;
        _tree = tree;
        _dd = dd;

        _regionsNumSpans = new int[_chf.maxRegions + 1];
        foreach (ref var span in chf.spans.AsSpan())
            ++_regionsNumSpans[span.reg & ~RcRecast.RC_BORDER_REG];
        _regionsStartOffset = new int[_regionsNumSpans.Length];
        for (int i = 1; i < _regionsNumSpans.Length; i++)
            _regionsStartOffset[i] = _regionsStartOffset[i - 1] + _regionsNumSpans[i - 1];
    }

    public override void Dispose()
    {
        _visuSolid?.Dispose();
        _visuDistance?.Dispose();
        _visuRegion?.Dispose();
    }

    public void Draw()
    {
        using var nr = _tree.Node("Compact heightfield");
        if (!nr.Opened)
            return;

        DrawBaseInfo(_tree, _chf.width, _chf.height, _chf.bmin, _chf.bmax, _chf.cs, _chf.ch);
        _tree.LeafNode($"Config: walkable height={_chf.walkableHeight}, walkable climb={_chf.walkableClimb}, border={_chf.borderSize}");

        using (var nc = _tree.Node($"Cells ({_chf.spanCount} spans total)###cells"))
        {
            if (nc.SelectedOrHovered)
                VisualizeSolid();
            if (nc.Opened)
            {
                for (int z = 0; z < _chf.height; ++z)
                {
                    UITree.NodeRaii? nz = null;
                    for (int x = 0; x < _chf.width; ++x)
                    {
                        ref var cell = ref _chf.cells[z * _chf.width + x];
                        if (cell.count == 0)
                            continue;

                        nz ??= _tree.Node($"[*x{z}]");
                        if (!nz.Value.Opened)
                            break;

                        using var nx = _tree.Node($"[{x}x{z}]: {cell.count} spans starting from {cell.index}");
                        if (nx.SelectedOrHovered)
                            VisualizeSolidCell(x, z, true);
                        if (nx.Opened)
                        {
                            for (int i = 0; i < cell.count; ++i)
                            {
                                ref var span = ref _chf.spans[cell.index + i];
                                if (_tree.LeafNode($"y={span.y}+{span.h}, conn={RcRecast.GetCon(ref span, 0)} {RcRecast.GetCon(ref span, 1)} {RcRecast.GetCon(ref span, 2)} {RcRecast.GetCon(ref span, 3)}, reg={span.reg & ~RcRecast.RC_BORDER_REG} ({((span.reg & RcRecast.RC_BORDER_REG) == 0 ? "normal" : "border")}), dist={_chf.dist[i]}, area={_chf.areas[i]}").SelectedOrHovered)
                                    VisualizeSolidSpan(x, z, cell.index + i, true);
                            }
                        }
                    }
                    nz?.Dispose();
                }
            }
        }

        if (_tree.LeafNode($"Distance field (max distance = {_chf.maxDistance})###dist").SelectedOrHovered && _chf.dist != null)
            VisualizeDistance();

        using (var nregs = _tree.Node($"Regions ({_chf.maxRegions + 1})###regions"))
        {
            if (nregs.SelectedOrHovered)
                VisualizeRegions();
            if (nregs.Opened)
            {
                for (int i = 0; i < _chf.maxRegions; ++i)
                {
                    using var nreg = _tree.Node($"Region {i}: {_regionsNumSpans[i]} spans, offset {_regionsStartOffset[i]}###reg_{i}");
                    if (nreg.SelectedOrHovered)
                        VisualizeRegion(i);
                    if (!nreg.Opened)
                        continue;

                    int ispan = 0;
                    for (int z = 0; z < _chf.height; ++z)
                    {
                        for (int x = 0; x < _chf.width; ++x)
                        {
                            ref var cell = ref _chf.cells[z * _chf.width + x];
                            for (int idx = 0; idx < cell.count; ++idx)
                            {
                                ref var span = ref _chf.spans[cell.index + idx];
                                if ((span.reg & ~RcRecast.RC_BORDER_REG) != i)
                                    continue;

                                if (_tree.LeafNode($"{ispan}: [{x}x{z}]: y={span.y}+{span.h}").SelectedOrHovered)
                                {
                                    VisualizeRegionSpan(ispan);
                                    VisualizeConnections(x, z, cell.index + idx);
                                }
                                ++ispan;
                            }
                        }
                    }
                }
            }
        }
    }

    private IEnumerable<(Vector3 center, int index)> EnumerateSpanPositions()
    {
        int icell = 0;
        int ispan = 0;
        var x0 = _chf.bmin.X + _chf.cs * 0.5f;
        var cz = _chf.bmin.Z + _chf.cs * 0.5f;
        for (int z = 0; z < _chf.height; ++z)
        {
            var cx = x0;
            for (int x = 0; x < _chf.width; ++x)
            {
                var cell = _chf.cells[icell++];
                if (cell.count != 0)
                {
                    if (cell.index != ispan)
                        throw new Exception($"Unexpected gap in compact heightfield spans: cell {x}x{z} has starting index {cell.index}, where {ispan} was expected");
                    for (int i = 0; i < cell.count; ++i)
                    {
                        yield return (new(cx, _chf.bmin.Y + (_chf.spans[ispan].y + _heightOffset) * _chf.ch, cz), ispan);
                        ++ispan;
                    }
                }
                cx += _chf.cs;
            }
            cz += _chf.cs;
        }
    }

    private EffectMesh.Data GetOrInitVisualizerSolid()
    {
        if (_visuSolid == null)
        {
            _visuSolid = new EffectMesh.Data(_dd.RenderContext, 4, 2, _chf.spanCount, false);
            using var builder = _visuSolid.Map(_dd.RenderContext);
            var quad = new AnalyticMeshQuad(builder);

            var timer = Timer.Create();
            var wx = new Vector3(_chf.cs * 0.5f, 0, 0);
            var wz = new Vector3(0, 0, -_chf.cs * 0.5f);
            foreach (var (center, index) in EnumerateSpanPositions())
                quad.Add(center, wx, wz, AreaColor(_chf.areas[index]));
            Service.Log.Debug($"chf solid visualization build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return _visuSolid;
    }

    private EffectMesh.Data GetOrInitVisualizerDistance()
    {
        if (_visuDistance == null)
        {
            _visuDistance = new EffectMesh.Data(_dd.RenderContext, 4, 2, _chf.spanCount, false);
            using var builder = _visuDistance.Map(_dd.RenderContext);
            var quad = new AnalyticMeshQuad(builder);

            var timer = Timer.Create();
            var wx = new Vector3(_chf.cs * 0.5f, 0, 0);
            var wz = new Vector3(0, 0, -_chf.cs * 0.5f);
            float dscale = 1.0f / Math.Max(1, _chf.maxDistance);
            foreach (var (center, index) in EnumerateSpanPositions())
            {
                var c = _chf.dist[index] * dscale;
                quad.Add(center, wx, wz, new(c, c, c, 0.7f));
            }
            Service.Log.Debug($"chf distance visualization build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return _visuDistance;
    }

    private EffectMesh.Data GetOrInitVisualizerRegions()
    {
        if (_visuRegion == null)
        {
            _visuRegion = new EffectMesh.Data(_dd.RenderContext, 4, 2, _chf.spanCount, false);
            using var builder = _visuRegion.Map(_dd.RenderContext);
            var quad = new AnalyticMeshQuad(builder);

            var timer = Timer.Create();
            var wx = new Vector3(_chf.cs * 0.5f, 0, 0);
            var wz = new Vector3(0, 0, -_chf.cs * 0.5f);
            var storage = new EffectMesh.Instance[_chf.spanCount];
            var offsets = new int[_regionsNumSpans.Length];
            foreach (var (center, index) in EnumerateSpanPositions())
            {
                var reg = _chf.spans[index].reg & ~RcRecast.RC_BORDER_REG;
                var off = offsets[reg]++;
                storage[_regionsStartOffset[reg] + off] = quad.BuildInstance(center, wx, wz, RegionColor(reg));
            }
            int i = 0;
            foreach (ref var inst in storage.AsSpan())
            {
                builder.AddInstance(inst);
                builder.AddMesh(quad.FirstVertex, quad.FirstPrimitive, quad.NumPrimitives, i++, 1);
            }

            Service.Log.Debug($"chf region visualization build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return _visuRegion;
    }

    private void VisualizeSolid()
    {
        if (_dd.EffectMesh == null)
            return;
        _dd.EffectMesh.Bind(_dd.RenderContext, true, false);
        GetOrInitVisualizerSolid().DrawAll(_dd.RenderContext);
    }

    private void VisualizeSolidCell(int x, int z, bool showConnections)
    {
        if (_dd.EffectMesh == null)
            return;
        ref var cell = ref _chf.cells[z * _chf.width + x];
        _dd.EffectMesh.Bind(_dd.RenderContext, true, false);
        GetOrInitVisualizerSolid().DrawSubset(_dd.RenderContext, cell.index, cell.count);
        if (showConnections)
            for (int i = 0; i < cell.count; ++i)
                VisualizeConnections(x, z, cell.index + i);
    }

    private void VisualizeSolidSpan(int x, int z, int spanIndex, bool showConnections)
    {
        if (_dd.EffectMesh == null)
            return;
        _dd.EffectMesh.Bind(_dd.RenderContext, true, false);
        GetOrInitVisualizerSolid().DrawSubset(_dd.RenderContext, spanIndex, 1);
        if (showConnections)
            VisualizeConnections(x, z, spanIndex);
    }

    private void VisualizeDistance()
    {
        if (_dd.EffectMesh == null)
            return;
        _dd.EffectMesh.Bind(_dd.RenderContext, true, false);
        GetOrInitVisualizerDistance().DrawAll(_dd.RenderContext);
    }

    private void VisualizeRegions()
    {
        if (_dd.EffectMesh == null)
            return;
        _dd.EffectMesh.Bind(_dd.RenderContext, true, false);
        GetOrInitVisualizerRegions().DrawAll(_dd.RenderContext);
    }

    private void VisualizeRegion(int reg)
    {
        if (_dd.EffectMesh == null)
            return;
        _dd.EffectMesh.Bind(_dd.RenderContext, true, false);
        GetOrInitVisualizerRegions().DrawSubset(_dd.RenderContext, _regionsStartOffset[reg], _regionsNumSpans[reg]);
    }

    private void VisualizeRegionSpan(int index)
    {
        if (_dd.EffectMesh == null)
            return;
        _dd.EffectMesh.Bind(_dd.RenderContext, true, false);
        GetOrInitVisualizerRegions().DrawSubset(_dd.RenderContext, index, 1);
    }

    private void VisualizeConnections(int x, int z, int spanIndex)
    {
        ref var span = ref _chf.spans[spanIndex];
        for (int dir = 0; dir < 4; ++dir)
        {
            var conn = RcRecast.GetCon(ref span, dir);
            if (conn == RcRecast.RC_NOT_CONNECTED)
                continue;
            int nx = x + RcRecast.GetDirOffsetX(dir);
            int nz = z + RcRecast.GetDirOffsetY(dir);
            ref var nc = ref _chf.cells[nz * _chf.width + nx];
            ref var ns = ref _chf.spans[nc.index + conn];
            var from = _chf.bmin.RecastToSystem() + new Vector3(_chf.cs * (x + 0.5f), _chf.ch * (span.y + _heightOffset), _chf.cs * (z + 0.5f));
            var to = _chf.bmin.RecastToSystem() + new Vector3(_chf.cs * (nx + 0.5f), _chf.ch * (ns.y + _heightOffset), _chf.cs * (nz + 0.5f));
            _dd.DrawWorldLine(from, to, 0xff00ffff);
        }
    }
}
