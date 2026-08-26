namespace Navmesh;

// VoxelPathfind reports bad input through Service, which is Dalamud-backed and cannot load in a
// test host. The sources under test are compiled in rather than referenced (see the csproj), so the
// tests supply the one member the pathfinder actually reaches for.
internal static class Service
{
    public static readonly StubLog Log = new();

    internal sealed class StubLog
    {
        public void Error(string message) { }
        public void Debug(string message) { }
    }
}
