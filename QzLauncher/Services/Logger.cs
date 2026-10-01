using System.Diagnostics;
using System.IO;
using System.Text;

namespace QzLauncher.Services;

public static class Logger
{
    private static readonly object _lock = new();
    private static string LogDir => Path.Combine(ChioriWorkspace.RootDir, "logs");
    public static string CurrentLogFile => Path.Combine(LogDir, $"chiori_launcher_{DateTime.Now:yyyyMMdd}.log");

    public static event Action<string, string>? LogEmitted; // (level, message)

    static Logger()
    {
        try
        {
            if (!Directory.Exists(LogDir))
            {
                Directory.CreateDirectory(LogDir);
            }
        }
        catch { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        string fullMsg = ex == null ? message : $"{message} | 异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Write("ERROR", fullMsg);
    }

    private static void Write(string level, string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string line = $"[{timestamp}] [{level}] {message}";

        Debug.WriteLine(line);

        // 触发 UI 订阅事件
        try
        {
            LogEmitted?.Invoke(level, message);
        }
        catch { }

        // 写入本地日志文件
        lock (_lock)
        {
            try
            {
                File.AppendAllText(CurrentLogFile, line + Environment.NewLine, Encoding.UTF8);

                // 同步写一份到 Temp 方便排查
                string tempLog = Path.Combine(Path.GetTempPath(), "chiori_launcher.log");
                File.AppendAllText(tempLog, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    /// <summary>
    /// 使用系统默认文本编辑器打开当前日志文件
    /// </summary>
    public static void OpenLogFile()
    {
        try
        {
            if (File.Exists(CurrentLogFile))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = CurrentLogFile,
                    UseShellExecute = true
                });
            }
            else
            {
                OpenLogFolder();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"打开日志失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 打开日志所在的文件夹
    /// </summary>
    public static void OpenLogFolder()
    {
        try
        {
            if (!Directory.Exists(LogDir))
            {
                Directory.CreateDirectory(LogDir);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = LogDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"打开日志目录失败: {ex.Message}");
        }
    }
}
