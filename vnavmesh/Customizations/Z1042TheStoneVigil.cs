using System.Collections.Generic;

namespace Navmesh.Customizations;

[CustomizationTerritory(1042)]
class Z1042TheStoneVigil : NavmeshCustomization
{
    public override int Version => 1;
    
    public override void CustomizeScene(SceneExtractor scene) 
    {
        //Force all Primitives of Mesh as unwalkable
        if (scene.Meshes.TryGetValue("bg/ffxiv/roc_r1/dun/r1d1/collision/r1d1_b1_sas03.pcb", out var mesh))
            scene.Meshes["bg/ffxiv/roc_r1/dun/r1d1/collision/r1d1_b1_sas03.pcb"] = scene.UnwalkableMesh(mesh);
    }
    
    public Z1042TheStoneVigil()
    {
    }
}
