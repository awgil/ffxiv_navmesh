using DotRecast.Core.Numerics;
using System;
using System.Numerics;

namespace Navmesh.Debug;

public abstract class DebugRecast : IDisposable
{
    public abstract void Dispose();

    public static void DrawBaseInfo(UITree _tree, int gridW, int gridH, RcVec3f bbMin, RcVec3f bbMax, float cellSize, float cellHeight)
    {
        var playerPos = Service.ClientState.LocalPlayer?.Position ?? default;
        _tree.LeafNode($"Num cells: {gridW}x{gridH}");
        _tree.LeafNode($"Bounds: [{bbMin}] - [{bbMax}]");
        _tree.LeafNode($"Cell size: {cellSize}x{cellHeight}");
        _tree.LeafNode($"Player's cell: {(int)((playerPos.X - bbMin.X) / cellSize)}x{(int)((playerPos.Y - bbMin.Y) / cellHeight)}x{(int)((playerPos.Z - bbMin.Z) / cellSize)}");
    }

    public static Vector4 IntColor(int v, float a)
    {
        var mask = new BitMask((ulong)v);
        float r = (mask[1] ? 0.25f : 0) + (mask[3] ? 0.5f : 0) + 0.25f;
        float g = (mask[2] ? 0.25f : 0) + (mask[4] ? 0.5f : 0) + 0.25f;
        float b = (mask[0] ? 0.25f : 0) + (mask[5] ? 0.5f : 0) + 0.25f;
        return new(r, g, b, a);
    }
}
