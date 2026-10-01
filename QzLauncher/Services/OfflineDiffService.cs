using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FufuLauncher.Constants;
using FufuLauncher.Helpers;
using FufuLauncher.Models.GameServer;

namespace QzLauncher.Services;

/// <summary>
/// 极简镜像对称转服服务：
/// 恪守 KISS 原则与完全镜像对称：
/// 国服 ➔ 国际服：YuanShen_Data 改名为 GenshinImpact_Data，主程序 YuanShen.exe 备份为 .cn_bak，覆写国际服差异文件，写国际服配置
/// 国际服 ➔ 国服：不再从本地备份恢复，统一交由 SophonService 走官方 CDN 差分下载合成
/// 彻底废除任何 mklink、软链接与 Directory.Delete 危险代码！
/// </summary>
public static class OfflineDiffService
{
    private const string DiffResourceName = "OverseaDiff.zip";

    /// <summary>
    /// 安全移动重命名目录，带防占用 GC 回收与重试机制，绝不破坏目录内容
    /// </summary>
    public static void SafeMoveDirectory(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        if (Directory.Exists(targetDir)) return;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                Directory.Move(sourceDir, targetDir);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i == 4) throw;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(120);
            }
        }
    }

    /// <summary>
    /// 安全重命名文件
    /// </summary>
    public static void SafeMoveFile(string sourceFile, string targetFile)
    {
        if (!File.Exists(sourceFile)) return;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(targetFile))
                {
                    File.Delete(targetFile);
                }
                File.Move(sourceFile, targetFile);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i == 4) throw;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// 将 sourceDir 的内容合并进 targetDir（同卷为瞬间移动）：
    /// 目标已存在的同名文件保留目标版本（新合成数据优先），其余文件与子目录整体搬迁；
    /// 完成后若 sourceDir 已空则删除。用于数据目录被意外劈开后的自愈合并。
    /// </summary>
    public static void MergeDirectoryInto(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destination = Path.Combine(targetDir, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                // 目标目录版本为准，丢弃源目录中的同名旧文件
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { }
                continue;
            }

            SafeMoveFile(file, destination);
        }

        foreach (string directory in Directory.GetDirectories(sourceDir))
        {
            MergeDirectoryInto(directory, Path.Combine(targetDir, Path.GetFileName(directory)));
        }

        try
        {
            if (Directory.GetFileSystemEntries(sourceDir).Length == 0)
            {
                Directory.Delete(sourceDir, false);
            }
        }
        catch { }
    }

    /// <summary>
    /// 真实 exe 物理目录（单文件自解压下 AppContext.BaseDirectory 指向临时解压目录，需用 Environment.ProcessPath 定位 exe 同级资源）
    /// </summary>
    private static string RealExeDir
    {
        get
        {
            try
            {
                string? proc = Environment.ProcessPath;
                string? dir = string.IsNullOrEmpty(proc) ? null : Path.GetDirectoryName(proc);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
            catch { }
            return AppContext.BaseDirectory;
        }
    }

    /// <summary>
    /// 检查启动器是否具备离线转换资源包（exe 同级外置文件或旧版内嵌资源任一即可）
    /// </summary>
    public static bool HasEmbeddedPackage()
    {
        if (ResolvePackageDir() != null) return true;
        if (ResolvePackageFile() != null) return true;
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames().Any(n => n.EndsWith(DiffResourceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 解析离线转换资源包 OverseaDiff.zip 的物理路径（exe 同级目录优先）
    /// </summary>
    public static string? ResolvePackageFile()
    {
        string[] candidates =
        [
            Path.Combine(RealExeDir, "OverseaDiff.zip"),
            Path.Combine(RealExeDir, "Assets", "OverseaDiff.zip"),
            Path.Combine(AppContext.BaseDirectory, "OverseaDiff.zip"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "OverseaDiff.zip"),
        ];
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>
    /// 打开转换资源包压缩流：优先磁盘外置文件，其次旧版内嵌资源
    /// </summary>
    private static ZipArchive OpenPackageArchive(Assembly asm, string? pkgFile, string? resName)
    {
        if (pkgFile != null)
            return new ZipArchive(new FileStream(pkgFile, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
        var stream = asm.GetManifestResourceStream(resName ?? "") ?? throw new InvalidOperationException("无法打开内置资源流！");
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    /// <summary>
    /// 解析已解压的 OverseaDiff 文件夹（exe 同级目录优先），存在且非空则返回其路径
    /// </summary>
    public static string? ResolvePackageDir()
    {
        string[] candidates =
        [
            Path.Combine(RealExeDir, "OverseaDiff"),
            Path.Combine(AppContext.BaseDirectory, "OverseaDiff"),
        ];
        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "*", SearchOption.AllDirectories).Any())
                return c;
        }
        return null;
    }

    /// <summary>
    /// 将单个资源包文件部署到游戏目录：先建目录、备份被覆盖的国服原文件，再流式拷贝覆盖
    /// </summary>
    private static async Task DeployPackageFileAsync(string relPath, string gameDir, string backupDir, Func<Stream> sourceFactory, CancellationToken token)
    {
        string targetFile = Path.Combine(gameDir, relPath);
        string? targetDirectory = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        // 备份被替换的国服原文件，方便一键极速还原
        if (File.Exists(targetFile))
        {
            string bakFile = Path.Combine(backupDir, relPath);
            string? bakDir = Path.GetDirectoryName(bakFile);
            if (!string.IsNullOrEmpty(bakDir) && !Directory.Exists(bakDir))
                Directory.CreateDirectory(bakDir);
            if (!File.Exists(bakFile))
            {
                try { File.Copy(targetFile, bakFile, true); } catch { }
            }
        }

        using var source = sourceFactory();
        using var targetStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
        await source.CopyToAsync(targetStream, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 使用内置资源包执行极速离线转换（国服 ➔ 国际服）
    /// </summary>
    public static async Task ConvertToOverseaOfflineAsync(
        string gameDir,
        IProgress<GameServerConversionProgress> progress,
        Action<string> log,
        CancellationToken token = default)
    {
        var asm = Assembly.GetExecutingAssembly();
        string? pkgDir = ResolvePackageDir();
        string? pkgFile = pkgDir == null ? ResolvePackageFile() : null;
        string? resName = (pkgDir == null && pkgFile == null)
            ? asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(DiffResourceName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (pkgDir == null && pkgFile == null && resName == null)
            throw new InvalidOperationException("未找到国际服转换资源包（请确认 OverseaDiff 文件夹或 OverseaDiff.zip 位于启动器同级目录）。");

        log("正在准备切换为国际服...");

        // 1. 完全对称镜像：数据文件夹安全改名为 GenshinImpact_Data
        string cnDataDir = Path.Combine(gameDir, "YuanShen_Data");
        string osDataDir = Path.Combine(gameDir, "GenshinImpact_Data");
        if (Directory.Exists(cnDataDir) && !Directory.Exists(osDataDir))
        {
            log("正在切换数据目录：YuanShen_Data ➔ GenshinImpact_Data...");
            SafeMoveDirectory(cnDataDir, osDataDir);
        }

        // 清理高版本残留的 Persistent 目录，防止 7.1 热更残留导致 7.0 引擎启动白屏
        PatchService.CleanPersistentCacheIfNeeded(gameDir);

        // 2. 主程序隔离：YuanShen.exe 改名为 YuanShen.exe.cn_bak
        string cnExe = Path.Combine(gameDir, "YuanShen.exe");
        string cnExeBak = Path.Combine(gameDir, "YuanShen.exe.cn_bak");
        if (File.Exists(cnExe))
        {
            SafeMoveFile(cnExe, cnExeBak);
        }

        // 若本地已有备份的 GenshinImpact.exe.os_bak，改回为 GenshinImpact.exe
        string osExe = Path.Combine(gameDir, "GenshinImpact.exe");
        string osExeBak = Path.Combine(gameDir, "GenshinImpact.exe.os_bak");
        if (!File.Exists(osExe) && File.Exists(osExeBak))
        {
            SafeMoveFile(osExeBak, osExe);
        }

        // 3. 从资源包（OverseaDiff 文件夹 / zip / 内嵌）拷贝并覆盖国际服独占引擎文件
        string backupDir = Path.Combine(AppPaths.ServerCacheDir, "Chinese_Backup");
        Directory.CreateDirectory(backupDir);
        string stage = "正在释放国际服官方核心组件...";

        if (pkgDir != null)
        {
            log("正在从 OverseaDiff 文件夹拷贝国际服专属引擎组件...");
            var files = Directory.GetFiles(pkgDir, "*", SearchOption.AllDirectories);
            int totalFiles = files.Length;
            long totalBytes = files.Sum(f => new FileInfo(f).Length);
            long doneBytes = 0;
            int currentFileIndex = 0;

            foreach (var f in files)
            {
                token.ThrowIfCancellationRequested();
                currentFileIndex++;
                if (Path.GetFileName(f).Equals("config_oversea.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                string src = f;
                await DeployPackageFileAsync(Path.GetRelativePath(pkgDir, f).Replace('\\', '/'), gameDir, backupDir, () => new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read), token).ConfigureAwait(false);
                doneBytes += new FileInfo(f).Length;
                progress.Report(new GameServerConversionProgress(stage, totalFiles, currentFileIndex, totalBytes, doneBytes, Path.GetFileName(f)));
            }
        }
        else
        {
            log(pkgFile != null ? "正在从启动器同级目录解压释放国际服专属引擎组件..." : "正在从启动器内置资源解压释放国际服专属引擎组件...");
            using var archive = OpenPackageArchive(asm, pkgFile, resName);

            var validEntries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            int totalFiles = validEntries.Count;
            long totalBytes = validEntries.Sum(e => e.Length);
            long doneBytes = 0;
            int currentFileIndex = 0;

            foreach (var entry in validEntries)
            {
                token.ThrowIfCancellationRequested();

                if (entry.FullName.Equals("config_oversea.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var e = entry;
                await DeployPackageFileAsync(e.FullName, gameDir, backupDir, () => e.Open(), token).ConfigureAwait(false);
                currentFileIndex++;
                doneBytes += e.Length;
                progress.Report(new GameServerConversionProgress(stage, totalFiles, currentFileIndex, totalBytes, doneBytes, e.Name));
            }
        }

        // 4. 写入国际服 config.ini
        log("正在配置国际服渠道识别 (sub_channel=0)...");
        string configPath = Path.Combine(gameDir, "config.ini");
        string overseaIniContent = "[General]\r\ngame_version=7.0.0\r\nchannel=1\r\nsub_channel=0\r\ncps=mihoyo\r\nuapc={\"hk4e_cn\":{\"uapc\":\"\"},\"hyp\":{\"uapc\":\"\"}}\r\nwpf_version=7.0.0.47194594\r\n";
        File.WriteAllText(configPath, overseaIniContent);

        log("★ 离线转换完成！客户端已就绪为国际服，100% 离线免流量！");
    }
}
