using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

public static class Seed
{
    public static readonly Dictionary<uint, List<Vector3>> Points = new()
    {
        // Middle La Noscea
        [134] = [new Vector3(218.69f, 113.02f, -257.19f)],
        // Labyrinthos
        [956] = [new Vector3(425.59f, 166.41f, -458.36f), new Vector3(-0.19f, -31.05f, -36.21f), new Vector3(-591.30f, -191.12f, 302.06f)],
        // heritage
        [1191] = [new Vector3(505.40f, 145.15f, 150.72f)]
    };
}
