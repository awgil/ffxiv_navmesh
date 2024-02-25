using Dalamud.Game.ClientState.Conditions;
using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Navmesh;
public class CompatModule : IDisposable
{
    bool IsSwapped = false;
    FollowPath _followPath;

    public CompatModule(FollowPath followPath)
    {
        Service.Framework.Update += OnFrameworkUpdate;
        _followPath = followPath;
    }

    public void EnsureLegacyMode()
    {
        if(Service.GameConfig.UiControl.TryGetUInt("MoveMode", out var value))
        {
            if(value == 0)
            {
                Service.GameConfig.UiControl.Set("MoveMode", 1);
                IsSwapped = true;
                Service.Log.Debug($"[CompatModule] switching to legacy input");
            }
        }
    }

    public void RestoreChanges()
    {
        if(IsSwapped)
        {
            Service.GameConfig.UiControl.Set("MoveMode", 0);
            IsSwapped = false;
            Service.Log.Debug($"[CompatModule] switching to standard input");
        }
    }

    void OnFrameworkUpdate(object _)
    {
        if (!Service.ClientState.IsLoggedIn) return;
        if (Service.Condition[ConditionFlag.LoggingOut])
        {
            RestoreChanges();
        }
        else if(_followPath.Waypoints.Count > 0)
        {
            EnsureLegacyMode();
        }
        else
        {
            RestoreChanges();
        }
    }

    public void Dispose()
    {
        RestoreChanges();
        Service.Framework.Update -= OnFrameworkUpdate;
    }
}
