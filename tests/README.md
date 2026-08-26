# Tests

```
dotnet test tests/Navmesh.Tests
```

Not part of `vnavmesh.sln`, and it does not reference the plugin assembly: that one is
built x64 only and needs Dalamud at runtime, so it will not load in a test host. The
project compiles the sources under test in directly, which limits it to code that does not
touch Dalamud or the game. Geometry qualifies, which is the code worth testing here anyway,
since the alternative is checking it by flying around.

`VoxelPathfind` joined the list in ADR 0008. It is not quite pure: it reports bad input
through `Service`, which is Dalamud-backed, so `ServiceStub.cs` supplies the one member it
reaches for. Its neighbour walk reads three octree levels directly, which the single-level
`VoxelGrid` cannot provide, so `VoxelWorld` builds a three-level map through the same
`VoxelMap.Build` path `NavmeshBuilder` uses.

`PathfindChat` joined in ADR 0009. It is only string building, split off from the printing
side for exactly this reason: the wording and the number formatting are worth pinning down,
and neither of them needs a game running.
