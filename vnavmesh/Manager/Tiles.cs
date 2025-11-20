using DotRecast.Recast;
using System.Numerics;

namespace Navmesh;

public partial class NavmeshManager
{
    public readonly ColliderSet Scene = new();
    public static readonly Vector3 BoundsMin = new(-1024);
    public static readonly Vector3 BoundsMax = new(1024);

    public RcBuilderResult?[,] Intermediates { get; private set; } = new RcBuilderResult?[0, 0];

    public bool TrackIntermediates;

    // keyed by zone
    public uint[] ActiveFestivals { get; private set; } = new uint[4];

    private bool _initialized;
}
