using Dalamud.Interface.Utility;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using ImGuiNET;
using Navmesh.Render;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Navmesh.Debug;

public unsafe class DebugDrawer : IDisposable
{
    public RenderContext RenderContext { get; init; } = new();
    public RenderTarget? RenderTarget { get; private set; }
    public EffectMesh EffectMesh { get; init; }
    public EffectBox EffectBox { get; init; }
    public EffectQuad EffectQuad { get; init; }

    public SharpDX.Matrix ViewProj { get; private set; }
    public SharpDX.Matrix Proj { get; private set; }
    public SharpDX.Matrix View { get; private set; }
    public SharpDX.Matrix CameraWorld { get; private set; }
    public float CameraAzimuth { get; private set; } // facing north = 0, facing west = pi/4, facing south = +-pi/2, facing east = -pi/4
    public float CameraAltitude { get; private set; } // facing horizontally = 0, facing down = pi/4, facing up = -pi/4
    public SharpDX.Vector2 ViewportSize { get; private set; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetEngineCoreSingletonDelegate();

    private nint _engineCoreSingleton;

    private List<(Vector2 from, Vector2 to, uint col, int thickness)> _viewportLines = new();
    private List<(Vector2 center, float radius, uint color)> _viewportCircles = new();

    public DebugDrawer()
    {
        EffectMesh = new(RenderContext);
        EffectBox = new(RenderContext);
        EffectQuad = new(RenderContext);
        _engineCoreSingleton = Marshal.GetDelegateForFunctionPointer<GetEngineCoreSingletonDelegate>(Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8D 4C 24 ?? 48 89 4C 24 ?? 4C 8D 4D ?? 4C 8D 44 24 ??"))();
    }

    public void Dispose()
    {
        EffectQuad.Dispose();
        EffectBox.Dispose();
        EffectMesh.Dispose();
        RenderTarget?.Dispose();
        RenderContext.Dispose();
    }

    public void StartFrame()
    {
        ViewProj = ReadMatrix(_engineCoreSingleton + 0x1B4);
        Proj = ReadMatrix(_engineCoreSingleton + 0x174);
        View = ViewProj * SharpDX.Matrix.Invert(Proj);
        CameraWorld = SharpDX.Matrix.Invert(View);
        CameraAzimuth = MathF.Atan2(View.Column3.X, View.Column3.Z);
        CameraAltitude = MathF.Asin(View.Column3.Y);
        ViewportSize = ReadVec2(_engineCoreSingleton + 0x1F4);

        EffectMesh.UpdateConstants(RenderContext, new() { ViewProj = ViewProj, LightingWorldYThreshold = 55.Degrees().Cos() });
        EffectBox.UpdateConstants(RenderContext, new() { ViewProj = ViewProj });
        EffectQuad.UpdateConstants(RenderContext, new() { ViewProj = ViewProj });

        if (RenderTarget == null || RenderTarget.Size != ViewportSize)
        {
            RenderTarget?.Dispose();
            RenderTarget = new(RenderContext, (int)ViewportSize.X, (int)ViewportSize.Y);
        }
        RenderTarget.Bind(RenderContext);
    }

    public void EndFrame()
    {
        RenderContext.Execute();

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(0, 0));
        ImGui.Begin("world_overlay", ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground);
        ImGui.SetWindowSize(ImGui.GetIO().DisplaySize);

        if (RenderTarget != null)
        {
            ImGui.GetWindowDrawList().AddImage(RenderTarget.ImguiHandle, new(), new(RenderTarget.Size.X, RenderTarget.Size.Y));
        }

        var dl = ImGui.GetWindowDrawList();
        foreach (var l in _viewportLines)
            dl.AddLine(l.from, l.to, l.col, l.thickness);
        foreach (var c in _viewportCircles)
            dl.AddCircleFilled(c.center, c.radius, c.color);
        _viewportLines.Clear();
        _viewportCircles.Clear();

        ImGui.End();
        ImGui.PopStyleVar();
    }

    public void DrawWorldLine(Vector3 start, Vector3 end, uint color, int thickness = 1)
    {
        var p1 = start.ToSharpDX();
        var p2 = end.ToSharpDX();
        if (ClipLineToNearPlane(ref p1, ref p2))
            _viewportLines.Add((WorldToScreen(p1), WorldToScreen(p2), color, thickness));
    }

    public void DrawWorldPolygon(IEnumerable<Vector3> points, uint color, int thickness = 1)
    {
        foreach (var (a, b) in AdjacentPairs(points))
            DrawWorldLine(a, b, color, thickness);
    }

    public void DrawWorldAABB(Vector3 origin, Vector3 halfSize, uint color, int thickness = 1)
    {
        var min = origin - halfSize;
        var max = origin + halfSize;
        var aaa = new Vector3(min.X, min.Y, min.Z);
        var aab = new Vector3(min.X, min.Y, max.Z);
        var aba = new Vector3(min.X, max.Y, min.Z);
        var abb = new Vector3(min.X, max.Y, max.Z);
        var baa = new Vector3(max.X, min.Y, min.Z);
        var bab = new Vector3(max.X, min.Y, max.Z);
        var bba = new Vector3(max.X, max.Y, min.Z);
        var bbb = new Vector3(max.X, max.Y, max.Z);
        DrawWorldLine(aaa, aab, color, thickness);
        DrawWorldLine(aab, bab, color, thickness);
        DrawWorldLine(bab, baa, color, thickness);
        DrawWorldLine(baa, aaa, color, thickness);
        DrawWorldLine(aba, abb, color, thickness);
        DrawWorldLine(abb, bbb, color, thickness);
        DrawWorldLine(bbb, bba, color, thickness);
        DrawWorldLine(bba, aba, color, thickness);
        DrawWorldLine(aaa, aba, color, thickness);
        DrawWorldLine(aab, abb, color, thickness);
        DrawWorldLine(baa, bba, color, thickness);
        DrawWorldLine(bab, bbb, color, thickness);
    }
    public void DrawWorldAABB(AABB aabb, uint color, int thickness = 1) => DrawWorldAABB((aabb.Min + aabb.Max) * 0.5f, (aabb.Max - aabb.Min) * 0.5f, color, thickness);

    public void DrawWorldSphere(Vector3 center, float radius, uint color, int thickness = 1)
    {
        int numSegments = CurveApprox.CalculateCircleSegments(radius, 360.Degrees(), 0.1f);
        var prev1 = center + new Vector3(0, 0, radius);
        var prev2 = center + new Vector3(0, radius, 0);
        var prev3 = center + new Vector3(radius, 0, 0);
        for (int i = 1; i <= numSegments; ++i)
        {
            var dir = (i * 360.0f / numSegments).Degrees().ToDirection();
            var curr1 = center + radius * new Vector3(dir.X, 0, dir.Y);
            var curr2 = center + radius * new Vector3(0, dir.Y, dir.X);
            var curr3 = center + radius * new Vector3(dir.Y, dir.X, 0);
            DrawWorldLine(curr1, prev1, color, thickness);
            DrawWorldLine(curr2, prev2, color, thickness);
            DrawWorldLine(curr3, prev3, color, thickness);
            prev1 = curr1;
            prev2 = curr2;
            prev3 = curr3;
        }
    }

    public void DrawWorldTriangle(Vector3 v1, Vector3 v2, Vector3 v3, uint color, int thickness = 1)
    {
        DrawWorldLine(v1, v2, color, thickness);
        DrawWorldLine(v2, v3, color, thickness);
        DrawWorldLine(v3, v1, color, thickness);
    }

    public void DrawWorldPoint(Vector3 p, float radius, uint color, int thickness = 1)
    {
        var pw = p.ToSharpDX();
        var nearPlane = ViewProj.Column3;
        if (SharpDX.Vector4.Dot(new(pw, 1), nearPlane) <= 0)
            return;

        var ps = WorldToScreen(pw);
        foreach (var (from, to) in AdjacentPairs(CurveApprox.Circle(ps, radius, 1)))
            _viewportLines.Add((from, to, color, thickness));
    }

    public void DrawWorldPointFilled(Vector3 p, float radius, uint color)
    {
        var pw = p.ToSharpDX();
        var nearPlane = ViewProj.Column3;
        if (SharpDX.Vector4.Dot(new(pw, 1), nearPlane) <= 0)
            return;
        _viewportCircles.Add((WorldToScreen(pw), radius, color));
    }

    // arrow with pointer at p coming from the direction of q
    public void DrawWorldArrowPoint(Vector3 p, Vector3 q, float l, uint color, int thickness = 1)
    {
        var pw = p.ToSharpDX();
        var nearPlane = ViewProj.Column3;
        if (SharpDX.Vector4.Dot(new(pw, 1), nearPlane) <= 0)
            return;

        var qw = q.ToSharpDX();
        ClipLineToNearPlane(ref pw, ref qw);
        var ps = WorldToScreen(pw);
        var qs = WorldToScreen(qw);
        var d = Vector2.Normalize(qs - ps) * l;
        var n = new Vector2(-d.Y, d.X) * 0.5f;
        _viewportLines.Add((ps, ps + d + n, color, thickness));
        _viewportLines.Add((ps, ps + d - n, color, thickness));
    }

    public void DrawWorldArc(Vector3 a, Vector3 b, float h, float arrowA, float arrowB, uint color, int thickness = 1)
    {
        var delta = b - a;
        var len = delta.Length();
        h *= len;
        Vector3 Eval(Vector3 from, Vector3 delta, float u)
        {
            var res = from + u * delta;
            var coeff = u * 2 - 1;
            res.Y += h * (1 - coeff * coeff);
            return res;
        };

        const int NumPoints = 8;
        const float u0 = 0.05f;
        const float du = (1.0f - u0 * 2) / NumPoints;
        var from = Eval(a, delta, u0);

        if (arrowA > 1)
            DrawWorldArrowPoint(from, Eval(a, delta, 2 * u0), arrowA, color, thickness);

        for (int i = 1; i <= NumPoints; ++i)
        {
            var to = Eval(a, delta, u0 + i * du);
            DrawWorldLine(from, to, color, thickness);
            from = to;
        }

        if (arrowB > 1)
            DrawWorldArrowPoint(from, Eval(a, delta, 1 - 2 * u0), arrowB, color, thickness);
    }

    private unsafe SharpDX.Matrix ReadMatrix(nint address)
    {
        var p = (float*)address;
        SharpDX.Matrix mtx = new();
        for (var i = 0; i < 16; i++)
            mtx[i] = *p++;
        return mtx;
    }

    private unsafe SharpDX.Vector2 ReadVec2(nint address)
    {
        var p = (float*)address;
        return new(p[0], p[1]);
    }

    private bool ClipLineToNearPlane(ref SharpDX.Vector3 a, ref SharpDX.Vector3 b)
    {
        var n = ViewProj.Column3; // near plane
        var an = SharpDX.Vector4.Dot(new(a, 1), n);
        var bn = SharpDX.Vector4.Dot(new(b, 1), n);
        if (an <= 0 && bn <= 0)
            return false;

        if (an < 0 || bn < 0)
        {
            var ab = b - a;
            var abn = SharpDX.Vector3.Dot(ab, new(n.X, n.Y, n.Z));
            var t = -an / abn;
            if (an < 0)
                a = a + t * ab;
            else
                b = a + t * ab;
        }
        return true;
    }

    private Vector2 WorldToScreen(SharpDX.Vector3 w)
    {
        var p = SharpDX.Vector3.TransformCoordinate(w, ViewProj);
        return new Vector2(0.5f * ViewportSize.X * (1 + p.X), 0.5f * ViewportSize.Y * (1 - p.Y)) + ImGuiHelpers.MainViewport.Pos;
    }

    private static IEnumerable<(T, T)> AdjacentPairs<T>(IEnumerable<T> v) where T : struct
    {
        var en = v.GetEnumerator();
        if (!en.MoveNext())
            yield break;
        var first = en.Current;
        var from = en.Current;
        while (en.MoveNext())
        {
            yield return (from, en.Current);
            from = en.Current;
        }
        yield return (from, first);
    }
}
