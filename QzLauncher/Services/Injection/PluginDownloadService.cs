using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace QzLauncher.Services.Injection;

public class PluginDownloadService
{
    private const string PrimaryUrl = "http://kr2-proxy.gitwarp.top:9980/https://github.com/CodeCubist/FufuLauncher--Plugins/blob/main/FuFuPlugin.zip";
    private const string FallbackUrl = "https://github.com/CodeCubist/FufuLauncher--Plugins/blob/main/FuFuPlugin.zip?raw=true";

    public event Action<double, string>? ProgressChanged;

    /// <summary>
    /// 下载并安装/修复千织专属增强插件 (ChioriPlugin)
    /// </summary>
    public async Task<(bool success, string message)> DownloadAndInstallPluginAsync(CancellationToken token = default)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"ChioriPlugin_{Guid.NewGuid():N}.zip");
        string extractDir = Path.Combine(Path.GetTempPath(), $"ChioriPlugin_Extract_{Guid.NewGuid():N}");
        string targetDir = ChioriWorkspace.ChioriPluginDir;

        try
        {
            ProgressChanged?.Invoke(0, "正在连接插件下载服务器...");

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            HttpResponseMessage? response = null;
            bool usedFallback = false;

            try
            {
                response = await client.GetAsync(PrimaryUrl, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"主线路返回状态码: {response.StatusCode}");
            }
            catch
            {
                ProgressChanged?.Invoke(5, "主线路连接超时，正在切换至全球备用线路...");
                usedFallback = true;
                response = await client.GetAsync(FallbackUrl, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"备用下载线路失败: HTTP {response.StatusCode}");
                }
            }

            using (response)
            {
                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                long totalRead = 0L;
                byte[] buffer = new byte[16384];

                using var stream = await response.Content.ReadAsStreamAsync(token);
                using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 16384, true);

                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), token);
                    totalRead += read;

                    if (totalBytes > 0)
                    {
                        double pct = Math.Round((double)totalRead / totalBytes * 100, 1);
                        string lineDesc = usedFallback ? "全球线路" : "极速线路";
                        ProgressChanged?.Invoke(pct, $"正在下载插件包 ({lineDesc}): {pct:F1}% ({(totalRead / 1048576.0):F2}MB / {(totalBytes / 1048576.0):F2}MB)");
                    }
                }
            }

            ProgressChanged?.Invoke(100, "正在解压并校验插件包...");

            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            Directory.CreateDirectory(extractDir);

            await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, extractDir), token);

            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            // 检查解压出的顶层文件夹（若包内有一层 FuFuPlugin/ 或类似目录，自动下钻）
            var subDirs = Directory.GetDirectories(extractDir);
            string sourceToCopy = (subDirs.Length == 1 && Directory.GetFiles(extractDir).Length == 0) ? subDirs[0] : extractDir;

            CopyDirectorySafe(sourceToCopy, targetDir);

            ProgressChanged?.Invoke(100, "千织专属增强插件安装完成！");
            return (true, "千织专属增强插件 (ChioriPlugin) 已成功下载并就绪！");
        }
        catch (OperationCanceledException)
        {
            return (false, "下载已由用户取消。");
        }
        catch (Exception ex)
        {
            return (false, $"下载安装失败: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
        }
    }

    /// <summary>
    /// 修复 FPS 帧率解锁插件
    /// </summary>
    public static (bool success, string message) RepairFpsPlugin()
    {
        try
        {
            string fpsDir = ChioriWorkspace.FpsPluginDir;
            if (!Directory.Exists(fpsDir)) Directory.CreateDirectory(fpsDir);

            string zipPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Launcher", "FPS.zip");
            if (File.Exists(zipPath))
            {
                ZipFile.ExtractToDirectory(zipPath, fpsDir, overwriteFiles: true);
                return (true, "FPS 插件已从内置安装包成功修复并解压！");
            }

            string disabledPath = Path.Combine(fpsDir, "FPS.disabled");
            string targetDll = Path.Combine(fpsDir, "FPS.dll");
            if (File.Exists(disabledPath) && !File.Exists(targetDll))
            {
                File.Copy(disabledPath, targetDll, true);
                return (true, "FPS 插件已成功恢复为启用状态！");
            }

            return (true, "FPS 插件已处于就绪状态。");
        }
        catch (Exception ex)
        {
            return (false, $"修复 FPS 插件失败: {ex.Message}");
        }
    }

    private static void CopyDirectorySafe(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string fileName = Path.GetFileName(file);
            string dest = Path.Combine(targetDir, fileName);

            // 如果是 config.ini 且目标已存在，保护用户的本地配置不被冲刷
            if (string.Equals(fileName, "config.ini", StringComparison.OrdinalIgnoreCase) && File.Exists(dest))
            {
                continue;
            }

            File.Copy(file, dest, true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectorySafe(dir, dest);
        }
    }
}
