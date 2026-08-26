using System;
using System.Globalization;
using System.Numerics;

namespace Navmesh.Movement.Human;

// text of the pathfind echo, see ADR 0009. kept free of Dalamud so it can be tested outside the
// game; PathfindAnnouncer decides when these are printed and prints them
public static class PathfindChat
{
    public static string Searching(Vector3 dest, bool fly, float range)
    {
        var kind = fly ? "flight path" : "path";
        var tolerance = range > 0 ? Fmt($" within {range:f1}y") : "";
        return Fmt($"searching for {kind} to {dest.X:f1}, {dest.Y:f1}, {dest.Z:f1}{tolerance}");
    }

    public static string Done(TimeSpan elapsed) => Fmt($"done in {elapsed.TotalSeconds:f3} seconds");

    public static string Cancelled() => "cancelled";

    public static string Failed() => "failed";

    // the client runs under whatever culture the user has; a comma decimal separator would turn a
    // coordinate triple into six comma-separated numbers
    private static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
