using System;
using System.Runtime.InteropServices;

namespace Navmesh.Customizations;

[CustomizationTerritory(1055)]
internal class Z1055IslandSanctuary : NavmeshCustomization
{
    // empty customization to force rebuild for existing users
    public override int Version => 2;

    private static bool AlmostEqual(float t, float val) => MathF.Abs(t - val) < 0.001f;

    // i don't actually know what flags indicate a swim->dive transition, so in the general case we pretend that fishable water (0xB400) can be flown through
    // however, island sanctuary doesn't have normal fishing, so instead we check that all vertices of the primitive are almost exactly at Y=0 or -20, which normal ground never is
    public override void CustomizeScene(SceneExtractor scene)
    {
        foreach (var (_, m) in scene.Meshes)
        {
            foreach (var p in m.Parts)
            {
                foreach (ref var prim in CollectionsMarshal.AsSpan(p.Primitives))
                {
                    var v1 = p.Vertices[prim.V1];
                    var v2 = p.Vertices[prim.V2];
                    var v3 = p.Vertices[prim.V3];

                    if (AlmostEqual(v1.Y, 0) && AlmostEqual(v2.Y, 0) && AlmostEqual(v3.Y, 0))
                        prim.Flags |= SceneExtractor.PrimitiveFlags.FlyThrough;

                    if (AlmostEqual(v1.Y, -20) && AlmostEqual(v2.Y, -20) && AlmostEqual(v3.Y, -20))
                        prim.Flags |= SceneExtractor.PrimitiveFlags.FlyThrough;
                }
            }
        }
    }
}
