namespace Navmesh.Customizations;

[CustomizationTerritory(1190)]
public class Z1190Shaaloani : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene)
    {
        foreach (var (key, mesh) in scene.Meshes)
        {
            if (key.StartsWith("bg/ex5/01_xkt_x6/fld/x6f1/collision/tr"))
            {
                foreach (var part in mesh.Parts)
                {
                    for (var i = 0; i < part.Primitives.Count; i++)
                    {
                        var prim = part.Primitives[i];
                        // big skyfishing surface on W side of map extends pretty far inland, mark it as unwalkable so we don't get bogus tiles underground
                        if (prim.Material == 0x8000)
                            part.Primitives[i] = prim with { Flags = prim.Flags | SceneExtractor.PrimitiveFlags.ForceUnwalkable };
                    }
                }
            }
        }
    }
}
