namespace Navmesh.Customizations;

[CustomizationTerritory(827)]
class Z0827EurekaHydatos : NavmeshCustomization
{
    public override int Version => 2;

    // remove all floor triangles that are part of existing colliders, but are below the walkable area of the map, as they can cause annoying false positives when calling PointOnFloor
    // this really only affects PW's boss room, which has some extra terrain beneath it
    public override void CustomizeScene(SceneExtractor scene)
    {
        foreach (var (key, mesh) in scene.Meshes)
        {
            if (key.StartsWith("bg/ex2/05_zon_z3/fld/z3fd/collision/tr"))
            {
                foreach (var part in mesh.Parts)
                {
                    for (var i = 0; i < part.Primitives.Count; i++)
                    {
                        var prim = part.Primitives[i];
                        var v1 = part.Vertices[prim.V1];
                        var v2 = part.Vertices[prim.V2];
                        var v3 = part.Vertices[prim.V3];
                        // lowest walkable point in hydatos proper is about Y=494, southernmost walkable point is around Z=-40; BA is underground and much further south
                        if (v1.Y < 480 && v2.Y < 480 && v3.Y < 480 && v1.Z < 0 && v2.Z < 0 && v3.Z < 0)
                            part.Primitives[i] = prim with { Flags = prim.Flags | SceneExtractor.PrimitiveFlags.ForceUnwalkable };
                    }
                }
            }
        }
    }

    public Z0827EurekaHydatos()
    {
        // watershed partitioning causes some annoying corner cases with large flat areas of map - very noticeable when pathfinding from Daphne spawn to Central Point
        Settings.Partitioning = DotRecast.Recast.RcPartition.LAYERS;
    }
}
