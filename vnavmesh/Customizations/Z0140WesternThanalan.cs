using DotRecast.Detour;
using System.Numerics;
using System.Collections.Generic;

namespace Navmesh.Customizations;

[CustomizationTerritory(140)]
internal class Z0140WesternThanalan : NavmeshCustomization
{
	public override int Version => 2;

    private void LinkPath(Navmesh mesh, List<Vector3> path)
    {
        for (var i = 0; i < path.Count-1; i++)
        {
            LinkPoints(mesh, path[i], path[i+1], Navmesh.AreaId.Shortcut);
            LinkPoints(mesh, path[i+1], path[i], Navmesh.AreaId.Shortcut);
        }
    }

	public override void CustomizeMesh(Navmesh mesh, List<uint> festivalLayers)
	{
		base.CustomizeMesh(mesh, festivalLayers);

        // uldah>scorpion
        LinkPath(mesh, [
            new(426.99088f, 93.9155f, 152.40146f),
            new(398.47162f, 84.97111f, 143.84796f),
            new(359.1448f, 74.74335f, 157.97179f),
            new(313.03302f, 64.7995f, 157.05261f),
            new(277.2021f, 54.160507f, 152.97801f)
        ]);
        // goblet
        LinkPath(mesh, [
            new(313.03302f, 64.7995f, 157.05261f),
            new(317.35007f, 67.275345f, 232.6594f)
        ]);
        // scorpion>silver bazaar
        LinkPath(mesh, [
            new(189.81735f, 51.99335f, 159.53635f),
            new(156.23552f, 51.150078f, 182.35542f),
            new(112.62422f, 51.54615f, 222.57841f),
            new(12.12237f, 52.66054f, 273.22778f),
            new(-65.63878f, 52.4164f, 287.29584f),
            new(-100.54567f, 49.538364f, 318.87463f),
            new(-140.41534f, 43.28235f, 350.7795f),
            new(-216.25105f, 31.532846f, 373.04108f)
        ]);
        // scorpion>horizon
        LinkPath(mesh, [
            new(208.67352f, 52.038116f, 116.44042f),
            new(201.36194f, 50.419308f, 61.550545f),
            new(176.58054f, 52.40618f, 34.829697f),
            new(93.228516f, 55.496067f, -23.552898f),
            new(93.23488f, 53.164715f, -58.111134f),
            new(159.9177f, 49.417847f, -146.97667f),
            new(99.774315f, 49.89678f, -188.35187f)
        ]);
        // scorpion>central
        LinkPath(mesh, [
            new(201.36194f, 50.419308f, 61.550545f),
            new(234.14839f, 51.040855f, 39.433617f),
            new(257.04526f, 52.525375f, -3.394511f)
        ]);
        // horizon>nophica's wells
        LinkPath(mesh, [
            new(93.228516f, 55.496067f, -23.552898f),
            new(60.99052f, 57.912163f, 12.046786f),
            new(44.88159f, 55.532764f, 42.61445f)
        ]);
        // nophicas cross
        LinkPath(mesh, [
            new(92.604645f, 15.122423f, 152.49191f),
            new(60.3148f, 17.632505f, 122.61662f),
            new(44.11512f, 21.719524f, 90.53238f),
            new(8.3533125f, 21.977991f, 78.75303f),
            new(-12.344802f, 17.610807f, 119.21537f),
            new(-52.721035f, 15.400875f, 136.849f),
            new(-58.978664f, 15.620071f, 170.845f)
        ]);
        // horizon>copperbell
        LinkPath(mesh, [
            new(159.9177f, 49.417847f, -146.97667f),
            new(258.5332f, 61.196674f, -157.92772f),
            new(276.20734f, 62.016293f, -174.24113f)
        ]);
		// Horizon>Vesper Bay
        LinkPath(mesh, [
            new(-85.52679f, 15.506252f, -256.68518f),
            new(-123.02397f, 15.505861f, -257.67828f),
            new(-187.35985f, 15.736494f, -266.33664f),
            new(-238.11607f, 15.989145f, -300.52383f),
            new(-275.41974f, 15.385908f, -312.26562f),
            new(-307.42465f, 19.75336f, -338.75467f)
        ]);
        // crescent cove
        LinkPath(mesh, [
            new(-238.11607f, 15.989145f, -300.52383f),
            new(-240.13986f, 15.351267f, -259.6219f),
            new(-273.1327f, 15.468161f, -238.97133f),
            new(-291.29257f, 17.162872f, -203.8298f)
        ]);
	}
}
