using System.Threading;
using System.Threading.Tasks;
using FufuLauncher.Models.GameServer;
using FufuLauncher.Services;
using FufuLauncher.Services.GameServer;

namespace QzLauncher.Services;

/// <summary>
/// Sophon 资源服务：下载国服资源 / 国服与国际服互转
/// 封装米哈游官方 Sophon CDN 极速下载和 Diff 差分转换核心
/// </summary>
public static class SophonService
{
    /// <summary>
    /// 下载国服完整资源 (Sophon CDN)
    /// </summary>
    public static async Task DownloadChinaAsync(
        string gameDir,
        Action<string> logCallback,
        Action<long, long, int, int> progressCallback,
        CancellationToken token = default,
        string targetTag = "7.0.0")
    {
        var httpClientProvider = new GameServerHttpClientProvider();
        var sophonBuildClient = new SophonBuildClient(httpClientProvider);
        var chunkDownloader = new ChunkDownloader(httpClientProvider);

        var scheme = GameServerScheme.ChineseOfficialOfficial;
        var downloader = new GenshinDownloader(sophonBuildClient, chunkDownloader, scheme);

        downloader.Log += logCallback;
        downloader.ProgressChanged += progressCallback;

        await downloader.StartDownloadAsync(gameDir, "zh-cn", true, 16, token, null, targetTag);
    }

    /// <summary>
    /// 服务器转换（国服 ⇄ 国际服互转）
    /// </summary>
    public static async Task ConvertServerAsync(
        string gameDir,
        GameServerScheme currentScheme,
        GameServerScheme targetScheme,
        Action<string> logCallback,
        IProgress<GameServerConversionProgress> progress,
        CancellationToken token = default,
        string targetTag = "7.0.0")
    {
        var httpClientProvider = new GameServerHttpClientProvider();
        var sophonBuildClient = new SophonBuildClient(httpClientProvider);
        var chunkDownloader = new ChunkDownloader(httpClientProvider);
        var configService = new GameServerConfigurationService();
        var sdkService = new GameChannelSdkService(chunkDownloader, sophonBuildClient);

        var converter = new GameServerConverter(sophonBuildClient, chunkDownloader, configService, sdkService);

        await converter.ConvertAsync(
            gameDir,
            currentScheme,
            targetScheme,
            progress,
            logCallback,
            token,
            targetTag: targetTag);
    }
}
