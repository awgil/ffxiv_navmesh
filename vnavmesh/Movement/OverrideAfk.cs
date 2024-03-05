using FFXIVClientStructs.FFXIV.Client.UI;

namespace Navmesh.Movement;

internal unsafe static class OverrideAFK
{
    public static void ResetTimers()
    {
        var module = UIModule.Instance()->GetInputTimerModule();
        module->AfkTimer = 0;
        module->ContentInputTimer = 0;
        module->InputTimer = 0;
        module->Unk1C = 0;
    }
}
