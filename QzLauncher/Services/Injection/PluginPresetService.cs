using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace QzLauncher.Services.Injection;

public class PluginPresetService
{
    private readonly string _presetsDir;
    private readonly string _stateFile;
    private readonly IniFile _iniFile;
    private readonly string _dllPath;

    public ObservableCollection<PresetModel> AvailablePresets { get; } = new();
    public PresetModel? CurrentPreset { get; private set; }

    public event Action? PresetsChanged;

    public PluginPresetService(string presetsDir, IniFile iniFile, string dllPath)
    {
        _presetsDir = presetsDir;
        _stateFile = Path.Combine(_presetsDir, "active_state.json");
        _iniFile = iniFile;
        _dllPath = dllPath;

        if (!Directory.Exists(_presetsDir))
        {
            Directory.CreateDirectory(_presetsDir);
        }
    }

    private string GetDllHash()
    {
        if (!File.Exists(_dllPath)) return string.Empty;
        try
        {
            using var sha256 = SHA256.Create();
            using var fs = File.OpenRead(_dllPath);
            return BitConverter.ToString(sha256.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
        catch { return string.Empty; }
    }

    public void LoadPresets()
    {
        AvailablePresets.Clear();
        var currentHash = GetDllHash();
        var currentIniData = _iniFile.ReadAll();
        string activeId = string.Empty;

        if (File.Exists(_stateFile))
        {
            try
            {
                var content = File.ReadAllText(_stateFile);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
                if (dict != null && dict.TryGetValue("ActiveId", out var id))
                {
                    activeId = id;
                }
            }
            catch { }
        }

        var jsonFiles = Directory.GetFiles(_presetsDir, "*.json")
            .Where(f => !Path.GetFileName(f).Equals("active_state.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        PresetModel? activePreset = null;

        foreach (var file in jsonFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                var preset = JsonSerializer.Deserialize<PresetModel>(content);
                if (preset != null)
                {
                    preset.FilePath = file;
                    preset.ConfigData.Remove("General");
                    AvailablePresets.Add(preset);

                    if (preset.Id == activeId)
                    {
                        activePreset = preset;
                    }
                }
            }
            catch { }
        }

        if (activePreset == null && AvailablePresets.Count > 0)
        {
            activePreset = AvailablePresets[0];
        }

        if (activePreset == null)
        {
            activePreset = CreatePresetInternal("默认预设", currentIniData, currentHash);
        }

        SetActivePreset(activePreset, saveToIni: false);
        PresetsChanged?.Invoke();
    }

    public void SwitchPreset(PresetModel preset)
    {
        if (preset == null) return;
        SetActivePreset(preset, saveToIni: true);
        PresetsChanged?.Invoke();
    }

    public PresetModel CreateNewPreset(string name)
    {
        var currentIniData = _iniFile.ReadAll();
        var preset = CreatePresetInternal(string.IsNullOrWhiteSpace(name) ? $"预设 {AvailablePresets.Count + 1}" : name, currentIniData, GetDllHash());
        SetActivePreset(preset, saveToIni: true);
        PresetsChanged?.Invoke();
        return preset;
    }

    public bool DeletePreset(PresetModel preset)
    {
        if (preset == null || AvailablePresets.Count <= 1)
        {
            // 至少保留一个预设
            return false;
        }

        try
        {
            if (File.Exists(preset.FilePath))
            {
                File.Delete(preset.FilePath);
            }
        }
        catch { }

        AvailablePresets.Remove(preset);

        if (CurrentPreset == preset)
        {
            var fallback = AvailablePresets.FirstOrDefault();
            if (fallback != null)
            {
                SetActivePreset(fallback, saveToIni: true);
            }
        }

        PresetsChanged?.Invoke();
        return true;
    }

    public void ResetAllPresets()
    {
        try
        {
            var files = Directory.GetFiles(_presetsDir, "*.json");
            foreach (var f in files)
            {
                File.Delete(f);
            }
        }
        catch { }

        AvailablePresets.Clear();
        CurrentPreset = null;
        LoadPresets();
    }

    public void UpdateCurrentPresetValue(string section, string key, string value)
    {
        if (CurrentPreset == null) return;

        if (!CurrentPreset.ConfigData.TryGetValue(section, out var sectionDict))
        {
            sectionDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CurrentPreset.ConfigData[section] = sectionDict;
        }

        sectionDict[key] = value;
        SavePresetToFile(CurrentPreset);
    }

    private void SetActivePreset(PresetModel preset, bool saveToIni)
    {
        foreach (var p in AvailablePresets)
        {
            p.IsActive = (p.Id == preset.Id);
        }

        CurrentPreset = preset;
        CurrentPreset.IsActive = true;

        SaveActiveState(preset.Id);

        if (saveToIni)
        {
            var dataToSync = new Dictionary<string, Dictionary<string, string>>(preset.ConfigData, StringComparer.OrdinalIgnoreCase);
            dataToSync.Remove("General");
            _iniFile.UpdateMultiple(dataToSync);
        }
    }

    private PresetModel CreatePresetInternal(string name, Dictionary<string, Dictionary<string, string>> currentIniData, string dllHash)
    {
        var cleanData = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in currentIniData)
        {
            if (!kvp.Key.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                cleanData[kvp.Key] = new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
            }
        }

        var preset = new PresetModel
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            DllHash = dllHash,
            ConfigData = cleanData
        };

        preset.FilePath = Path.Combine(_presetsDir, $"{preset.Id}.json");
        SavePresetToFile(preset);
        AvailablePresets.Add(preset);
        return preset;
    }

    private void SavePresetToFile(PresetModel preset)
    {
        try
        {
            var json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(preset.FilePath, json);
        }
        catch { }
    }

    private void SaveActiveState(string activeId)
    {
        try
        {
            var dict = new Dictionary<string, string> { { "ActiveId", activeId } };
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(dict));
        }
        catch { }
    }
}
