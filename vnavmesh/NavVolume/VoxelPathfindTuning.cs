namespace Navmesh.NavVolume;

// Knobs on the volume A*, gathered in one place so the search reads as upstream's plus a set of
// named dials, and so it can be exercised without a Dalamud-backed Config behind it. Defaults
// reproduce upstream exactly; see ADR 0008 for what each one costs and buys.
public record struct VoxelPathfindTuning
{
    // Multiplies the heuristic. Moves are axis-only while the heuristic is euclidean, so the real
    // cost of reaching a node can exceed the estimate by up to sqrt(3) and the search spreads far
    // wider than the route needs. Above 1 trades optimality for speed, and the greedy straightening
    // that runs afterwards (ADR 0007) gives most of the length back.
    public float HeuristicWeight { get; set; }

    // How far the grandparent line-of-sight check may reach, yalms. 0 means no limit, which is what
    // upstream used. The check walks every voxel on the line, so an unbounded one across a long hop
    // is the most expensive single thing the search does.
    public float RaycastRange { get; set; }

    // Whether to collapse a node into its grandparent during the search at all. VoxelStringPull
    // does the same job on the finished path under UseStringPulling, so with that on this largely
    // pays for work that is about to be redone.
    public bool SearchRaycast { get; set; }

    // Give up after this many expansions. A goal in a different connected component of the volume
    // cannot be reached at any budget, and the partial path handed back when the budget runs out is
    // the same one that comes back after flooding the entire region.
    public int MaxSteps { get; set; }

    // Upstream's per-edge noise on the g-score. It lives here rather than being read from Config in
    // the inner loop, which also lets a test make the search deterministic.
    public float RandomnessMultiplier { get; set; }

    public static VoxelPathfindTuning Upstream => new()
    {
        HeuristicWeight = 1,
        RaycastRange = 0,
        SearchRaycast = true,
        MaxSteps = 1000000,
        RandomnessMultiplier = 1,
    };
}
