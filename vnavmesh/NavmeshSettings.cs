using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using DotRecast.Recast;
using ImGuiNET;
using System;

namespace Navmesh;

public class NavmeshSettings
{
    [Flags]
    public enum Filter
    {
        None = 0,
        LowHangingObstacles = 1 << 0,
        LedgeSpans = 1 << 1,
        WalkableLowHeightSpans = 1 << 2,
        Interiors = 1 << 3,
    }

    public float CellSize = 0.25f;
    public float CellHeight = 0.25f;
    public float AgentHeight = 2.0f;
    public float AgentRadius = 0.5f;
    public float AgentMaxClimb = 0.75f; // consider web bridges in lost city of amdapor (h)
    public float AgentMaxSlopeDeg = 55f;
    public Filter Filtering = Filter.LowHangingObstacles | Filter.LedgeSpans | Filter.WalkableLowHeightSpans;
    public float RegionMinSize = 8;
    public float RegionMergeSize = 20;
    public RcPartition Partitioning = RcPartition.WATERSHED;
    public float PolyMaxEdgeLen = 12f;
    public float PolyMaxSimplificationError = 1.5f;
    public int PolyMaxVerts = 6;
    public float DetailSampleDist = 6f;
    public float DetailMaxSampleError = 1f;

    // we assume that bounds are constant -1024 to 1024 along each axis (since that's the quantization range of position in some packets)
    // there is some code that relies on tiling being power-of-2
    // current values mean 128x128x128 L1 tiles -> 16x16x16 L2 tiles -> 2x2x2 voxels
    public int[] NumTiles = [16, 8, 8];


    public void Draw()
    {
        DrawConfigFloat(ref CellSize, 0.1f, 1.0f, 0.01f, "Rasterization: Cell Size (#cs)", """
            The xz-plane cell size to use for fields. [Limit: > 0] [Units: world]

            The voxelization cell size #cs defines the voxel size along both axes of
            the ground plane: x and z in Recast. This value is usually derived from the
            character radius `r`. A recommended starting value for #cs is either `r/2`
            or `r/3`. Smaller values of #cs will increase rasterization resolution and
            navmesh detail, but total generation time will increase exponentially.  In
            outdoor environments, `r/2` is often good enough.  For indoor scenes with
            tight spaces you might want the extra precision, so a value of `r/3` or
            smaller may give better results.

            The initial instinct is to reduce this value to something very close to zero
            to maximize the detail of the generated navmesh. This quickly becomes a case
            of diminishing returns, however. Beyond a certain point there's usually not
            much perceptable difference in the generated navmesh, but huge increases in
            generation time.  This hinders your ability to quickly iterate on level
            designs and provides little benefit.  The general recommendation here is to
            use as large a value for #cs as you can get away with.

            #cs and #ch define voxel/grid/cell size.  So their values have significant
            side effects on all parameters defined in voxel units.

            The minimum value for this parameter depends on the platform's floating point
            accuracy, with the practical minimum usually around 0.05.
            """);
        DrawConfigFloat(ref CellHeight, 0.1f, 1.0f, 0.01f, "Rasterization: Cell Height (#ch)", """
            The y-axis cell size to use for fields. [Limit: > 0] [Units: world]

            The voxelization cell height #ch is defined separately in order to allow for
            greater precision in height tests. A good starting point for #ch is half the
            #cs value. Smaller #ch values ensure that the navmesh properly connects areas
            that are only separated by a small curb or ditch.  If small holes are generated
            in your navmesh around where there are discontinuities in height (for example,
            stairs or curbs), you may want to decrease the cell height value to increase
            the vertical rasterization precision of Recast.

            #cs and #ch define voxel/grid/cell size.  So their values have significant
            side effects on all parameters defined in voxel units.

            The minimum value for this parameter depends on the platform's floating point
            accuracy, with the practical minimum usually around 0.05.
            """);
        DrawConfigFloat(ref AgentHeight, 0.1f, 5.0f, 0.1f, "Agent: Height", """
            Minimum floor to 'ceiling' height that will still allow the floor area to be considered walkable. [Limit: >= 3 * CellHeight] [Units: world]

            This value defines the worldspace height `h` of the agent in voxels. The value
            of #walkableHeight should be calculated as `ceil(h / ch)`.  Note this is based
            on #ch not #cs since it's a height value.

            Permits detection of overhangs in the source geometry that make the geometry
            below un-walkable. The value is usually set to the maximum agent height.
            """);
        DrawConfigFloat(ref AgentRadius, 0.0f, 5.0f, 0.1f, "Agent: Radius", """
            The distance to erode/shrink the walkable area of the heightfield away from obstructions. [Limit: >= 0] [Units: world]

            The parameter #walkableRadius defines the worldspace agent radius `r` in voxels.
            Most often, this value of #walkableRadius should be calculated as `ceil(r / cs)`.
            Note this is based on #cs since the agent radius is always parallel to the ground
            plane.

            If the #walkableRadius value is greater than zero, the edges of the navmesh will
            be pushed away from all obstacles by this amount.

            A non-zero #walkableRadius allows for much simpler runtime navmesh collision checks.
            The game only needs to check that the center point of the agent is contained within
            a navmesh polygon.  Without this erosion, runtime navigation checks need to collide
            the geometric projection of the agent's logical cylinder onto the navmesh with the
            boundary edges of the navmesh polygons.

            In general, this is the closest any part of the final mesh should get to an
            obstruction in the source geometry.  It is usually set to the maximum
            agent radius.

            If you want to have tight-fitting navmesh, or want to reuse the same navmesh for
            multiple agents with differing radii, you can use a `walkableRadius` value of zero.
            Be advised though that you will need to perform your own collisions with the navmesh
            edges, and odd edge cases issues in the mesh generation can potentially occur.  For
            these reasons, specifying a radius of zero is allowed but is not recommended.
            """);
        DrawConfigFloat(ref AgentMaxClimb, 0.1f, 5.0f, 0.1f, "Agent: Max Climb", """
            Maximum ledge height that is considered to still be traversable. [Limit: >= 0] [Units: world]

            The #walkableClimb value defines the maximum height of ledges and steps that
            the agent can walk up. Given a designer-defined `maxClimb` distance in world
            units, the value of #walkableClimb should be calculated as `ceil(maxClimb / ch)`.
            Note that this is using #ch not #cs because it's a height-based value.

            Allows the mesh to flow over low lying obstructions such as curbs and
            up/down stairways. The value is usually set to how far up/down an agent can step.
            """);
        DrawConfigFloat(ref AgentMaxSlopeDeg, 0.0f, 90.0f, 1.0f, "Agent: Max Slope", """
            The maximum slope that is considered walkable. [Limits: 0 <= value < 90] [Units: Degrees]

            The parameter #walkableSlopeAngle is to filter out areas of the world where
            the ground slope would be too steep for an agent to traverse. This value is
            defined as a maximum angle in degrees that the surface normal of a polgyon
            can differ from the world's up vector.  This value must be within the range
            `[0, 90]`.

            The practical upper limit for this parameter is usually around 85 degrees.
            """);
        DrawConfigFilteringCombo(ref Filtering, "Filtering", """
            Select which filtering passes to apply to voxelized geometry to remove some classes of artifacts.
            """);
        DrawConfigFloat(ref RegionMinSize, 0.0f, 150.0f, 1.0f, "Region: Min Size", """
            The minimum number of cells allowed to form isolated island areas. [Limit: >= 0] [Units: voxels]

            Watershed partitioning is really prone to noise in the input distance field.
            In order to get nicer areas, the areas are merged and small disconnected areas
            are removed after the water shed partitioning. The parameter #minRegionArea
            describes the minimum isolated region size that is still kept. A region is
            removed if the number of voxels in the region is less than the square of
            #minRegionArea.

            Any regions that are smaller than this area will be marked as unwalkable.
            This is useful in removing useless regions that can sometimes form on
            geometry such as table tops, box tops, etc.
            """);
        DrawConfigFloat(ref RegionMergeSize, 0.0f, 150.0f, 1.0f, "Region: Merge Size", """
            Any regions with a span count smaller than this value will, if possible, be merged with larger regions. [Limit: >=0] [Units: voxels]

            The triangulation process works best with small, localized voxel regions.
            The parameter #mergeRegionArea controls the maximum voxel area of a region
            that is allowed to be merged with another region.  If you see small patches
            missing here and there, you could lower the #minRegionArea value.
            """);
        DrawConfigPartitioningCombo(ref Partitioning, "Partitioning algorithm", """
            There are 3 martitioning methods, each with some pros and cons.
            """);
        DrawConfigFloat(ref PolyMaxEdgeLen, 0.0f, 50.0f, 1.0f, "Polygonization: Max Edge Length", """
            The maximum allowed length for contour edges along the border of the mesh. [Limit: >= 0] [Units: world]

            In certain cases, long outer edges may decrease the quality of the resulting
            triangulation, creating very long thin triangles. This can sometimes be
            remedied by limiting the maximum edge length, causing the problematic long
            edges to be broken up into smaller segments.

            The parameter #maxEdgeLen defines the maximum edge length and is defined in
            terms of voxels. A good value for #maxEdgeLen is something like
            `walkableRadius * 8`. A good way to adjust this value is to first set it really
            high and see if your data creates long edges. If it does, decrease #maxEdgeLen
            until you find the largest value which improves the resulting tesselation.

            Extra vertices will be inserted as needed to keep contour edges below this
            length. A value of zero effectively disables this feature.
            """);
        DrawConfigFloat(ref PolyMaxSimplificationError, 0.1f, 3.0f, 0.1f, "Polygonization: Max Edge Simplification Error", """
            The maximum distance a simplfied contour's border edges should deviate from the original raw contour. [Limit: >=0] [Units: voxels]

            When the rasterized areas are converted back to a vectorized representation,
            the #maxSimplificationError describes how loosely the simplification is done.
            The simplification process uses the Ramer–Douglas-Peucker algorithm,
            and this value describes the max deviation in voxels.

            Good values for #maxSimplificationError are in the range `[1.1, 1.5]`.
            A value of `1.3` is a good starting point and usually yields good results.
            If the value is less than `1.1`, some sawtoothing starts to appear at the
            generated edges.  If the value is more than `1.5`, the mesh simplification
            starts to cut some corners it shouldn't.

            The effect of this parameter only applies to the xz-plane.
            """);
        DrawConfigInt(ref PolyMaxVerts, 3, 12, 1, "Polygonization: Max Vertices per Polygon", """
            The maximum number of vertices allowed for polygons generated during the contour to polygon conversion process. [Limit: >= 3]

            If the mesh data is to be used to construct a Detour navigation mesh, then the upper limit
            is limited to <= #DT_VERTS_PER_POLYGON.
            """); // TODO: fix the limit to make it always suitable for detour
        DrawConfigFloat(ref DetailSampleDist, 0.0f, 16.0f, 1.0f, "Detail Mesh: Sample Distance", """
            Sampling distance to use when generating the detail mesh. [Limits: 0 or >= 0.9] [Units: voxels]
            """); // TODO: verify that it's actually in voxels
        DrawConfigFloat(ref DetailMaxSampleError, 0.0f, 16.0f, 1.0f, "Detail Mesh: Max Sample Error", """
            The maximum distance the detail mesh surface should deviate from heightfield data. (For height detail only.) [Limit: >= 0] [Units: world]
            """); // TODO: verify that it's actually in voxels
        DrawConfigInt(ref NumTiles[0], 1, 32, 1, "L1 Tile count", """
            Number of tiles per axis for first-level subdivision. Has to be power-of-2. [Limit: 1 <= value <= 32]
            Affects both navmesh and nav volume.
            """);
        DrawConfigInt(ref NumTiles[1], 1, 32, 1, "L2 Tile count", """
            Number of tiles per axis for second-level subdivision. Has to be power-of-2. [Limit: 1 <= value <= 32]
            Affects only nav volume.
            """);
        DrawConfigInt(ref NumTiles[2], 1, 32, 1, "L3 Voxel count", """
            Number of leaf voxels per axis per tile. Has to be power-of-2. [Limit: 1 <= value <= 32]
            Affects only nav volume.
            """);
    }

    private void DrawConfigFloat(ref float value, float min, float max, float increment, string label, string help)
    {
        ImGui.SetNextItemWidth(300);
        ImGui.InputFloat(label, ref value);
        ImGuiComponents.HelpMarker(help);
    }

    private void DrawConfigInt(ref int value, int min, int max, int increment, string label, string help)
    {
        ImGui.SetNextItemWidth(300);
        ImGui.InputInt(label, ref value);
        ImGuiComponents.HelpMarker(help);
    }

    private void DrawConfigFilteringCombo(ref Filter value, string label, string help)
    {
        ImGui.SetNextItemWidth(300);
        using var combo = ImRaii.Combo(label, value.ToString());
        if (!combo)
        {
            ImGuiComponents.HelpMarker(help);
            return;
        }
        DrawConfigFilteringEnum(ref value, Filter.LowHangingObstacles, "Low-hanging obstacles", """
            Marks non-walkable spans as walkable if their maximum is within #walkableClimb of the span below them.

            This removes small obstacles and rasterization artifacts that the agent would be able to walk over
            such as curbs.  It also allows agents to move up terraced structures like stairs.

            Obstacle spans are marked walkable if: obstacleSpan.smax - walkableSpan.smax < walkableClimb
            """);
        DrawConfigFilteringEnum(ref value, Filter.LedgeSpans, "Ledge spans", """
            Marks spans that are ledges as not-walkable.

            A ledge is a span with one or more neighbors whose maximum is further away than #walkableClimb
            from the current span's maximum.
            This method removes the impact of the overestimation of conservative voxelization 
            so the resulting mesh will not have regions hanging in the air over ledges.

            A span is a ledge if: abs(currentSpan.smax - neighborSpan.smax) > walkableClimb
            """);
        DrawConfigFilteringEnum(ref value, Filter.WalkableLowHeightSpans, "Walkable low-height spans", """
            Marks walkable spans as not walkable if the clearance above the span is less than the specified #walkableHeight.

            For this filter, the clearance above the span is the distance from the span's 
            maximum to the minimum of the next higher span in the same column.
            If there is no higher span in the column, the clearance is computed as the
            distance from the top of the span to the maximum heightfield height.
            """);
        DrawConfigFilteringEnum(ref value, Filter.Interiors, "Interiors", """
            Marks spans inside manifold geometry (or below non-manifold) as non-walkable.
            """);
    }

    private void DrawConfigFilteringEnum(ref Filter value, Filter mask, string label, string help)
    {
        bool set = value.HasFlag(mask);
        if (ImGui.Checkbox(label, ref set))
            value ^= mask;
        ImGuiComponents.HelpMarker(help);
    }

    private void DrawConfigPartitioningCombo(ref RcPartition value, string label, string help)
    {
        ImGui.SetNextItemWidth(300);
        using var combo = ImRaii.Combo(label, value switch
        {
            RcPartition.WATERSHED => "Watershed",
            RcPartition.MONOTONE => "Monotone",
            RcPartition.LAYERS => "Layer",
            _ => "???"
        });
        if (!combo)
        {
            ImGuiComponents.HelpMarker(help);
            return;
        }

        DrawConfigPartitioningEnum(ref value, RcPartition.WATERSHED, "Watershed", """
            Watershed partitioning:
             - the classic Recast partitioning
             - creates the nicest tessellation
             - usually slowest
             - partitions the heightfield into nice regions without holes or overlaps
             - the are some corner cases where this method creates produces holes and overlaps
                - holes may appear when a small obstacles is close to large open area (triangulation can handle this)
                - overlaps may occur if you have narrow spiral corridors (i.e stairs), this make triangulation to fail
            Generally the best choice if you precompute the nacmesh, use this if you have large open areas.
            """);
        DrawConfigPartitioningEnum(ref value, RcPartition.MONOTONE, "Monotone", """
            Monotone partitioning:
             - fastest
             - partitions the heightfield into regions without holes and overlaps (guaranteed)
             - creates long thin polygons, which sometimes causes paths with detours
            Use this if you want fast navmesh generation.
            """);
        DrawConfigPartitioningEnum(ref value, RcPartition.LAYERS, "Layer", """
            Layer partitioning
             - quite fast
             - partitions the heighfield into non-overlapping regions
             - relies on the triangulation code to cope with holes (thus slower than monotone partitioning)
             - produces better triangles than monotone partitioning
             - does not have the corner cases of watershed partitioning
             - can be slow and create a bit ugly tessellation (still better than monotone)
               if you have large open areas with small obstacles (not a problem if you use tiles)
            Good choice to use for tiled navmesh with medium and small sized tiles.
            """);
    }

    private void DrawConfigPartitioningEnum(ref RcPartition value, RcPartition choice, string label, string help)
    {
        if (ImGui.RadioButton(label, value.Equals(choice)))
            value = choice;
        ImGuiComponents.HelpMarker(help);
    }
}
