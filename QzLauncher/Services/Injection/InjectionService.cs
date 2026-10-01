using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QzLauncher.Services.Injection;

public class InjectionService
{
    private const uint ProcessAllAccess = 0x001F0FFF;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern nint VirtualAllocEx(nint hProcess, nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateRemoteThread(nint hProcess, nint lpThreadAttributes, nuint dwStackSize, nint lpStartAddress, nint lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(nint hThread, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(nint hProcess, nint lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string lpPathName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LaunchGameAndInjectDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string gamePath,
        [MarshalAs(UnmanagedType.LPWStr)] string dllPath,
        [MarshalAs(UnmanagedType.LPWStr)] string commandLineArgs,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder errorMessage,
        int errorMessageSize);

    /// <summary>
    /// 使用 Fufu 原生核心引擎 Launcher.dll 执行拉起并挂起注入插件 (与 Fufu 100% 同源稳定生效)
    /// </summary>
    public static (bool success, string message, int processId) LaunchGameWithPluginNative(string gameExePath, string dllPath, string commandLineArgs = "")
    {
        // 终极物理安全拦截：国服绝对严禁注入启动
        if (gameExePath.Contains("YuanShen", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "【安全熔断】目标为原神国服客户端 (YuanShen.exe)！根据最高安全准则严禁向国服注入任何第三方插件以保护账号安全！", 0);
        }

        if (!File.Exists(gameExePath))
        {
            return (false, $"游戏主程序不存在: {gameExePath}", 0);
        }

        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
        {
            return (false, $"插件 DLL 不存在: {dllPath}", 0);
        }

        string launcherDllPath = ChioriWorkspace.LauncherDllPath;
        if (!File.Exists(launcherDllPath))
        {
            // 尝试自解压
            ChioriWorkspace.ExtractEmbeddedResource("Launcher.dll", launcherDllPath, true);
        }

        if (!File.Exists(launcherDllPath))
        {
            return (false, "注入核心引擎 Launcher.dll 不存在，无法以挂起方式注入插件！", 0);
        }

        string previousDir = Environment.CurrentDirectory;
        try
        {
            string launcherDir = Path.GetDirectoryName(launcherDllPath) ?? AppContext.BaseDirectory;
            
            // 关键：Launcher.dll 基于当前工作目录扫描 Plugins 目录，必须切换到包含完整插件的目录
            Environment.CurrentDirectory = launcherDir;
            SetDllDirectoryW(launcherDir);

            nint hModule = LoadLibraryW(launcherDllPath);
            if (hModule == nint.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return (false, $"加载注入引擎 Launcher.dll 失败 (错误码: {err})", 0);
            }

            nint procPtr = GetProcAddress(hModule, "LaunchGameAndInject");
            if (procPtr == nint.Zero)
            {
                return (false, "Launcher.dll 未导出 LaunchGameAndInject 函数", 0);
            }

            var func = Marshal.GetDelegateForFunctionPointer<LaunchGameAndInjectDelegate>(procPtr);
            var buffer = new StringBuilder(2048);

            int result = func(gameExePath, dllPath, commandLineArgs ?? "", buffer, buffer.Capacity);
            string output = buffer.ToString().Trim();

            if (result == 0)
            {
                int pid = 0;
                if (!int.TryParse(output, out pid) || pid <= 0)
                {
                    // 进程刚刚以挂起状态拉起，捕捉系统内 GenshinImpact 真实主进程 PID
                    for (int i = 0; i < 15; i++)
                    {
                        var procs = Process.GetProcessesByName("GenshinImpact");
                        var found = procs.FirstOrDefault(p => !p.HasExited);
                        if (found != null)
                        {
                            pid = found.Id;
                            break;
                        }
                        Thread.Sleep(100);
                    }
                }

                // 检查 Launcher.log 日志确认注入状态
                string logPath = Path.Combine(launcherDir, "Launcher.log");
                string logDetail = "";
                if (File.Exists(logPath))
                {
                    try
                    {
                        string logContent = File.ReadAllText(logPath);
                        if (logContent.Contains("Total plugins injected: 0", StringComparison.OrdinalIgnoreCase))
                        {
                            return (false, "原生引擎未能挂载插件 (Total injected: 0)，请检查插件 DLL 完整性！", 0);
                        }
                        if (logContent.Contains("Plugin injection completed successfully", StringComparison.OrdinalIgnoreCase))
                        {
                            logDetail = " · 插件已挂载生效";
                        }
                    }
                    catch { }
                }

                return (true, $"已成功拉起游戏并注入插件 (PID={pid}){logDetail}", pid);
            }
            else
            {
                return (false, $"Launcher.dll 注入启动失败 (Code={result}): {output}", 0);
            }
        }
        catch (Exception ex)
        {
            return (false, $"注入启动异常: {ex.Message}", 0);
        }
        finally
        {
            try { Environment.CurrentDirectory = previousDir; } catch { }
        }
    }

    /// <summary>
    /// 获取当前生效的目标插件 DLL 完整路径
    /// </summary>
    public static string? ResolveTargetPluginDll(string targetPlugin)
    {
        string pluginsRoot = Directory.Exists(ChioriWorkspace.PluginsDir)
            ? ChioriWorkspace.PluginsDir
            : Path.Combine(AppContext.BaseDirectory, "Plugins");

        if (!Directory.Exists(pluginsRoot)) return null;

        string subFolder = string.Equals(targetPlugin, "FPS", StringComparison.OrdinalIgnoreCase) ? "FPS" : "ChioriPlugin";
        string targetDir = Path.Combine(pluginsRoot, subFolder);

        // 如果 ChioriPlugin 目录尚不存在，尝试后向兼容 FuFuPlugin 目录
        if (!Directory.Exists(targetDir) && !string.Equals(subFolder, "FPS", StringComparison.OrdinalIgnoreCase))
        {
            string legacyDir = Path.Combine(pluginsRoot, "FuFuPlugin");
            if (Directory.Exists(legacyDir)) targetDir = legacyDir;
        }

        if (Directory.Exists(targetDir))
        {
            // 优先检查 config.ini 中指定的 File
            string iniPath = Path.Combine(targetDir, "config.ini");
            if (File.Exists(iniPath))
            {
                try
                {
                    var ini = new IniFile(iniPath);
                    string? designatedFile = ini.Read("General", "File");
                    if (!string.IsNullOrWhiteSpace(designatedFile))
                    {
                        string designatedPath = Path.Combine(targetDir, designatedFile);
                        if (File.Exists(designatedPath) && !designatedPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                        {
                            return designatedPath;
                        }
                    }
                }
                catch { }
            }

            var dlls = Directory.GetFiles(targetDir, "*.dll", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (dlls.Count > 0)
            {
                // 优先选择千织专属模块主插件
                var preferred = dlls.FirstOrDefault(f => Path.GetFileName(f).Contains("Chiori", StringComparison.OrdinalIgnoreCase))
                             ?? dlls.FirstOrDefault(f => Path.GetFileName(f).Contains("FPS", StringComparison.OrdinalIgnoreCase))
                             ?? dlls[0];
                return preferred;
            }
        }

        // 全局搜索备用 DLL
        var anyDll = Directory.GetFiles(pluginsRoot, "*.dll", SearchOption.AllDirectories)
            .FirstOrDefault(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));

        return anyDll;
    }

    /// <summary>
    /// 向指定 PID 的游戏进程执行远程线程 LoadLibraryW 注入
    /// </summary>
    public static (bool success, string message) InjectDll(int processId, string dllPath)
    {
        if (processId <= 0) return (false, "无效的进程 PID");

        // 终极物理安全拦截：检测到国服进程绝对熔断拒绝注入
        try
        {
            var proc = Process.GetProcessById(processId);
            string procName = proc.ProcessName;
            if (string.Equals(procName, "YuanShen", StringComparison.OrdinalIgnoreCase) || procName.Contains("YuanShen", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "【安全熔断】目标为原神国服客户端 (YuanShen.exe)！根据最高安全准则严禁向国服注入任何第三方插件以保护账号安全！");
            }

            try
            {
                string? mainModule = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(mainModule) && (mainModule.Contains("YuanShen", StringComparison.OrdinalIgnoreCase) || mainModule.Contains("YuanShen_Data", StringComparison.OrdinalIgnoreCase)))
                {
                    return (false, "【安全熔断】目标程序路径识别为国服客户端 (YuanShen)！严禁注入！");
                }
            }
            catch { }
        }
        catch { }

        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
        {
            return (false, $"注入 DLL 不存在: {dllPath}");
        }

        string absoluteDllPath = Path.GetFullPath(dllPath);
        nint hProcess = nint.Zero;
        nint remoteMem = nint.Zero;
        nint hThread = nint.Zero;

        try
        {
            hProcess = OpenProcess(ProcessAllAccess, false, processId);
            if (hProcess == nint.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return (false, $"无法打开目标游戏进程 (PID={processId})，错误码: {err}，请确保以管理员权限运行启动器。");
            }

            byte[] bytes = Encoding.Unicode.GetBytes(absoluteDllPath + "\0");
            nuint size = (nuint)bytes.Length;

            remoteMem = VirtualAllocEx(hProcess, nint.Zero, size, MemCommit | MemReserve, PageReadWrite);
            if (remoteMem == nint.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return (false, $"在目标进程申请内存失败，错误码: {err}");
            }

            if (!WriteProcessMemory(hProcess, remoteMem, bytes, size, out _))
            {
                int err = Marshal.GetLastWin32Error();
                return (false, $"向目标进程写入 DLL 路径失败，错误码: {err}");
            }

            nint kernel32 = GetModuleHandleW("kernel32.dll");
            if (kernel32 == nint.Zero) return (false, "定位 kernel32.dll 失败");

            nint loadLibraryW = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibraryW == nint.Zero) return (false, "定位 LoadLibraryW 地址失败");

            hThread = CreateRemoteThread(hProcess, nint.Zero, 0, loadLibraryW, remoteMem, 0, out _);
            if (hThread == nint.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return (false, $"创建远程线程失败，错误码: {err}");
            }

            // 等待注入完成（最多等待 10 秒）
            uint waitRes = WaitForSingleObject(hThread, 10000);
            if (waitRes != 0)
            {
                return (false, "远程线程执行超时");
            }

            GetExitCodeThread(hThread, out uint exitCode);
            if (exitCode == 0)
            {
                return (false, "目标进程 LoadLibraryW 返回 0，DLL 加载失败（可能缺失依赖库或被安全策略拦截）。");
            }

            return (true, $"插件已成功注入到游戏进程 (PID={processId})，模块基址: 0x{exitCode:X8}");
        }
        catch (Exception ex)
        {
            return (false, $"注入发生异常: {ex.Message}");
        }
        finally
        {
            if (remoteMem != nint.Zero && hProcess != nint.Zero)
            {
                VirtualFreeEx(hProcess, remoteMem, 0, MemRelease);
            }
            if (hThread != nint.Zero)
            {
                CloseHandle(hThread);
            }
            if (hProcess != nint.Zero)
            {
                CloseHandle(hProcess);
            }
        }
    }
}
