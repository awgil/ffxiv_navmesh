using DotRecast.Recast;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Navmesh.Render;
using System.Numerics;

namespace Navmesh.Debug;

public class DebugSolidHeightfield : DebugRecast
{
    private RcHeightfield _hf;
    private UITree _tree;
    private DebugDrawer _dd;
    private int _numNullSpans;
    private int _numWalkableSpans;
    private int[,] _spanCellOffsets;
    private EffectBox.Data? _visu;

    private static Vector4 _colSide = new(1.0f);
    private static Vector4 _colAreaNull = new(0.25f, 0.25f, 0.25f, 1.0f);
    private static Vector4 _colAreaWalkable = new(0.25f, 0.5f, 0.63f, 1.0f);
    private static Vector4 AreaColor(int area) => area == 0 ? _colAreaNull : _colAreaWalkable; // TODO: other colors for other areas

    public DebugSolidHeightfield(RcHeightfield hf, UITree tree, DebugDrawer dd)
    {
        _hf = hf;
        _tree = tree;
        _dd = dd;

        _spanCellOffsets = new int[hf.width, hf.height];
        int icell = 0;
        for (int z = 0; z < hf.height; ++z)
        {
            for (int x = 0; x < hf.width; ++x)
            {
                _spanCellOffsets[x, z] = _numNullSpans + _numWalkableSpans;
                var span = hf.spans[icell++];
                while (span != null)
                {
                    if (span.area == 0)
                        ++_numNullSpans;
                    else
                        ++_numWalkableSpans;
                    span = span.next;
                }
            }
        }
    }

    public override void Dispose()
    {
        _visu?.Dispose();
    }

    public void Draw()
    {
        using var nr = _tree.Node("Solid heightfield");
        if (!nr.Opened)
            return;

        DrawBaseInfo(_tree, _hf.width, _hf.height, _hf.bmin, _hf.bmax, _hf.cs, _hf.ch);
        _tree.LeafNode($"Border size: {_hf.borderSize}");

        using var nc = _tree.Node("Cells");
        if (nc.SelectedOrHovered)
            Visualize();
        if (!nc.Opened)
            return;

        for (int z = 0; z < _hf.height; ++z)
        {
            UITree.NodeRaii? nz = null;
            for (int x = 0; x < _hf.width; ++x)
            {
                var span = _hf.spans[z * _hf.width + x];
                if (span == null)
                    continue;

                nz ??= _tree.Node($"[*x{z}]");
                if (!nz.Value.Opened)
                    break;

                using var nx = _tree.Node($"[{x}x{z}]");
                if (nx.SelectedOrHovered)
                    VisualizeCell(x, z);
                if (nx.Opened)
                {
                    int ispan = 0;
                    while (span != null)
                    {
                        if (_tree.LeafNode($"{span.smin}-{span.smax} = {span.area:X}").SelectedOrHovered)
                            VisualizeSpan(_spanCellOffsets[x, z] + ispan);
                        span = span.next;
                        ++ispan;
                    }
                }
            }
            nz?.Dispose();
        }
    }

    private EffectBox.Data GetOrInitVisualizer()
    {
        if (_visu == null)
        {
            _visu = new(_dd.RenderContext, _numNullSpans + _numWalkableSpans, false);
            using var builder = _visu.Map(_dd.RenderContext);

            var timer = Timer.Create();
            // TODO: one thing i don't like about current visualization is the lack of edges and/or any depth cues
            int icell = 0;
            Matrix4x3 world = new() { M11 = _hf.cs * 0.5f, M33 = _hf.cs * 0.5f }; // x/z scale never changes
            world.M43 = _hf.bmin.Z + _hf.cs * 0.5f;
            var x0 = _hf.bmin.X + _hf.cs * 0.5f;
            var chh = _hf.ch * 0.5f;
            for (int z = 0; z < _hf.height; ++z)
            {
                world.M41 = x0;
                for (int x = 0; x < _hf.width; ++x)
                {
                    var span = _hf.spans[icell++];
                    while (span != null)
                    {
                        world.M22 = (span.smax - span.smin) * chh;
                        world.M42 = _hf.bmin.Y + (span.smin + span.smax) * chh;
                        builder.Add(ref world, AreaColor(span.area), _colSide);
                        span = span.next;
                    }
                    world.M41 += _hf.cs;
                }
                world.M43 += _hf.cs;
            }
            Service.Log.Debug($"hf visualization build time: {timer.Value().TotalMilliseconds:f3}ms");
        }
        return _visu;
    }

    private void Visualize()
    {
        _dd.EffectBox.Draw(_dd.RenderContext, GetOrInitVisualizer());
    }

    private void VisualizeCell(int x, int z)
    {
        int numSpans = 0;
        var span = _hf.spans[z * _hf.width + x];
        while (span != null)
        {
            ++numSpans;
            span = span.next;
        }
        if (numSpans > 0)
            _dd.EffectBox.DrawSubset(_dd.RenderContext, GetOrInitVisualizer(), _spanCellOffsets[x, z], numSpans);
    }

    private void VisualizeSpan(int spanIndex)
    {
        _dd.EffectBox.DrawSubset(_dd.RenderContext, GetOrInitVisualizer(), spanIndex, 1);
    }
}
