using ImGuiNET;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using Dalamud.Interface.ImGuiFileDialog;

namespace Navmesh;

public class Config
{
    private const int _version = 1;

    public bool AutoLoadNavmesh = true;
    public bool EnableDTR = true;
    public bool AlignCameraToMovement;
    public bool ShowWaypoints;
    public bool ForceShowGameCollision;
    public bool CancelMoveOnUserInput;
    public string MeshDirectory = string.Empty;

    private string _defaultMeshDirectory;
    public event Action? Modified;

    public void NotifyModified() => Modified?.Invoke();

    private FileDialogManager _fileDialogManager = new();
    public void Draw()
    {
        if (ImGui.InputText("Mesh Directory", ref MeshDirectory, 256))
        {
            if (!Directory.Exists(MeshDirectory))
                MeshDirectory = _defaultMeshDirectory;
            NotifyModified();
        }

        ImGui.SameLine();
        if (ImGui.Button("Browse"))
        {
            _fileDialogManager.OpenFolderDialog("Navmesh Directory", (success, path) =>
            {
                if (success && Directory.Exists(path))
                    MeshDirectory = path;
                NotifyModified();
            });
        }
        if (ImGui.Checkbox("Automatically load/build navigation data when changing zones", ref AutoLoadNavmesh))
            NotifyModified();
        if (ImGui.Checkbox("Enable DTR bar", ref EnableDTR))
            NotifyModified();
        if (ImGui.Checkbox("Align camera to movement direction", ref AlignCameraToMovement))
            NotifyModified();
        if (ImGui.Checkbox("Show active waypoints", ref ShowWaypoints))
            NotifyModified();
        if (ImGui.Checkbox("Always visualize game collision", ref ForceShowGameCollision))
            NotifyModified();
        if (ImGui.Checkbox("Cancel current path on player movement input", ref CancelMoveOnUserInput))
            NotifyModified();
        _fileDialogManager.Draw();
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
            _defaultMeshDirectory = Path.Combine(Service.PluginInterface.ConfigDirectory.FullName, "meshcache");
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

            if (string.IsNullOrEmpty(MeshDirectory) || !Directory.Exists(MeshDirectory))
            {
                MeshDirectory = _defaultMeshDirectory;
                NotifyModified();
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
