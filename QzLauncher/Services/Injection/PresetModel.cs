using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace QzLauncher.Services.Injection;

public class PresetModel : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "默认预设";
    public string DllHash { get; set; } = string.Empty;
    public Dictionary<string, Dictionary<string, string>> ConfigData { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;

    private bool _isActive;
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
