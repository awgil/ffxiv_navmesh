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
    public float RandomnessMultiplier = 1f;
    public bool EnableStuckDetection = true;
    public float StuckTimeoutSeconds = 5.0f;
    public float StuckDistanceThreshold = 0.5f;
    public int MaxRetryAttempts = 3;

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
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Randomness Multiplier", ref RandomnessMultiplier, 0f, 1.0f, "%.2f"))
            NotifyModified();
        
        ImGui.Separator();
        ImGui.Text("Stuck Detection");
        if (ImGui.Checkbox("Enable stuck detection and auto re-pathing", ref EnableStuckDetection))
            NotifyModified();
        if (EnableStuckDetection)
        {
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Stuck timeout (seconds)", ref StuckTimeoutSeconds, 1.0f, 15.0f, "%.1f"))
                NotifyModified();
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Movement threshold (distance)", ref StuckDistanceThreshold, 0.1f, 2.0f, "%.1f"))
                NotifyModified();
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("Max retry attempts", ref MaxRetryAttempts, 1, 10))
                NotifyModified();
        }
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
