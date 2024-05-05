using DotRecast.Core.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;

namespace Navmesh.Customizations;

[CustomizationTerritory(171)]
class Z0171DzemaelDarkhold : NavmeshCustomization
{
    public override int Version => 1;
    
    public override void CustomizeScene(SceneExtractor scene) 
    {
        //change worldbounds of all instances (These colliders are there just too low to make that mesh connection at the doors, increasing there Y by 1 fixes this)
        if (scene.Meshes.TryGetValue("bg/ffxiv/roc_r1/rad/r1r1/collision/r1r1_a1_dor4.pcb", out var mesh))
        {
            foreach (var instance in mesh.Instances)
            {
                instance.WorldBounds.Min.Y += 1f;
                instance.WorldBounds.Max.Y += 1f;
            }
        }
    }
    
    public Z0171DzemaelDarkhold()
    {
        Settings.Partitioning = DotRecast.Recast.RcPartition.MONOTONE;
    }
}
