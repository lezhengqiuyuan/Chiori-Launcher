using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FufuLauncher.Helpers;
using FufuLauncher.Models.GameServer;
using FufuLauncher.Services.GameServer;
using ProtoBuf;
using ZstdSharp;

namespace FufuLauncher.Services;

[ProtoContract]
public class Manifest
{
    [ProtoMember(1)]
    public List<FileEntry> Files { get; set; } = new List<FileEntry>();
}

[ProtoContract]
public class FileEntry
{
    [ProtoMember(1)]
    public string Path { get; set; } = string.Empty;
    [ProtoMember(2)]
    public List<Chunk> Chunks { get; set; } = new List<Chunk>();
    [ProtoMember(3)]
    public bool IsFolder { get; set; }
    [ProtoMember(4)]
    public long Size { get; set; }
    [ProtoMember(5)]
    public string Checksum { get; set; } = string.Empty;
}

[ProtoContract]
public class Chunk
{
    [ProtoMember(1)]
    public string Id { get; set; } = string.Empty;
    [ProtoMember(2)]
    public string Checksum { get; set; } = string.Empty;
    [ProtoMember(3)]
    public long Offset { get; set; }
    [ProtoMember(4)]
    public int CompressedSize { get; set; }
    [ProtoMember(5)]
    public int UncompressedSize { get; set; }
}

public class GenshinDownloader
{
    private readonly SophonBuildClient _sophonBuildClient;
    private readonly ChunkDownloader _chunkDownloader;
    private readonly GameServerScheme _scheme;
    private long _lastReportTicks = 0;

    public event Action<string>? Log;
    public event Action<long, long, int, int>? ProgressChanged;
    public event Action<string>? ErrorOccurred;

    public GenshinDownloader(SophonBuildClient sophonBuildClient, ChunkDownloader chunkDownloader, GameServerScheme scheme)
    {
        _sophonBuildClient = sophonBuildClient;
        _chunkDownloader = chunkDownloader;
        _scheme = scheme;
    }

    public async Task StartDownloadAsync(string installPath, string lang, bool downloadBaseGame, int maxThreads, CancellationToken token, GameServerDownloadMonitor? downloadMonitor = null, string targetTag = "7.0.0")
    {
        try
        {
            Log?.Invoke("Download_Connecting".GetLocalized());

            using JsonDocument buildDoc = await _sophonBuildClient.GetBuildDocumentAsync(_scheme, false, token, targetTag).ConfigureAwait(false);
            var dataProp = buildDoc.RootElement.GetProperty("data");
            var manifestsProp = dataProp.GetProperty("manifests");
            string versionTag = dataProp.TryGetProperty("tag", out var tagProp) ? tagProp.GetString()! : targetTag;

            var targetAssets = new List<string>();
            if (downloadBaseGame) targetAssets.Add("game");
            else Log?.Invoke("Download_VoiceOnly".GetLocalized());

            targetAssets.Add(lang);

            var filesToProcess = new ConcurrentBag<(FileEntry File, string UrlPrefix)>();

            foreach (var asset in targetAssets)
            {
                JsonElement config = default;
                foreach (var manifestElement in manifestsProp.EnumerateArray())
                {
                    if (manifestElement.GetProperty("matching_field").GetString() == asset)
                    {
                        config = manifestElement;
                        break;
                    }
                }

                if (config.ValueKind == JsonValueKind.Undefined) continue;

                string mId = config.GetProperty("manifest").GetProperty("id").GetString()!;
                string mChecksum = config.GetProperty("manifest").TryGetProperty("checksum", out var checksumProp) && checksumProp.ValueKind == JsonValueKind.String
                    ? checksumProp.GetString()!
                    : string.Empty;
                string mDownloadPrefix = config.GetProperty("manifest_download").GetProperty("url_prefix").GetString()!;
                string chunkDownloadPrefix = config.GetProperty("chunk_download").GetProperty("url_prefix").GetString()!;

                Log?.Invoke(string.Format("Download_FetchingManifest".GetLocalized(), asset));

                byte[] manifestBytes = await _sophonBuildClient.DownloadAndDecompressAsync($"{mDownloadPrefix}/{mId}", mChecksum, token).ConfigureAwait(false);

                using var ms = new MemoryStream(manifestBytes);
                var protoManifest = Serializer.Deserialize<Manifest>(ms);
                foreach (var f in protoManifest.Files) filesToProcess.Add((f, chunkDownloadPrefix));
            }

            int totalFiles = filesToProcess.Count;
            long totalBytes = filesToProcess.Sum(f => f.File.Size);

            // 第一阶段：多线程并行秒级扫描本地已有资源 (杜绝全量重复下载)
            Log?.Invoke("正在多线程快速扫描比对本地已有游戏资源...");

            var toDownload = new ConcurrentBag<(FileEntry File, string UrlPrefix)>();
            long alreadyHaveBytes = 0;
            int alreadyHaveCount = 0;

            var scanParallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = Math.Max(Environment.ProcessorCount, 8), 
                CancellationToken = token 
            };

            await Parallel.ForEachAsync(filesToProcess, scanParallelOptions, async (item, ct) =>
            {
                if (item.File.IsFolder) return;

                string relPath = item.File.Path.Replace('/', Path.DirectorySeparatorChar).TrimStart('\\', '/');
                string targetPath = Path.Combine(installPath, relPath);

                // 1. 检查目标位置是否已有且大小一致 (对核心场景/代码/配置等重点文件加验 MD5，杜绝白屏崩溃)
                if (File.Exists(targetPath) && new FileInfo(targetPath).Length == item.File.Size)
                {
                    bool match = true;
                    if (!string.IsNullOrEmpty(item.File.Checksum) && NeedsMd5Verification(relPath, item.File.Size))
                    {
                        try
                        {
                            string localMd5 = await ComputeFileMd5Async(targetPath, ct).ConfigureAwait(false);
                            if (!localMd5.Equals(item.File.Checksum, StringComparison.OrdinalIgnoreCase))
                            {
                                match = false;
                                try { File.Delete(targetPath); } catch { }
                            }
                        }
                        catch { match = false; }
                    }

                    if (match)
                    {
                        Interlocked.Add(ref alreadyHaveBytes, item.File.Size);
                        int count = Interlocked.Increment(ref alreadyHaveCount);
                        if (count % 50 == 0)
                        {
                            ReportProgress(Interlocked.Read(ref alreadyHaveBytes), totalBytes, count, totalFiles, force: true);
                        }
                        return;
                    }
                }

                // 2. 检查是否有旧 staging 遗留文件，顺手秒级就地搬正
                string stagingFile = Path.Combine(installPath, "staging", relPath);
                if (File.Exists(stagingFile) && new FileInfo(stagingFile).Length == item.File.Size)
                {
                    bool match = true;
                    if (!string.IsNullOrEmpty(item.File.Checksum) && NeedsMd5Verification(relPath, item.File.Size))
                    {
                        try
                        {
                            string localMd5 = await ComputeFileMd5Async(stagingFile, ct).ConfigureAwait(false);
                            if (!localMd5.Equals(item.File.Checksum, StringComparison.OrdinalIgnoreCase))
                            {
                                match = false;
                            }
                        }
                        catch { match = false; }
                    }

                    if (match)
                    {
                        string? sDir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(sDir) && !Directory.Exists(sDir)) Directory.CreateDirectory(sDir);
                        try
                        {
                            File.Move(stagingFile, targetPath, true);
                            Interlocked.Add(ref alreadyHaveBytes, item.File.Size);
                            int count = Interlocked.Increment(ref alreadyHaveCount);
                            if (count % 50 == 0)
                            {
                                ReportProgress(Interlocked.Read(ref alreadyHaveBytes), totalBytes, count, totalFiles, force: true);
                            }
                            return;
                        }
                        catch { }
                    }
                }

                // 3. 检查对端数据目录是否存在同名同大小资源 (国服/国际服互通资产复用)
                string altRelPath = string.Empty;
                if (relPath.StartsWith("YuanShen_Data", StringComparison.OrdinalIgnoreCase))
                {
                    altRelPath = "GenshinImpact_Data" + relPath.Substring("YuanShen_Data".Length);
                }
                else if (relPath.StartsWith("GenshinImpact_Data", StringComparison.OrdinalIgnoreCase))
                {
                    altRelPath = "YuanShen_Data" + relPath.Substring("GenshinImpact_Data".Length);
                }

                if (!string.IsNullOrEmpty(altRelPath))
                {
                    string altFullPath = Path.Combine(installPath, altRelPath);
                    if (File.Exists(altFullPath) && new FileInfo(altFullPath).Length == item.File.Size)
                    {
                        bool match = true;
                        if (!string.IsNullOrEmpty(item.File.Checksum) && NeedsMd5Verification(relPath, item.File.Size))
                        {
                            try
                            {
                                string localMd5 = await ComputeFileMd5Async(altFullPath, ct).ConfigureAwait(false);
                                if (!localMd5.Equals(item.File.Checksum, StringComparison.OrdinalIgnoreCase))
                                {
                                    match = false;
                                }
                            }
                            catch { match = false; }
                        }

                        if (match)
                        {
                            string? tDir = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(tDir) && !Directory.Exists(tDir)) Directory.CreateDirectory(tDir);
                            File.Copy(altFullPath, targetPath, true);
                            Interlocked.Add(ref alreadyHaveBytes, item.File.Size);
                            int count = Interlocked.Increment(ref alreadyHaveCount);
                            if (count % 50 == 0)
                            {
                                ReportProgress(Interlocked.Read(ref alreadyHaveBytes), totalBytes, count, totalFiles, force: true);
                            }
                            return;
                        }
                    }
                }

                // 缺失或损坏，加入下载列表
                toDownload.Add(item);
            });

            var downloadList = toDownload.ToList();

            // 立即向 UI 反映已有资源比对进度
            ReportProgress(alreadyHaveBytes, totalBytes, alreadyHaveCount, totalFiles, force: true);

            // 若全部已有，直接完成
            if (downloadList.Count == 0)
            {
                Log?.Invoke($"本地资源比对完成：共 {totalFiles} 个文件 ({FormatSize(totalBytes)}) 全部完好，无需重复下载！");
                ReportProgress(totalBytes, totalBytes, totalFiles, totalFiles, force: true);

                string gVer = Path.Combine(installPath, "gid_ver");
                string cIni = Path.Combine(installPath, "config.ini");
                await File.WriteAllTextAsync(gVer, versionTag, token);

                if (File.Exists(cIni))
                {
                    var ini = new IniFile(cIni);
                    ini.WriteValue("General", "game_version", versionTag);
                }
                else
                {
                    string cfg = $"[General]\ngame_version={versionTag}\nchannel={(int)_scheme.Channel}\nsub_channel={(int)_scheme.SubChannel}\ncps={_scheme.Cps}\n";
                    await File.WriteAllTextAsync(cIni, cfg, token);
                }

                Log?.Invoke("Download_AllDone".GetLocalized());
                return;
            }

            Log?.Invoke($"比对完成：本地已存在 {alreadyHaveCount}/{totalFiles} 个文件 ({FormatSize(alreadyHaveBytes)})，仅需下载剩余 {downloadList.Count} 个文件 ({FormatSize(totalBytes - alreadyHaveBytes)})");

            long processedBytes = alreadyHaveBytes;
            int processedFiles = alreadyHaveCount;
            var failedFiles = new ConcurrentBag<string>();

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = token };
            Action<long>? onBytesTransferred = downloadMonitor is null ? null : downloadMonitor.AddBytes;

            await Parallel.ForEachAsync(downloadList, parallelOptions, async (item, ct) =>
            {
                Action<int> onChunkWritten = (size) =>
                {
                    long current = Interlocked.Add(ref processedBytes, size);
                    ReportProgress(current, totalBytes, processedFiles, totalFiles);
                };

                bool success = await ProcessFileAsync(installPath, item.File, item.UrlPrefix, onChunkWritten, onBytesTransferred, ct);

                if (!success)
                {
                    failedFiles.Add(item.File.Path);
                    Log?.Invoke(string.Format("Download_FileUnfixable".GetLocalized(), item.File.Path));
                }

                Interlocked.Increment(ref processedFiles);
                ReportProgress(Interlocked.Read(ref processedBytes), totalBytes, processedFiles, totalFiles, force: true);
            });

            if (!failedFiles.IsEmpty)
            {
                throw new InvalidOperationException(string.Format("Download_FileFailed".GetLocalized(), failedFiles.Count));
            }

            string gidVerPath = Path.Combine(installPath, "gid_ver");
            string configPath = Path.Combine(installPath, "config.ini");

            await File.WriteAllTextAsync(gidVerPath, versionTag, token);

            if (File.Exists(configPath))
            {
                var iniFile = new IniFile(configPath);
                iniFile.WriteValue("General", "game_version", versionTag);
            }
            else
            {
                string configContent = $"[General]\ngame_version={versionTag}\nchannel={(int)_scheme.Channel}\nsub_channel={(int)_scheme.SubChannel}\ncps={_scheme.Cps}\n";
                await File.WriteAllTextAsync(configPath, configContent, token);
            }

            ReportProgress(totalBytes, totalBytes, totalFiles, totalFiles, force: true);
            Log?.Invoke("Download_AllDone".GetLocalized());
        }
        catch (OperationCanceledException) { Log?.Invoke("Download_UserCancelled".GetLocalized()); throw; }
        catch (Exception ex) { ErrorOccurred?.Invoke(ex.Message); throw; }
    }

    private async Task<bool> ProcessFileAsync(string installPath, FileEntry file, string urlPrefix, Action<int> onProgress, Action<long>? onBytesTransferred, CancellationToken token)
    {
        try
        {
            string rel = file.Path.Replace('/', Path.DirectorySeparatorChar).TrimStart('\\', '/');
            string targetPath = Path.Combine(installPath, rel);

            // 1. 优先检查目标正式文件是否已就绪 (秒级断点识别，且对核心文件严格比对 MD5)
            if (File.Exists(targetPath))
            {
                var finalInfo = new FileInfo(targetPath);
                if (finalInfo.Length == file.Size)
                {
                    bool match = true;
                    if (!string.IsNullOrEmpty(file.Checksum) && NeedsMd5Verification(rel, file.Size))
                    {
                        string localMd5 = await ComputeFileMd5Async(targetPath, token).ConfigureAwait(false);
                        if (!localMd5.Equals(file.Checksum, StringComparison.OrdinalIgnoreCase))
                        {
                            match = false;
                            try { File.Delete(targetPath); } catch { }
                        }
                    }

                    if (match)
                    {
                        onProgress?.Invoke((int)file.Size);
                        return true;
                    }
                }
            }

            // 2. 检查对端数据目录是否存在同名同大小资源 (国服/国际服互通资产复用)
            string altRelPath = string.Empty;
            if (rel.StartsWith("YuanShen_Data", StringComparison.OrdinalIgnoreCase))
            {
                altRelPath = Path.Combine(installPath, "GenshinImpact_Data" + rel.Substring("YuanShen_Data".Length));
            }
            else if (rel.StartsWith("GenshinImpact_Data", StringComparison.OrdinalIgnoreCase))
            {
                altRelPath = Path.Combine(installPath, "YuanShen_Data" + rel.Substring("GenshinImpact_Data".Length));
            }

            if (!string.IsNullOrEmpty(altRelPath) && File.Exists(altRelPath))
            {
                var altInfo = new FileInfo(altRelPath);
                if (altInfo.Length == file.Size)
                {
                    bool match = true;
                    if (!string.IsNullOrEmpty(file.Checksum) && NeedsMd5Verification(rel, file.Size))
                    {
                        string localMd5 = await ComputeFileMd5Async(altRelPath, token).ConfigureAwait(false);
                        if (!localMd5.Equals(file.Checksum, StringComparison.OrdinalIgnoreCase))
                        {
                            match = false;
                        }
                    }

                    if (match)
                    {
                        string? targetDir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                        File.Copy(altRelPath, targetPath, true);
                        onProgress?.Invoke((int)file.Size);
                        return true;
                    }
                }
            }

            // 3. 检查是否有旧 staging 遗留文件
            string stagingPath = Path.Combine(installPath, "staging", rel);
            if (File.Exists(stagingPath))
            {
                var sInfo = new FileInfo(stagingPath);
                if (sInfo.Length == file.Size)
                {
                    string? targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                    File.Move(stagingPath, targetPath, true);
                    onProgress?.Invoke((int)file.Size);
                    return true;
                }
                try { File.Delete(stagingPath); } catch { }
            }

            // 4. 下载到 targetPath + ".downloading"，单文件原子提交落盘
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string downloadingPath = targetPath + ".downloading";
            if (File.Exists(downloadingPath))
            {
                var info = new FileInfo(downloadingPath);
                if (info.Length == file.Size)
                {
                    string localMd5 = await ComputeFileMd5Async(downloadingPath, token);
                    if (localMd5.Equals(file.Checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(downloadingPath, targetPath, true);
                        onProgress?.Invoke((int)file.Size);
                        return true;
                    }
                }
                File.Delete(downloadingPath);
            }

            using (var fs = new FileStream(downloadingPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var chunk in file.Chunks)
                {
                    token.ThrowIfCancellationRequested();

                    long written = await DownloadAndDecompressChunkAsync($"{urlPrefix}/{chunk.Id}", fs, chunk.CompressedSize, onBytesTransferred, token);
                    onProgress?.Invoke((int)written);
                }
            }

            string finalMd5 = await ComputeFileMd5Async(downloadingPath, token);
            if (!finalMd5.Equals(file.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke(string.Format("Download_VerifyFailed".GetLocalized(), file.Path, file.Checksum, finalMd5));
                if (File.Exists(downloadingPath)) File.Delete(downloadingPath);
                return false;
            }

            // 单文件原子提交：立即成为正式游戏文件！绝不受后续中断影响！
            File.Move(downloadingPath, targetPath, true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke(string.Format("Download_FileException".GetLocalized(), file.Path, ex.Message));
            return false;
        }
    }
    
    private async Task<long> DownloadAndDecompressChunkAsync(string url, Stream target, int compressedSize, Action<long>? onBytesTransferred, CancellationToken token)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zst");
        try
        {
            await _chunkDownloader.DownloadFileAsync(url, tempPath, compressedSize, null, token, onBytesTransferred).ConfigureAwait(false);

            using var compressedStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None, ChunkDownloader.BufferSize, true);
            using var decompressor = new DecompressionStream(compressedStream);

            byte[] buffer = new byte[ChunkDownloader.BufferSize];
            long total = 0;
            while (true)
            {
                int read = await decompressor.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                total += read;
            }

            return total;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static bool NeedsMd5Verification(string relPath, long size)
    {
        // 1. 小于 2MB 的极小文件：全量校验 MD5（耗时微秒级，瞬间扫完；涵盖 level0~5、boot.config、轻量配置，彻底根治白屏）
        if (size <= 2 * 1024 * 1024) return true;

        // 2. 关键系统组件、可执行体与运行时元数据
        string ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is ".dll" or ".exe" or ".sys" or ".dat") return true;

        // 3. Unity 根目录核心资产（level 场景、sharedassets、resources.assets、globalgamemanagers）
        string fileName = Path.GetFileName(relPath);
        if (fileName.StartsWith("level", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("resources.assets", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 大文件音频包 (.pck)、资源块 (.blk)、视频 (.usm) 等数十 GB 数据，跨版本大小必然剧烈变动，大小一致即 100% 完好，跳过耗时磁盘哈希
        return false;
    }

    private async Task<string> ComputeFileMd5Async(string filePath, CancellationToken token)
    {
        using var md5 = MD5.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        byte[] hash = await md5.ComputeHashAsync(stream, token);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private void ReportProgress(long downloaded, long total, int filesDone, int filesTotal, bool force = false)
    {
        long now = DateTime.UtcNow.Ticks;
        if (force || (now - _lastReportTicks) > 1000000)
        {
            _lastReportTicks = now;
            ProgressChanged?.Invoke(downloaded, total, filesDone, filesTotal);
        }
    }

    private void MoveFilesRecursively(DirectoryInfo source, DirectoryInfo target)
    {
        if (!target.Exists) target.Create();
        foreach (var file in source.GetFiles())
        {
            string targetPath = Path.Combine(target.FullName, file.Name);
            if (File.Exists(targetPath)) File.Delete(targetPath);
            file.MoveTo(targetPath);
        }
        foreach (var dir in source.GetDirectories())
        {
            MoveFilesRecursively(dir, target.CreateSubdirectory(dir.Name));
        }
    }

    private string FormatSize(long bytes) => $"{bytes / 1024.0 / 1024.0:F2} MB";
}
