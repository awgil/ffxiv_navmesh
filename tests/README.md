# Tests

```
dotnet test tests/Navmesh.Tests
```

Not part of `vnavmesh.sln`, and it does not reference the plugin assembly: that one is
built x64 only and needs Dalamud at runtime, so it will not load in a test host. The
project compiles the sources under test in directly, which limits it to code that does not
touch Dalamud or the game. Geometry qualifies, which is the code worth testing here anyway,
since the alternative is checking it by flying around.
