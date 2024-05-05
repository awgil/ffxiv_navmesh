using Dalamud.Game.Gui.Dtr;
using System;

namespace Navmesh;

public class DTRProvider : IDisposable
{
    private NavmeshManager _manager;
    private DtrBarEntry _dtrBarEntry;

    public DTRProvider(NavmeshManager manager)
    {
        _manager = manager;
        _dtrBarEntry = Service.DtrBar.Get("vnavmesh");
    }

    public void Dispose()
    {
        _dtrBarEntry.Dispose();
    }

    public void Update()
    {
        _dtrBarEntry.Shown = Service.Config.EnableDTR;
        if (_dtrBarEntry.Shown)
        {
            var loadProgress = _manager.LoadTaskProgress;
            var status = loadProgress >= 0 ? $"{loadProgress * 100:f0}%" : _manager.Navmesh != null ? "Ready" : "Not Ready";
            _dtrBarEntry.Text = "Mesh: " + status;
        }
    }
}
