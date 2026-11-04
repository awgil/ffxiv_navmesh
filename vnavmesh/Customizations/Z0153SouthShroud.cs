namespace Navmesh.Customizations;

[CustomizationTerritory(153)]
internal class Z0153SouthShroud : NavmeshCustomization
{
	public override int Version => 1;

	public override void CustomizeScene(SceneExtractor scene)
	{
		if (scene.Meshes.TryGetValue("<plane one-sided>", out var planes))
		{
			// boxes around doors in the redbelly hive area
			planes.Instances.RemoveAll(i => i.WorldTransform.Row1.Y is > 3 and < 4);
		}
	}
}
