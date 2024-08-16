namespace Navmesh.Customizations;

[CustomizationTerritory(1192)]
class Z1192LivingMemory : NavmeshCustomization {
    public override int Version => 1;

    public override void CustomizeScene(SceneExtractor scene) {
        if (scene.Meshes.TryGetValue("<box>", out var meshes)) {
            foreach (var thisMesh in meshes.Instances) {
                if (thisMesh.Id == 42412170687807488) {
                    thisMesh.ForceSetPrimFlags |= SceneExtractor.PrimitiveFlags.ForceUnwalkable;
                }
            }
        }
    }

    public Z1192LivingMemory() {
    }
}
