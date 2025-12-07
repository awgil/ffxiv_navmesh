using DotRecast.Detour;
using System.Collections.Generic;

namespace Navmesh.Customizations;

[CustomizationTerritory(146)]
class Z146SouthernThanalan : NavmeshCustomization
{
    public override int Version => 1;

    public override void CustomizeMesh(DtNavMesh mesh, List<uint> festivalLayers)
    {
        // zahar'ak gates main path
        LinkPoints(mesh, new(29.409502f, 3.3774254f, 5.090047f), new(32.08006f, 3.7143986f, 5.072673f), bidirectional: true);
        LinkPoints(mesh, new(319.59805f, 8f, -35.540222f), new(322.23926f, 8.032201f, -35.315277f), bidirectional: true);
        LinkPoints(mesh, new(385.54288f, 15.35475f, -68.275406f), new(387.24783f, 15.498692f, -70.5433f), bidirectional: true);
        LinkPoints(mesh, new(443.03668f, 16.128588f, -121.43631f), new(444.84265f, 16.042698f, -123.55241f), bidirectional: true);
        LinkPoints(mesh, new(533.8438f, 7.5996237f, -156.45427f), new(536.28577f, 7.5996237f, -157.03238f), bidirectional: true);
        LinkPoints(mesh, new(535.9662f, 7.5996237f, -113.552414f), new(537.5262f, 7.5996237f, -111.48574f), bidirectional: true);
        LinkPoints(mesh, new(544.1686f, 7.599619f, -63.10377f), new(544.2267f, 7.599618f, -60.27884f), bidirectional: true);
        LinkPoints(mesh, new(575.16187f, 7.5119467f, -83.26855f), new(578.0016f, 7.2896442f, -82.81813f), bidirectional: true);
        // zahar'ak side gate
        LinkPoints(mesh, new(172.92215f, 8.176805f, 143.83092f), new(171.1628f, 8.122168f, 145.88765f), bidirectional: true);
    }
}
