using System.Numerics;

namespace Navmesh;

public static class MapUtils
{
    public static Vector3? FlagToPoint(NavmeshQuery q)
    {
        var flag = GetFlagPosition();
        if (flag == null)
            return null;
        return q.FindPointOnFloor(new(flag.Value.X, 1024, flag.Value.Y));
    }

    private unsafe static Vector2? GetFlagPosition()
    {
        var map = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
        if (map == null || map->FlagMarkerCount == 0)
            return null;
        var marker = map->FlagMapMarkers[0];
        return new(marker.XFloat, marker.YFloat);
    }
}
