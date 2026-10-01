using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QzLauncher.Services;

public class LauncherConfig
{
    public string GameDir { get; set; } = string.Empty;
    public bool UseInjection { get; set; } = false;
    public string SelectedPlugin { get; set; } = "ChioriPlugin";
    public string ActivePresetId { get; set; } = "default";
    public bool EnableFpsHud { get; set; } = false;

    [JsonIgnore]
    public static string ConfigPath => ChioriWorkspace.ConfigPath;

    public static LauncherConfig Load()
    {
        ChioriWorkspace.EnsureInitialized();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<LauncherConfig>(json);
                if (cfg is not null)
                {
                    if (string.Equals(cfg.SelectedPlugin, "FuFuPlugin", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.SelectedPlugin = "ChioriPlugin";
                    }
                    return cfg;
                }
            }
        }
        catch { /* 忽略配置读取错误，走默认 */ }
        return new LauncherConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }
}
