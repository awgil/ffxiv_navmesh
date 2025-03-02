namespace Navmesh.Customizations;

[CustomizationTerritory(795)]
class Z0795EurekaPyros : NavmeshCustomization
{
    public override int Version => 1;

    // remove all floor triangle that are part of existing colliders, but are far below the walkable area of the map, as they can cause annoying false positives when calling PointOnFloor
    public override void CustomizeScene(SceneExtractor scene)
    {
        foreach (var (key, mesh) in scene.Meshes)
        {
            if (key.StartsWith("bg/ex2/05_zon_z3/fld/z3fc/collision"))
            {
                foreach (var part in mesh.Parts)
                {
                    for (var i = part.Primitives.Count - 1; i >= 0; i--)
                    {
                        var prim = part.Primitives[i];
                        var v1 = part.Vertices[prim.V1];
                        var v2 = part.Vertices[prim.V2];
                        var v3 = part.Vertices[prim.V3];
                        // lowest walkable point in pyros is (probably) the SW edge of the skoll prep area, which is at about Y=578
                        if (v1.Y < 100 && v2.Y < 100 && v3.Y < 100)
                            part.Primitives.RemoveAt(i);
                    }
                }
            }
        }
    }
}
