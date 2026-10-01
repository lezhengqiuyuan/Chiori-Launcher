using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using FufuLauncher.Constants;
using FufuLauncher.Models.GameServer;
using FufuLauncher.Services.GameServer;

namespace QzLauncher.Services;

public enum GameEnvironmentState
{
    InvalidDirectory,
    MissingExe,
    IsChinaClient,   // 国服，仅支持国际服，需弹窗提示转换
    IsOverseaClient  // 国际服，支持补丁替换并启动
}

/// <summary>
/// Astrolabe.dll 补丁替换机制（仅支持国际服 GenshinImpact_Data）：
/// - 首次启动：将当前任意 Astrolabe.dll 备份为 Astrolabe_original_backup.dll，替换为千织专用补丁；
/// - 后续启动：通过 SHA-256 检测当前文件是否已为千织补丁，是则直接启动，否则再替换；
/// - 游戏退出 / 启动器退出：补丁保持常驻，不还原，下次可直接启动。
/// </summary>
public static class PatchService
{
    private const string PatchResource = "QzLauncher.Assets.patch_astrolabe.dll";
    private const string OsDataDir = GameConstants.OS_DATA_DIR; // "GenshinImpact_Data"
    private const string CnDataDir = GameConstants.CN_DATA_DIR; // "YuanShen_Data"

    public static string PluginsDir(string gameDir) => Path.Combine(gameDir, OsDataDir, "Plugins");
    public static string TargetDll(string gameDir)  => Path.Combine(PluginsDir(gameDir), "Astrolabe.dll");

    /// <summary>原始文件备份（首次替换时保存，后续不覆盖）</summary>
    public static string BackupDll(string gameDir)  => Path.Combine(PluginsDir(gameDir), "Astrolabe.dll.bak");

    // ──────────────────────────────────────────────
    // 环境检测
    // ──────────────────────────────────────────────

    /// <summary>检测游戏客户端环境状态</summary>
    public static GameEnvironmentState CheckEnvironment(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return GameEnvironmentState.InvalidDirectory;

        // 1. 优先读取 config.ini 中的 sub_channel
        string iniPath = Path.Combine(gameDir, "config.ini");
        if (File.Exists(iniPath))
        {
            try
            {
                foreach (var line in File.ReadAllLines(iniPath))
                {
                    var t = line.Trim();
                    if (t.StartsWith("sub_channel", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = t.Split('=');
                        if (parts.Length == 2)
                        {
                            var sc = parts[1].Trim();
                            if (sc == "1") return GameEnvironmentState.IsChinaClient;
                            if (sc == "0") return GameEnvironmentState.IsOverseaClient;
                        }
                    }
                }
            }
            catch { }
        }

        // 2. 根据可执行程序判断
        bool hasCnExe = File.Exists(Path.Combine(gameDir, GameConstants.CN_EXE));
        bool hasOsExe = File.Exists(Path.Combine(gameDir, GameConstants.OS_EXE));
        if (hasCnExe && !hasOsExe) return GameEnvironmentState.IsChinaClient;
        if (hasOsExe && !hasCnExe) return GameEnvironmentState.IsOverseaClient;

        // 3. 根据数据目录判断
        bool hasCnData = Directory.Exists(Path.Combine(gameDir, CnDataDir));
        bool hasOsData = Directory.Exists(Path.Combine(gameDir, OsDataDir));
        if (hasCnData && !hasOsData) return GameEnvironmentState.IsChinaClient;
        if (hasOsData && !hasCnData) return GameEnvironmentState.IsOverseaClient;

        if (hasOsData || hasOsExe) return GameEnvironmentState.IsOverseaClient;
        if (hasCnData || hasCnExe) return GameEnvironmentState.IsChinaClient;

        return GameEnvironmentState.InvalidDirectory;
    }

    /// <summary>获取当前生效的服务器方案（国服 / 国际服）</summary>
    public static GameServerScheme DetectCurrentScheme(string gameDir)
    {
        var configService = new GameServerConfigurationService();
        var detected = configService.TryDetectCurrentScheme(gameDir);
        if (detected is not null) return detected;

        var state = CheckEnvironment(gameDir);
        return state == GameEnvironmentState.IsOverseaClient
            ? GameServerScheme.OverseaOfficialDefault
            : GameServerScheme.ChineseOfficialOfficial;
    }

    // ──────────────────────────────────────────────
    // 补丁替换（幂等，常驻）
    // ──────────────────────────────────────────────

    /// <summary>
    /// 启动游戏前调用。
    /// - 若当前 Astrolabe.dll 已是千织专用补丁（SHA-256 一致）则幂等跳过；
    /// - 否则：备份原有文件（首次备份不覆盖），替换为千织补丁。
    /// - 补丁常驻：游戏退出 / 启动器退出均不还原。
    /// </summary>
    public static bool ApplyPatch(string gameDir)
    {
        try
        {
            string pluginsDir = PluginsDir(gameDir);
            Directory.CreateDirectory(pluginsDir);

            string target     = TargetDll(gameDir);
            string backup     = BackupDll(gameDir);
            string patchSrc   = ResolvePatchDll();

            // 已是千织补丁则跳过替换
            if (File.Exists(target) && IsSameFile(target, patchSrc))
            {
                Logger.Info("[补丁] 当前 Astrolabe.dll 已是千织专用版，无需重复替换，直接启动。");
            }
            else
            {
                // 首次备份（确保保留权威国际服官方原版）
                if (!File.Exists(backup))
                {
                    try
                    {
                        string? officialOs = ResolveOfficialOverseaDll();
                        if (officialOs != null && File.Exists(officialOs))
                        {
                            File.Copy(officialOs, backup, overwrite: true);
                            Logger.Info($"[补丁] 已使用权威国际服官方原版 Astrolabe.7.0.guoji.dll 创建备份 → {backup}");
                        }
                        else if (File.Exists(target) && !IsSameFile(target, patchSrc))
                        {
                            File.Copy(target, backup, overwrite: false);
                            Logger.Info($"[补丁] 已备份原有 Astrolabe.dll → {backup}");
                        }
                    }
                    catch
                    {
                        if (File.Exists(target) && !IsSameFile(target, patchSrc))
                        {
                            File.Copy(target, backup, overwrite: false);
                        }
                    }
                }

                File.Copy(patchSrc, target, overwrite: true);
                Logger.Info($"[补丁] 已替换千织 Astrolabe.dll → {target}");
            }

            // 自动清理高版本热更新残留以防 7.0 引擎启动白屏
            CleanPersistentCacheIfNeeded(gameDir);

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[补丁] 应用补丁失败: {ex.Message}", ex);
            return false;
        }
    }

    // ──────────────────────────────────────────────
    // 辅助方法
    // ──────────────────────────────────────────────

    /// <summary>
    /// 检测并清理高版本（如 7.1）热更新残留目录（Persistent），彻底解决 7.1 覆盖 7.0 导致的启动白屏死锁
    /// </summary>
    public static void CleanPersistentCacheIfNeeded(string gameDir)
    {
        try
        {
            string[] dataDirNames = ["GenshinImpact_Data", "YuanShen_Data"];
            foreach (var name in dataDirNames)
            {
                string dataDir = Path.Combine(gameDir, name);
                if (!Directory.Exists(dataDir)) continue;

                string pDir = Path.Combine(dataDir, "Persistent");
                if (!Directory.Exists(pDir)) continue;

                string svFile = Path.Combine(pDir, "ScriptVersion");
                bool shouldClean = false;
                if (File.Exists(svFile))
                {
                    string sv = File.ReadAllText(svFile).Trim();
                    if (!sv.StartsWith("7.0"))
                    {
                        shouldClean = true;
                        Logger.Info($"[环境自愈] 发现高版本热更残留 (ScriptVersion: {sv})，正在清理以防 7.0 引擎启动白屏！");
                    }
                }
                else
                {
                    string ctable = Path.Combine(pDir, "ctable.dat");
                    if (File.Exists(ctable))
                    {
                        shouldClean = true;
                    }
                }

                if (shouldClean)
                {
                    string bakDir = Path.Combine(dataDir, $"Persistent_bak_{DateTime.Now:yyyyMMddHHmmss}");
                    try
                    {
                        Directory.Move(pDir, bakDir);
                        Logger.Info($"[环境自愈] 已将冲突的热更新缓存备份移至: {bakDir}");
                    }
                    catch
                    {
                        Directory.Delete(pDir, true);
                        Logger.Info("[环境自愈] 已直接清除冲突的热更新缓存目录 Persistent");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[环境自愈] 检查/清理 Persistent 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取国际服 7.0 官方 Astrolabe.dll 路径（优先级：外置文件 -> 内嵌资源）
    /// </summary>
    public static string? ResolveOfficialOverseaDll()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Astrolabe.7.0.guoji.dll"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Astrolabe.7.0.guoji.dll"),
        ];

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        try
        {
            return ExtractResourceToTemp("Astrolabe.7.0.guoji.dll", "Astrolabe.7.0.guoji.dll");
        }
        catch { return null; }
    }

    /// <summary>
    /// 查找千织补丁来源（按优先级）：
    ///   1. 启动器内置千织补丁（内嵌程序集资源，QzLauncher.Assets.patch_astrolabe.dll，优先级最高）
    ///   2. 程序同级目录外置补丁文件兜底
    /// </summary>
    private static string ResolvePatchDll()
    {
        // 1. 优先级最高：启动器内置千织补丁（内嵌程序集资源）
        try
        {
            string embedded = ExtractResourceToTemp(PatchResource, "chiori_astrolabe_patch.dll");
            if (File.Exists(embedded))
            {
                Logger.Info($"[补丁] 【优先级最高】使用启动器内置千织补丁资源: {PatchResource}");
                return embedded;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[补丁] 释放内置补丁资源异常: {ex.Message}，尝试搜索外部备选文件...");
        }

        // 2. 兜底：程序同级目录外置补丁文件
        string[] fallbackCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Astrolabe.dll"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "patch_astrolabe.dll"),
        ];

        foreach (var c in fallbackCandidates)
        {
            if (File.Exists(c))
            {
                Logger.Info($"[补丁] 【备用路径】使用外部补丁文件: {c}");
                return c;
            }
        }

        throw new FileNotFoundException("未找到可用的千织 Astrolabe.dll 补丁！请确认启动器内置资源完好。");
    }

    /// <summary>用 SHA-256 比对两个文件内容是否完全一致</summary>
    private static bool IsSameFile(string a, string b)
    {
        try { return ComputeHash(a) == ComputeHash(b); }
        catch { return false; }
    }

    private static string ComputeHash(string path)
    {
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static string ExtractResourceToTemp(string resourceName, string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var match = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Equals(resourceName, StringComparison.OrdinalIgnoreCase) || n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));
        using var stream = (match != null ? assembly.GetManifestResourceStream(match) : null)
            ?? throw new FileNotFoundException($"内嵌资源未找到: {resourceName}");
        string tmp = Path.Combine(Path.GetTempPath(), fileName);
        using var fs = File.Create(tmp);
        stream.CopyTo(fs);
        return tmp;
    }
}
