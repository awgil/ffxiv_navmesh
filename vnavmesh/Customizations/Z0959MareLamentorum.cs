using DotRecast.Detour;

namespace Navmesh.Customizations;

[CustomizationTerritory(959)]
class Z0959MareLamentorum : NavmeshCustomization
{
	public override int Version => 1;

	public override void CustomizeSettings(DtNavMeshCreateParams config)
	{
		// all the little allagan bridges are too steep
		config.AddOffMeshConnection(new(-51, 42.5f, 466.6f), new(-52.4f, 43.8f, 472.3f), bidirectional: true);
		config.AddOffMeshConnection(new(112.9f, 45.5f, 460.9f), new(109.4f, 43.3f, 457.2f), bidirectional: true);
		config.AddOffMeshConnection(new(128.7f, 52.8f, 465.5f), new(131.5f, 54.5f, 467.8f), bidirectional: true);
		config.AddOffMeshConnection(new(308.9f, 108.5f, 26.4f), new(307.1f, 107.5f, 23.6f), bidirectional: true);
	}
}
