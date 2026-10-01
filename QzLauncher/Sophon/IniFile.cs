using System.IO;
using System.Text;

namespace FufuLauncher.Helpers;

public sealed class IniFile
{
    private readonly string _path;

    public IniFile(string path)
    {
        _path = path;
    }

    public Dictionary<string, Dictionary<string, string>> ReadAll()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path))
        {
            return result;
        }

        string currentSection = "";
        foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed.Substring(1, trimmed.Length - 2).Trim();
                if (!result.ContainsKey(currentSection))
                {
                    result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            int eqIndex = trimmed.IndexOf('=');
            if (eqIndex > 0)
            {
                string key = trimmed.Substring(0, eqIndex).Trim();
                string val = trimmed.Substring(eqIndex + 1).Trim();
                if (!result.ContainsKey(currentSection))
                {
                    result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                result[currentSection][key] = val;
            }
        }

        return result;
    }

    public void WriteValue(string section, string key, string value)
    {
        var all = ReadAll();
        if (!all.ContainsKey(section))
        {
            all[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        all[section][key] = value;
        SaveAll(all);
    }

    public void UpdateMultiple(Dictionary<string, Dictionary<string, string>> updates)
    {
        var all = ReadAll();
        foreach (var secPair in updates)
        {
            if (!all.ContainsKey(secPair.Key))
            {
                all[secPair.Key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            foreach (var kvp in secPair.Value)
            {
                all[secPair.Key][kvp.Key] = kvp.Value;
            }
        }
        SaveAll(all);
    }

    private void SaveAll(Dictionary<string, Dictionary<string, string>> all)
    {
        var sb = new StringBuilder();
        foreach (var sec in all)
        {
            sb.AppendLine($"[{sec.Key}]");
            foreach (var kvp in sec.Value)
            {
                sb.AppendLine($"{kvp.Key}={kvp.Value}");
            }
            sb.AppendLine();
        }

        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);
    }
}
