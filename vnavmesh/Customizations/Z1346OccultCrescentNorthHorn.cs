namespace Navmesh.Customizations;

[CustomizationTerritory(1346)]
internal class Z1346OccultCrescentNorthHorn : NavmeshCustomization
{
	public override int Version => 1;

	public Z1346OccultCrescentNorthHorn()
	{
		// Watershed REALLY doesn't like the spiral staircase near Sinking Sanctuary and i am not skilled enough to tweak the config to fix it
		Settings.Partitioning = DotRecast.Recast.RcPartition.LAYERS;
	}
}
