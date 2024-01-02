using DotRecast.Recast;
using Lumina.Models.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh.Debug;

public class DebugContourSet : DebugRecast
{
    private RcContourSet _cset;
    private UITree _tree;
    private DebugDrawer _dd;

    public DebugContourSet(RcContourSet cset, UITree tree, DebugDrawer dd)
    {
        _cset = cset;
        _tree = tree;
        _dd = dd;
    }

    public override void Dispose()
    {
    }

    public void Draw()
    {
        using var nr = _tree.Node("Contour set");
        if (!nr.Opened)
            return;

        DrawBaseInfo(_tree, _cset.width, _cset.height, _cset.bmin, _cset.bmax, _cset.cs, _cset.ch);
        _tree.LeafNode($"Misc: border size={_cset.borderSize}, max error={_cset.maxError}");

        using (var nc = _tree.Node($"Contours ({_cset.conts.Count})###contours"))
        {
            if (nc.SelectedOrHovered)
                VisualizeContours();
            if (nc.Opened)
            {
                int i = 0;
                foreach (var c in _cset.conts)
                {
                    var hOffset = (i & 1) + 1; // i assume this is just for better visualization?..
                    using var ncont = _tree.Node($"Contour {i++}: area={c.area}, region={c.reg}");

                    bool contourDrawn = false;
                    if (ncont.Opened)
                    {
                        using (var ns = _tree.Node($"Simplified vertices ({c.nverts})###simp"))
                        {
                            if (ns.SelectedOrHovered)
                            {
                                VisualizeContour(c.verts, c.nverts, hOffset, c.reg, 1, true);
                                contourDrawn = true;
                            }
                            if (ns.Opened)
                            {
                                for (int iv = 0; iv < c.nverts; ++iv)
                                {
                                    var reg = c.verts[iv * 4 + 3];
                                    if (_tree.LeafNode($"{iv}: {c.verts[iv * 4]}x{c.verts[iv * 4 + 1]}x{c.verts[iv * 4 + 2]}, reg={reg & RcConstants.RC_CONTOUR_REG_MASK}, border={(reg & RcConstants.RC_BORDER_VERTEX) != 0}, areaborder={(reg & RcConstants.RC_AREA_BORDER) != 0}").SelectedOrHovered)
                                        VisualizeVertex(c.verts, iv, hOffset, RegionColor(reg, true, 1), 10);
                                }
                            }
                        }

                        using (var nraw = _tree.Node($"Raw vertices ({c.nrverts})###raw"))
                        {
                            if (nraw.SelectedOrHovered)
                            {
                                VisualizeContour(c.rverts, c.nrverts, hOffset, c.reg, 1, true);
                                contourDrawn = true;
                            }
                            if (nr.Opened)
                            {
                                for (int iv = 0; iv < c.nrverts; ++iv)
                                {
                                    var reg = c.rverts[iv * 4 + 3];
                                    if (_tree.LeafNode($"{iv}: {c.rverts[iv * 4]}x{c.rverts[iv * 4 + 1]}x{c.rverts[iv * 4 + 2]}, reg={reg & RcConstants.RC_CONTOUR_REG_MASK}, border={(reg & RcConstants.RC_BORDER_VERTEX) != 0}, areaborder={(reg & RcConstants.RC_AREA_BORDER) != 0}").SelectedOrHovered)
                                        VisualizeVertex(c.rverts, iv, hOffset, RegionColor(reg, true, 1), 10);
                                }
                            }
                        }
                    }

                    if (!contourDrawn && ncont.SelectedOrHovered)
                    {
                        VisualizeContour(c.rverts, c.nrverts, hOffset, c.reg, 0.5f, true);
                        VisualizeContour(c.verts, c.nverts, hOffset, c.reg, 1.0f, true);
                    }
                }
            }
        }

        if (_tree.LeafNode("Region connections").SelectedOrHovered)
            VisualizeConnections();
    }

    private void VisualizeContours()
    {
        int i = 0;
        foreach (var c in _cset.conts)
        {
            var hOffset = (i & 1) + 1; // i assume this is just for better visualization?..
            //VisualizeContour(c.rverts, c.nrverts, hOffset, c.reg, 0.5f, false);
            VisualizeContour(c.verts, c.nverts, hOffset, c.reg, 1.0f, false);
        }
    }

    private void VisualizeContour(int[] verts, int numVerts, int hOffset, int reg, float alpha, bool withVertices)
    {
        if (numVerts <= 0)
            return;
        _dd.DrawWorldPolygon(Enumerable.Range(0, numVerts).Select(i => GetContourVertex(verts, i, hOffset)), RegionColor(reg, false, alpha));
        if (!withVertices)
            return;
        var vcolor = RegionColor(reg, true, 1);
        for (int i = 0; i < numVerts; i++)
            VisualizeVertex(verts, i, hOffset, vcolor, 5);
    }

    private void VisualizeVertex(int[] verts, int index, int hOffset, uint color, float radius)
    {
        bool isBorder = (verts[4 * index + 3] & RcConstants.RC_BORDER_VERTEX) != 0;
        _dd.DrawWorldPoint(GetContourVertex(verts, index, hOffset + (isBorder ? 2 : 0)), radius, isBorder ? 0xffffffff : color, 2);
    }

    private void VisualizeConnections()
    {
        foreach (var c in _cset.conts)
        {
            var from = GetContourCenter(c.verts, c.nverts);
            _dd.DrawWorldPoint(from, 8, RegionColor(c.reg, true, 1));

            for (int iv = 0; iv < c.nverts; ++iv)
            {
                var reg = c.verts[4 * iv + 3] & RcConstants.RC_CONTOUR_REG_MASK;
                if (reg == 0 || reg < c.reg)
                    continue;
                var other = _cset.conts.FirstOrDefault(cand => cand.reg == reg);
                if (other == null)
                    continue;
                var to = GetContourCenter(other.verts, other.nverts);
                _dd.DrawWorldArc(from, to, 0.25f, 6, 6, 0xc0000000, 2);
            }
        }
    }

    private Vector3 GetContourVertex(int[] verts, int index, int hOffset) => _cset.bmin.RecastToSystem() + new Vector3(_cset.cs, _cset.ch, _cset.cs) * new Vector3(verts[4 * index], verts[4 * index + 1] + hOffset, verts[4 * index + 2]);

    private Vector3 GetContourCenter(int[] verts, int numVerts)
    {
        Vector3 res = new();
        if (numVerts > 0)
        {
            for (int i = 0; i < numVerts; ++i)
                res += new Vector3(verts[4 * i], verts[4 * i + 1] + 4, verts[4 * i + 2]);
            res = _cset.bmin.RecastToSystem() + new Vector3(_cset.cs, _cset.ch, _cset.cs) * res / numVerts;
        }
        return res;
    }

    private uint RegionColor(int reg, bool darken, float alpha)
    {
        var fcolor = IntColor(reg, 0);
        if (darken)
            fcolor *= 0.5f;
        fcolor.W = alpha;
        fcolor *= 255;
        return (((uint)fcolor.W) & 0xFF) << 24 | (((uint)fcolor.Z) & 0xFF) << 16 | (((uint)fcolor.Y) & 0xFF) << 8 | (((uint)fcolor.X) & 0xFF);
    }
}
