using DotRecast.Detour;

namespace Navmesh.Customizations;

[CustomizationBg("ffxiv/roc_r1/fld/r1f1/level/r1f1")]
class Z0155CoerthasCentralHighlands : NavmeshCustomization
{
    public override int Version => 6;

    public override void CustomizeSettings(DtNavMeshCreateParams config)
    {
        // broken staircase
        config.AddOffMeshConnection(new(-418.71f, 221.50f, -288.00f), new(-418.28f, 222.75f, -290.65f), bidirectional: true);

        // tiny doorway next to observatorium
        config.AddOffMeshConnection(new(202.18361f, 257.8792f, 80.24515f), new(199.19151f, 257.38525f, 77.14409f), bidirectional: true);
    }
}
