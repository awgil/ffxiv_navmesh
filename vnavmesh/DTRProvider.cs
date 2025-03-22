using Dalamud.Game.Gui.Dtr;
using System;

namespace Navmesh;

public class DTRProvider : IDisposable
{
    private NavmeshManager _manager;
    private AsyncMoveRequest _asyncMove;
    private IDtrBarEntry _dtrBarEntry;

    public DTRProvider(NavmeshManager manager, AsyncMoveRequest asyncMove)
    {
        _manager = manager;
        _asyncMove = asyncMove;
        _dtrBarEntry = Service.DtrBar.Get("vnavmesh");
    }

    public void Dispose()
    {
        _dtrBarEntry.Remove();
    }

    public void Update()
    {
        _dtrBarEntry.Shown = Service.Config.EnableDTR;
        if (_dtrBarEntry.Shown)
        {
            var loadProgress = _manager.LoadTaskProgress;
            var status = loadProgress >= 0 ? $"{loadProgress * 100:f0}%" : _manager.Navmesh != null ? "Ready" : "Not Ready";
            if (_asyncMove.TaskInProgress)
                status = "Pathfinding";
            _dtrBarEntry.Text = "Mesh: " + status;
        }
    }
}
