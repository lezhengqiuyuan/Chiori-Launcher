using System;
using System.IO;

namespace QzLauncher.Services;

/// <summary>
/// 千织启动器统一工作空间服务（严格防呆设计）：
/// 绿色免安装——优先使用启动器 exe 同级目录（整个文件夹可拷贝分发），若受限则自动回退至 LocalAppData 或临时目录。
/// 具备：多级优雅回退、线程安全锁、防自克隆死锁、空坏文件自愈、以及极速自检。
/// </summary>
public static class ChioriWorkspace
{
    private static readonly object _initLock = new();
    private static bool _isInitialized = false;

    // 启动器包根目录（exe 同级；只读时回退 LocalAppData/Temp），经防呆可写性自检确定
    private static string _resolvedBaseDir = string.Empty;

    /// <summary>启动器包根目录（= exe 所在目录；运行数据直接落于此目录，与 exe 同级）</summary>
    public static string BaseDir
    {
        get
        {
            if (string.IsNullOrEmpty(_resolvedBaseDir))
            {
                lock (_initLock)
                {
                    if (string.IsNullOrEmpty(_resolvedBaseDir))
                    {
                        _resolvedBaseDir = DetermineSafeBaseDir();
                    }
                }
            }
            return _resolvedBaseDir;
        }
    }

    /// <summary>启动器运行数据目录（= BaseDir，exe 同级）：Plugins/Assets/logs/config/Launcher.dll 均直接落于 exe 同级</summary>
    public static string RootDir => BaseDir;

    public static string ConfigPath => Path.Combine(RootDir, "qz_launcher_config.json");
    public static string PluginsDir => Path.Combine(RootDir, "Plugins");
    public static string PresetsDir => Path.Combine(PluginsDir, "Presets");
    public static string ChioriPluginDir => Path.Combine(PluginsDir, "ChioriPlugin");
    public static string FpsPluginDir => Path.Combine(PluginsDir, "FPS");
    public static string AssetsDir => Path.Combine(RootDir, "Assets");

    /// <summary>
    /// 注入核心引擎 Launcher.dll 物理路径（优先使用当前程序所在目录，兜底使用用户工作空间）
    /// </summary>
    public static string LauncherDllPath
    {
        get
        {
            string appLocal = Path.Combine(AppContext.BaseDirectory, "Launcher.dll");
            if (File.Exists(appLocal)) return appLocal;

            string rootLocal = Path.Combine(RootDir, "Launcher.dll");
            if (File.Exists(rootLocal)) return rootLocal;

            // 优先返回程序目录
            return appLocal;
        }
    }

    /// <summary>
    /// 初始化工作空间结构（幂等且线程安全）
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_isInitialized) return;

        lock (_initLock)
        {
            if (_isInitialized) return;

            try
            {
                string root = RootDir;

                // 1. 安全创建层级目录（exe 同级 BaseDir）；游戏目录由用户手动选择，不在此创建
                CreateDirectorySafe(BaseDir);
                CreateDirectorySafe(root);
                CreateDirectorySafe(PluginsDir);
                CreateDirectorySafe(PresetsDir);
                CreateDirectorySafe(ChioriPluginDir);
                CreateDirectorySafe(FpsPluginDir);
                CreateDirectorySafe(AssetsDir);

                // 2. 防呆数据平滑迁移（仅当源与目标为不同物理路径时）
                string localAppBase = NormalizePath(AppContext.BaseDirectory);
                string currentRoot = NormalizePath(root);

                if (!string.Equals(localAppBase, currentRoot, StringComparison.OrdinalIgnoreCase) &&
                    !currentRoot.StartsWith(localAppBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    // 迁移配置文件（若目标不存在或目标为0字节损坏文件）
                    string localConfig = Path.Combine(AppContext.BaseDirectory, "qz_launcher_config.json");
                    if (File.Exists(localConfig))
                    {
                        bool needCopy = !File.Exists(ConfigPath) || (new FileInfo(ConfigPath).Length == 0);
                        if (needCopy)
                        {
                            try { File.Copy(localConfig, ConfigPath, true); } catch { }
                        }
                    }

                    // 迁移已有的 Plugins 插件目录
                    string localPlugins = Path.Combine(AppContext.BaseDirectory, "Plugins");
                    if (Directory.Exists(localPlugins))
                    {
                        CopyDirectorySafeWithGuards(localPlugins, PluginsDir);
                    }
                }

                // 3. 部署千织专属核心 DLL 与全功能插件 (0 外部依赖，严格限定在用户文档空间)
                try
                {
                    string chioriIniPath = Path.Combine(ChioriPluginDir, "config.ini");
                    string fpsIniPath = Path.Combine(FpsPluginDir, "config.ini");

                    // 1. 部署核心组件（仅限文档目录，绝不污染桌面）
                    ExtractEmbeddedResource("Launcher.dll", Path.Combine(root, "Launcher.dll"), true);
                    ExtractEmbeddedResource("ChioriPlugin.dll", Path.Combine(ChioriPluginDir, "ChioriPlugin.dll"), true);
                    ExtractEmbeddedResource("FPS.dll", Path.Combine(FpsPluginDir, "FPS.disabled"), true);

                    // 2. 悬浮窗配置自愈：若不存在或包含 Fufu / 包含非全关脏数据，强制覆盖为纯净全关
                    bool needResetFpsIni = !File.Exists(fpsIniPath);
                    if (File.Exists(fpsIniPath))
                    {
                        try
                        {
                            string content = File.ReadAllText(fpsIniPath);
                            if (content.Contains("Fufu", StringComparison.OrdinalIgnoreCase) || 
                                content.Contains("Value = 1"))
                            {
                                needResetFpsIni = true;
                            }
                        }
                        catch { needResetFpsIni = true; }
                    }
                    if (needResetFpsIni)
                    {
                        ExtractEmbeddedResource("DefaultFpsConfig.ini", fpsIniPath, true);
                    }

                    // 3. 千织配置自愈：若不存在或包含旧版 Fufu 残留，强制覆盖为纯净全关
                    bool needResetChioriIni = !File.Exists(chioriIniPath);
                    if (File.Exists(chioriIniPath))
                    {
                        try
                        {
                            string content = File.ReadAllText(chioriIniPath);
                            if (content.Contains("Fufu", StringComparison.OrdinalIgnoreCase))
                            {
                                needResetChioriIni = true;
                            }
                        }
                        catch { needResetChioriIni = true; }
                    }
                    if (needResetChioriIni)
                    {
                        ExtractEmbeddedResource("DefaultChioriConfig.ini", chioriIniPath, true);
                    }

                    // 4. 彻底去 Fufu 化：清除遗留的 Fufu DLL
                    string legacyFufuDll = Path.Combine(ChioriPluginDir, "FufuLauncher.UnlockerIsland.dll");
                    if (File.Exists(legacyFufuDll))
                    {
                        try { File.Delete(legacyFufuDll); } catch { }
                    }

                    // 5. 清理旧版本含有 Fufu 或开启项的脏预设文件
                    if (Directory.Exists(PresetsDir))
                    {
                        var oldPresets = Directory.GetFiles(PresetsDir, "*.json");
                        foreach (var op in oldPresets)
                        {
                            try
                            {
                                string text = File.ReadAllText(op);
                                if (text.Contains("Fufu", StringComparison.OrdinalIgnoreCase) || text.Contains("\"Value\": \"1\""))
                                {
                                    File.Delete(op);
                                }
                            }
                            catch { }
                        }
                    }

                    // 6. 悬浮窗默认全关核心保障：若本地存在历史 FPS.dll，强制删除（仅保留 FPS.disabled）
                    string legacyFpsDll = Path.Combine(FpsPluginDir, "FPS.dll");
                    if (File.Exists(legacyFpsDll))
                    {
                        try { File.Delete(legacyFpsDll); } catch { }
                    }
                }
                catch { }

                _isInitialized = true;
            }
            catch
            {
                // 最终保底：即使初始化某个边缘环节受阻，也不阻断程序启动
                _isInitialized = true;
            }
        }
    }

    /// <summary>
    /// 检查性能悬浮窗 HUD (FPS.dll) 当前是否处于激活加载状态
    /// </summary>
    public static bool IsFpsPluginEnabled
    {
        get
        {
            string fpsDll = Path.Combine(FpsPluginDir, "FPS.dll");
            return File.Exists(fpsDll);
        }
    }

    /// <summary>
    /// 动态开启或关闭性能悬浮窗 HUD (通过重命名 FPS.dll 和 FPS.disabled 实现无缝切换)
    /// </summary>
    public static void SetFpsPluginEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(FpsPluginDir);
            string fpsDll = Path.Combine(FpsPluginDir, "FPS.dll");
            string fpsDisabled = Path.Combine(FpsPluginDir, "FPS.disabled");

            if (enabled)
            {
                if (File.Exists(fpsDisabled))
                {
                    if (File.Exists(fpsDll)) File.Delete(fpsDll);
                    File.Move(fpsDisabled, fpsDll);
                }
                else if (!File.Exists(fpsDll))
                {
                    ExtractEmbeddedResource("FPS.dll", fpsDll, true);
                }
            }
            else
            {
                if (File.Exists(fpsDll))
                {
                    if (File.Exists(fpsDisabled)) File.Delete(fpsDisabled);
                    File.Move(fpsDll, fpsDisabled);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 从嵌入式程序集资源中安全释放文件（若已存在且大小正常则跳过）
    /// </summary>
    public static void ExtractEmbeddedResource(string resourceLogicalName, string targetFilePath, bool overwrite = false)
    {
        try
        {
            if (File.Exists(targetFilePath) && !overwrite)
            {
                var fi = new FileInfo(targetFilePath);
                if (fi.Length > 0) return;
            }

            var asm = typeof(ChioriWorkspace).Assembly;
            using var stream = asm.GetManifestResourceStream(resourceLogicalName) 
                            ?? asm.GetManifestResourceStream(asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(resourceLogicalName, StringComparison.OrdinalIgnoreCase)) ?? "");
            if (stream == null) return;

            string? dir = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(fs);
        }
        catch { }
    }

    /// <summary>
    /// 防呆决策：确定启动器包根目录 BaseDir（绿色免安装，随 exe 分发）
    /// 优先级：1. exe 同级目录 ➔ 2. LocalAppData ➔ 3. 临时目录
    /// </summary>
    private static string DetermineSafeBaseDir()
    {
        // 候选路径 1: 启动器 exe 同级目录（绿色免安装，整个文件夹可拷贝分发）
        try
        {
            string exeDir = GetExeDirectory();
            if (!string.IsNullOrWhiteSpace(exeDir) && Directory.Exists(exeDir))
            {
                if (TestDirectoryWritable(exeDir))
                {
                    return exeDir;
                }
            }
        }
        catch { }

        // 候选路径 2: LocalAppData (exe 目录只读时的回退，例如被放进 Program Files)
        try
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localApp) && Directory.Exists(localApp))
            {
                string testPath = Path.Combine(localApp, "ChioriLauncher");
                if (TestDirectoryWritable(testPath))
                {
                    return testPath;
                }
            }
        }
        catch { }

        // 候选路径 3: 兜底使用临时目录
        return Path.Combine(Path.GetTempPath(), "ChioriLauncher");
    }

    /// <summary>
    /// 获取启动器 exe 真实所在目录（兼容单文件发布：单文件下 AppContext.BaseDirectory 亦为 exe 目录）
    /// </summary>
    private static string GetExeDirectory()
    {
        try
        {
            string? procPath = Environment.ProcessPath;
            string? dir = string.IsNullOrEmpty(procPath) ? null : Path.GetDirectoryName(procPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }
        catch { }
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 防呆探测：真实测试目录是否具备读写权限
    /// </summary>
    private static bool TestDirectoryWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            string testFile = Path.Combine(path, $".probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "probe");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CreateDirectorySafe(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }
        catch { }
    }

    /// <summary>
    /// 目录复制防呆卫士：
    /// 1. 拦截同源复制；2. 拦截父目录向子目录复制的死循环；3. 拦截损坏 0 字节文件并覆盖修复
    /// </summary>
    private static void CopyDirectorySafeWithGuards(string source, string target)
    {
        string normSource = NormalizePath(source);
        string normTarget = NormalizePath(target);

        // 防呆拦截：源和目标相同，或目标包含在源目录内
        if (string.Equals(normSource, normTarget, StringComparison.OrdinalIgnoreCase)) return;
        if (normTarget.StartsWith(normSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

        CreateDirectorySafe(target);

        try
        {
            foreach (var file in Directory.GetFiles(source))
            {
                try
                {
                    string fileName = Path.GetFileName(file);
                    string dest = Path.Combine(target, fileName);

                    // 若目标不存在，或目标文件为 0 字节（可能损坏），才予以复制写入
                    if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
                    {
                        File.Copy(file, dest, true);
                    }
                }
                catch { }
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                try
                {
                    string subName = Path.GetFileName(dir);
                    CopyDirectorySafeWithGuards(dir, Path.Combine(target, subName));
                }
                catch { }
            }
        }
        catch { }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }
}
