using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Collections.Generic;
using System.Linq;

namespace Navmesh.Debug;

public class DebugFloodFill
{
    private readonly List<Lumina.Excel.Sheets.TerritoryType> _tts = [];

    public DebugFloodFill()
    {
        _tts.AddRange(Service.LuminaSheet<Lumina.Excel.Sheets.TerritoryType>()!.Where(t => t.TerritoryIntendedUse.RowId == 1 && t.RowId != 250));
    }

    public void Draw()
    {
        var ff = FloodFill.Get();
        if (ff == null)
            return;

        ImGui.TextUnformatted("Zones with missing data");

        using var t = ImRaii.Table("zones", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
        if (!t)
            return;

        ImGui.TableSetupColumn("id", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthFixed, 250);
        ImGui.TableSetupColumn("num");

        foreach (var tt in _tts)
        {
            var count = ff.Seeds.TryGetValue(tt.RowId, out var v) ? v.Count : 0;
            if (count > 0)
                continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(tt.RowId.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(tt.PlaceName.ValueNullable?.Name.ToString() ?? "<unknown>");
        }
    }
}
