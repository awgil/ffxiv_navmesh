using DotRecast.Recast;
using System.Numerics;

namespace Navmesh.Debug;

public class DebugSolidHeightfield
{
    private UITree _tree;
    private DebugDrawer _dd;

    public DebugSolidHeightfield(UITree tree, DebugDrawer dd)
    {
        _tree = tree;
        _dd = dd;
    }

    public void Draw(RcHeightfield hf)
    {
        using var nr = _tree.Node("Solid heightfield");
        if (nr.SelectedOrHovered)
            Visualize(hf);
        if (!nr.Opened)
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
                    VisualizeCell(hf, x, z);
                if (nx.Opened)
                {
                    while (span != null)
                    {
                        if (_tree.LeafNode($"{span.smin}-{span.smax} = {span.area:X}").SelectedOrHovered)
                            VisualizeSpan(hf, x, z, span);
                        span = span.next;
                    }
                }
            }
            nz?.Dispose();
        }
    }

    private void Visualize(RcHeightfield hf)
    {
        //var timer = DateTime.Now;
        // this is unrolled for efficiency, still quite slow though :(
        // TODO: one thing i don't like about current visualization is the lack of edges and/or any depth cues
        int ispan = 0;
        FFXIVClientStructs.FFXIV.Common.Math.Matrix4x3 world = new() { M11 = hf.cs * 0.5f, M33 = hf.cs * 0.5f }; // x/z scale never changes
        world.M43 = hf.bmin.Z + hf.cs * 0.5f;
        var x0 = hf.bmin.X + hf.cs * 0.5f;
        var chh = hf.ch * 0.5f;
        for (int z = 0; z < hf.height; ++z)
        {
            world.M41 = x0;
            for (int x = 0; x < hf.width; ++x)
            {
                var span = hf.spans[ispan++];
                while (span != null)
                {
                    world.M22 = (span.smax - span.smin) * chh;
                    world.M42 = hf.bmin.Y + (span.smin + span.smax) * chh;
                    _dd.DrawBox(ref world, AreaColor(span.area), _colSide);
                    span = span.next;
                }
                world.M41 += hf.cs;
            }
            world.M43 += hf.cs;
        }
        //Service.Log.Debug($"hf: {(DateTime.Now - timer).TotalMilliseconds:f3}ms");
    }

    private void VisualizeCell(RcHeightfield hf, int x, int z)
    {
        var span = hf.spans[z * hf.width + x];
        while (span != null)
        {
            VisualizeSpan(hf, x, z, span);
            span = span.next;
        }
    }

    private void VisualizeSpan(RcHeightfield hf, int x, int z, RcSpan span)
    {
        var s = new Vector3(hf.cs, hf.ch, hf.cs);
        var min = hf.bmin.RecastToSystem() + s * new Vector3(x, span.smin, z);
        s.Y *= span.smax - span.smin;
        var max = min + s;
        _dd.DrawAABB(min, max, AreaColor(span.area), _colSide);
    }

    private static Vector4 _colSide = new(1.0f);
    private static Vector4 _colAreaNull = new(0.25f, 0.25f, 0.25f, 1.0f);
    private static Vector4 _colAreaWalkable = new(0.25f, 0.5f, 0.63f, 1.0f);
    private static Vector4 AreaColor(int area) => area == 0 ? _colAreaNull : _colAreaWalkable; // TODO: other colors for other areas
}
