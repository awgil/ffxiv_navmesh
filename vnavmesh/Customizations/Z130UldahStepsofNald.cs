using System.Collections.Generic;

namespace Navmesh.Customizations;

[CustomizationTerritory(130)]
class Z130UldahStepsofNald : NavmeshCustomization
{
    public override int Version => 1;
    
    public override void CustomizeScene(SceneExtractor scene) 
    {
        //Force all Primitives of Mesh as unwalkable
        if (scene.Meshes.TryGetValue("bg/ffxiv/wil_w1/twn/common/collision/w1t0_f0_kadn1.pcb", out var mesh))
            scene.Meshes["bg/ffxiv/wil_w1/twn/common/collision/w1t0_f0_kadn1.pcb"] = scene.UnwalkableMesh(mesh);
    }
    
    public Z130UldahStepsofNald()
    {
    }
}
