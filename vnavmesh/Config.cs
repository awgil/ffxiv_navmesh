using ImGuiNET;
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace Navmesh;

public class Config
{
    private const int _version = 1;

    public bool AutoLoadNavmesh = true;
    public bool EnableDTR = true;
    public bool ShowQueryStatusInDTR = true;
    public bool AlignCameraToMovement;
    public bool ShowWaypoints;
    public bool ForceShowGameCollision;
    public bool CancelMoveOnUserInput;
    public bool StopOnStuck = false;
    public float StuckTolerance = 0.05f;
    public int StuckTimeoutMs = 500;
    public bool RetryOnStuck = true;
    public float RandomnessMultiplier = 1f;

    public event Action? Modified;

    public void NotifyModified() => Modified?.Invoke();

    public void Draw()
    {
        if (ImGui.Checkbox("Automatically load/build navigation data when changing zones", ref AutoLoadNavmesh))
            NotifyModified();
        if (ImGui.Checkbox("Enable DTR bar", ref EnableDTR))
            NotifyModified();
        if (ImGui.Checkbox("Show detailed query status in DTR", ref ShowQueryStatusInDTR))
            NotifyModified();
        if (ImGui.Checkbox("Align camera to movement direction", ref AlignCameraToMovement))
            NotifyModified();
        if (ImGui.Checkbox("Show active waypoints", ref ShowWaypoints))
            NotifyModified();
        if (ImGui.Checkbox("Always visualize game collision", ref ForceShowGameCollision))
            NotifyModified();
        if (ImGui.Checkbox("Cancel current path on player movement input", ref CancelMoveOnUserInput))
            NotifyModified();
        if (ImGui.Checkbox("Stop pathing when stuck", ref StopOnStuck))
            NotifyModified();

        if (StopOnStuck)
        {
            if (ImGui.SliderFloat("Stuck tolerance (units/frame)", ref StuckTolerance, 0.01f, 0.075f))
                NotifyModified();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The minimum distance the object must move each frame to avoid being considered stuck.");

            if (ImGui.SliderInt("Stuck timeout (ms)", ref StuckTimeoutMs, 100, 10_000))
                NotifyModified();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How long you can remain under the stuck threshold before stopping.");

            if (ImGui.Checkbox("Retry pathing after stop", ref RetryOnStuck))
                NotifyModified();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("If enabled, the agent will attempt to re-path after being considered stuck.");
        }

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Randomness Multiplier", ref RandomnessMultiplier, 0f, 1.0f, "%.2f"))
            NotifyModified();
    }

    public void Save(FileInfo file)
    {
        try
        {
            JObject jContents = new()
            {
                { "Version", _version },
                { "Payload", JObject.FromObject(this) }
            };
            File.WriteAllText(file.FullName, jContents.ToString());
        }
        catch (Exception e)
        {
            Service.Log.Error($"Failed to save config to {file.FullName}: {e}");
        }
    }

    public void Load(FileInfo file)
    {
        try
        {
            var contents = File.ReadAllText(file.FullName);
            var json = JObject.Parse(contents);
            var version = (int?)json["Version"] ?? 0;
            if (json["Payload"] is JObject payload)
            {
                payload = ConvertConfig(payload, version);
                var thisType = GetType();
                foreach (var (f, data) in payload)
                {
                    var thisField = thisType.GetField(f);
                    if (thisField != null)
                    {
                        var value = data?.ToObject(thisField.FieldType);
                        if (value != null)
                        {
                            thisField.SetValue(this, value);
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Service.Log.Error($"Failed to load config from {file.FullName}: {e}");
        }
    }

    private static JObject ConvertConfig(JObject payload, int version)
    {
        return payload;
    }
}
