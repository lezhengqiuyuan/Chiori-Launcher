using System.IO;
using System.Text;

namespace QzLauncher.Services.Injection;

/// <summary>
/// 健壮高效的 INI 配置文件解析与回写工具
/// </summary>
public class IniFile
{
    private readonly string _path;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    public IniFile(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    /// <summary>
    /// 读取 INI 文件中的所有 Section 与 Key-Value 字典
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ReadAll()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path)) return result;

        string currentSection = string.Empty;
        var lines = File.ReadAllLines(_path, Encoding.UTF8);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("#"))
                continue;

            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                currentSection = trimmed.Substring(1, trimmed.Length - 2).Trim();
                if (!result.ContainsKey(currentSection))
                {
                    result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            int separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex > 0 && !string.IsNullOrEmpty(currentSection))
            {
                string key = trimmed.Substring(0, separatorIndex).Trim();
                string value = trimmed.Substring(separatorIndex + 1).Trim();
                result[currentSection][key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// 读取指定 Section 下特定 Key 的值
    /// </summary>
    public string? Read(string section, string key, string? defaultValue = null)
    {
        var all = ReadAll();
        if (all.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var val))
        {
            return val;
        }
        return defaultValue;
    }

    /// <summary>
    /// 更新指定 Section 中的 Key 值
    /// </summary>
    public void WriteValue(string section, string key, string value)
    {
        if (!File.Exists(_path))
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(_path, $"[{section}]\r\n{key} = {value}\r\n", Utf8WithoutBom);
            return;
        }

        var lines = new List<string>(File.ReadAllLines(_path, Encoding.UTF8));
        int sectionStartIndex = -1;
        int nextSectionIndex = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                string current = trimmed.Substring(1, trimmed.Length - 2).Trim();
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase))
                {
                    sectionStartIndex = i;
                }
                else if (sectionStartIndex != -1)
                {
                    nextSectionIndex = i;
                    break;
                }
            }
        }

        if (sectionStartIndex == -1)
        {
            lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.Add($"{key} = {value}");
        }
        else
        {
            int endIndex = nextSectionIndex != -1 ? nextSectionIndex : lines.Count;
            bool keyFound = false;

            for (int i = sectionStartIndex + 1; i < endIndex; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith(";") || line.StartsWith("#")) continue;

                int eqIndex = line.IndexOf('=');
                if (eqIndex > 0)
                {
                    string existingKey = line.Substring(0, eqIndex).Trim();
                    if (existingKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key} = {value}";
                        keyFound = true;
                        break;
                    }
                }
            }

            if (!keyFound)
            {
                lines.Insert(endIndex, $"{key} = {value}");
            }
        }

        File.WriteAllLines(_path, lines, Utf8WithoutBom);
    }

    /// <summary>
    /// 批量更新 INI 文件中的所有 Sections（如切换预设时全量回写）
    /// </summary>
    public void UpdateMultiple(Dictionary<string, Dictionary<string, string>> data)
    {
        if (data == null) return;
        foreach (var section in data)
        {
            foreach (var kv in section.Value)
            {
                WriteValue(section.Key, kv.Key, kv.Value);
            }
        }
    }
}
