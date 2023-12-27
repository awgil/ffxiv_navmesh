using Dalamud.Interface.Utility;
using Dalamud.Utility;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Navmesh;

public unsafe class DebugGeometry
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetEngineCoreSingletonDelegate();

    private nint _engineCoreSingleton;

    public SharpDX.Matrix ViewProj { get; private set; }
    public SharpDX.Matrix Proj { get; private set; }
    public SharpDX.Matrix View { get; private set; }
    public SharpDX.Matrix CameraWorld { get; private set; }
    public float CameraAzimuth { get; private set; } // facing north = 0, facing west = pi/4, facing south = +-pi/2, facing east = -pi/4
    public float CameraAltitude { get; private set; } // facing horizontally = 0, facing down = pi/4, facing up = -pi/4
    public SharpDX.Vector2 ViewportSize { get; private set; }

    private List<(Vector2 from, Vector2 to, uint col)> _viewportLines = new();

    public DebugGeometry()
    {
        _engineCoreSingleton = Marshal.GetDelegateForFunctionPointer<GetEngineCoreSingletonDelegate>(Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8D 4C 24 ?? 48 89 4C 24 ?? 4C 8D 4D ?? 4C 8D 44 24 ??"))();
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
    }

    public void EndFrame()
    {
        if (_viewportLines.Count == 0)
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(0, 0));
        ImGui.Begin("world_overlay", ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground);
        ImGui.SetWindowSize(ImGui.GetIO().DisplaySize);

        var dl = ImGui.GetWindowDrawList();
        foreach (var l in _viewportLines)
            dl.AddLine(l.from, l.to, l.col);
        _viewportLines.Clear();

        ImGui.End();
        ImGui.PopStyleVar();
    }

    public void DrawWorldLine(Vector3 start, Vector3 end, uint color)
    {
        var p1 = start.ToSharpDX();
        var p2 = end.ToSharpDX();
        if (!ClipLineToNearPlane(ref p1, ref p2))
            return;

        p1 = SharpDX.Vector3.TransformCoordinate(p1, ViewProj);
        p2 = SharpDX.Vector3.TransformCoordinate(p2, ViewProj);
        var p1screen = new Vector2(0.5f * ViewportSize.X * (1 + p1.X), 0.5f * ViewportSize.Y * (1 - p1.Y)) + ImGuiHelpers.MainViewport.Pos;
        var p2screen = new Vector2(0.5f * ViewportSize.X * (1 + p2.X), 0.5f * ViewportSize.Y * (1 - p2.Y)) + ImGuiHelpers.MainViewport.Pos;
        _viewportLines.Add((p1screen, p2screen, color));
    }

    public void DrawWorldCone(Vector3 center, float radius, Angle direction, Angle halfWidth, uint color)
    {
        int numSegments = CurveApprox.CalculateCircleSegments(radius, halfWidth, 0.1f);
        var delta = halfWidth / numSegments;

        var prev = center + radius * (direction - delta * numSegments).ToDirectionXZ();
        DrawWorldLine(center, prev, color);
        for (int i = -numSegments + 1; i <= numSegments; ++i)
        {
            var curr = center + radius * (direction + delta * i).ToDirectionXZ();
            DrawWorldLine(prev, curr, color);
            prev = curr;
        }
        DrawWorldLine(prev, center, color);
    }

    public void DrawWorldCircle(Vector3 center, float radius, uint color)
    {
        int numSegments = CurveApprox.CalculateCircleSegments(radius, 360.Degrees(), 0.1f);
        var prev = center + new Vector3(0, 0, radius);
        for (int i = 1; i <= numSegments; ++i)
        {
            var curr = center + radius * (i * 360.0f / numSegments).Degrees().ToDirectionXZ();
            DrawWorldLine(curr, prev, color);
            prev = curr;
        }
    }

    public void DrawWorldSphere(Vector3 center, float radius, uint color)
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
            DrawWorldLine(curr1, prev1, color);
            DrawWorldLine(curr2, prev2, color);
            DrawWorldLine(curr3, prev3, color);
            prev1 = curr1;
            prev2 = curr2;
            prev3 = curr3;
        }
    }

    private unsafe SharpDX.Matrix ReadMatrix(IntPtr address)
    {
        var p = (float*)address;
        SharpDX.Matrix mtx = new();
        for (var i = 0; i < 16; i++)
            mtx[i] = *p++;
        return mtx;
    }

    private unsafe SharpDX.Vector2 ReadVec2(IntPtr address)
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
}
