namespace Navmesh.Customizations;

[CustomizationTerritory(134)]
public class Z134MiddleLaNoscea : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        foreach (var (key, mesh) in scene.Meshes)
        {
            if (key.StartsWith("bg/ffxiv/sea_s1/fld/s1f1/collision/tr"))
            {
                foreach (var part in mesh.Parts)
                {
                    for (var i = 0; i < part.Primitives.Count; i++)
                    {
                        var prim = part.Primitives[i];
                        var v1 = part.Vertices[prim.V1];
                        var v2 = part.Vertices[prim.V2];
                        var v3 = part.Vertices[prim.V3];
                        if (v1.Y < 2 && v2.Y < 2 && v3.Y < 2)
                            part.Primitives[i] = prim with { Flags = prim.Flags | SceneExtractor.PrimitiveFlags.ForceUnwalkable };
                    }
                }
            }
        }
    }
}
