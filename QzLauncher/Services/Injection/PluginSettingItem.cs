using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QzLauncher.Services.Injection;

public class PluginSettingItem : INotifyPropertyChanged
{
    public string SectionKey { get; }
    public string DisplayName { get; set; }
    public string Type { get; }
    public string HelpUrl { get; }

    private string _rawValue = string.Empty;
    private readonly Action<string, string, string>? _onValueChanged;

    public string Category { get; set; } = "核心功能";
    public string TargetPlugin { get; set; } = "ChioriPlugin";

    public bool HasHelp => !string.IsNullOrWhiteSpace(HelpUrl);

    public bool IsBoolType => string.Equals(Type, "bool", StringComparison.OrdinalIgnoreCase);
    public bool IsKeyType => string.Equals(Type, "key", StringComparison.OrdinalIgnoreCase);
    public bool IsNumberType => string.Equals(Type, "int", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Type, "float", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Type, "number", StringComparison.OrdinalIgnoreCase);
    public bool IsStringType => !IsBoolType && !IsKeyType && !IsNumberType;

    public static List<VirtualKeyOption> AvailableKeys => VirtualKeyOption.GetCommonKeys();

    public PluginSettingItem(string sectionKey, string displayName, string type, string value, string helpUrl, Action<string, string, string>? onValueChanged, string category = "核心功能", string targetPlugin = "ChioriPlugin")
    {
        SectionKey = sectionKey;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? sectionKey : displayName;
        Type = (type ?? "string").ToLowerInvariant();
        _rawValue = value ?? string.Empty;
        HelpUrl = helpUrl ?? string.Empty;
        _onValueChanged = onValueChanged;
        Category = category;
        TargetPlugin = targetPlugin;
    }

    public string RawValue
    {
        get => _rawValue;
        set
        {
            if (_rawValue != value)
            {
                _rawValue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BoolValue));
                OnPropertyChanged(nameof(KeyValue));
                OnPropertyChanged(nameof(FloatValue));
                OnPropertyChanged(nameof(StringValue));
            }
        }
    }

    public bool BoolValue
    {
        get => _rawValue == "1" || string.Equals(_rawValue, "true", StringComparison.OrdinalIgnoreCase);
        set
        {
            var strVal = value ? "1" : "0";
            if (_rawValue != strVal)
            {
                _rawValue = strVal;
                OnPropertyChanged();
                NotifyChange("Value", strVal);
            }
        }
    }

    public int KeyValue
    {
        get => int.TryParse(_rawValue, out var v) ? v : 0;
        set
        {
            var strVal = value.ToString();
            if (_rawValue != strVal)
            {
                _rawValue = strVal;
                OnPropertyChanged();
                NotifyChange("Value", strVal);
            }
        }
    }

    public double FloatValue
    {
        get => double.TryParse(_rawValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        set
        {
            var strVal = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (_rawValue != strVal)
            {
                _rawValue = strVal;
                OnPropertyChanged();
                NotifyChange("Value", strVal);
            }
        }
    }

    public string StringValue
    {
        get => _rawValue;
        set
        {
            if (_rawValue != value)
            {
                _rawValue = value ?? string.Empty;
                OnPropertyChanged();
                NotifyChange("Value", _rawValue);
            }
        }
    }

    private void NotifyChange(string key, string value)
    {
        _onValueChanged?.Invoke(SectionKey, key, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
