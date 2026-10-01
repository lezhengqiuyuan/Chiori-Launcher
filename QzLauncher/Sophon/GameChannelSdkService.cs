/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.IO.Compression;
using System.Text.Json;
using FufuLauncher.Constants;
using FufuLauncher.Helpers;
using FufuLauncher.Models.GameServer;

namespace FufuLauncher.Services.GameServer;

public sealed class GameChannelSdkService
{
    private const string SdkVersionFileName = "sdk_pkg_version";

    private readonly ChunkDownloader _chunkDownloader;
    private readonly SophonBuildClient _sophonBuildClient;

    public GameChannelSdkService(ChunkDownloader chunkDownloader, SophonBuildClient sophonBuildClient)
    {
        _chunkDownloader = chunkDownloader;
        _sophonBuildClient = sophonBuildClient;
    }
    
    public async Task EnsureSdkAndDeprecatedFilesAsync(string gameDir, GameServerScheme scheme,
        Action<string>? print = null, CancellationToken token = default, Action<long>? onBytesTransferred = null)
    {
        print?.Invoke("GameServer_CleaningLegacySdk".GetLocalized());

        string[] legacySdkFiles =
        {
            Path.Combine(gameDir, GameConstants.CN_DATA_DIR, "Plugins", "PCGameSDK.dll"),
            Path.Combine(gameDir, GameConstants.OS_DATA_DIR, "Plugins", "PCGameSDK.dll"),
            Path.Combine(gameDir, GameConstants.CN_DATA_DIR, "Plugins", "EOSSDK-Win64-Shipping.dll"),
            Path.Combine(gameDir, GameConstants.OS_DATA_DIR, "Plugins", "EOSSDK-Win64-Shipping.dll"),
            Path.Combine(gameDir, GameConstants.CN_DATA_DIR, "Plugins", "PluginEOSSDK.dll"),
            Path.Combine(gameDir, GameConstants.OS_DATA_DIR, "Plugins", "PluginEOSSDK.dll"),
            Path.Combine(gameDir, SdkVersionFileName),
        };

        foreach (string file in legacySdkFiles)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch
            {
                // ignored
            }
        }

        await EnsureChannelSdkAsync(gameDir, scheme, print, token, onBytesTransferred).ConfigureAwait(false);
        await ProcessDeprecatedFilesAsync(gameDir, scheme, print, token).ConfigureAwait(false);
    }
    
    public async Task EnsureChannelSdkAsync(string gameDir, GameServerScheme scheme,
        Action<string>? print = null, CancellationToken token = default, Action<long>? onBytesTransferred = null)
    {
        print?.Invoke("GameServer_FetchingChannelSdk".GetLocalized());
        (string? sdkPkgUrl, long sdkSize, string sdkMd5) = await GetChannelSdkPackageAsync(scheme, token).ConfigureAwait(false);
        if (sdkPkgUrl is null)
        {
            return;
        }

        print?.Invoke("GameServer_DownloadingChannelSdk".GetLocalized());
        string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await _chunkDownloader.DownloadFileAsync(
                sdkPkgUrl, tempFile,
                sdkSize > 0 ? sdkSize : null,
                string.IsNullOrEmpty(sdkMd5) ? null : sdkMd5,
                token, onBytesTransferred).ConfigureAwait(false);

            print?.Invoke("GameServer_ExtractingChannelSdk".GetLocalized());
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(tempFile);
                foreach (var entry in archive.Entries)
                {
                    string destination = Path.GetFullPath(Path.Combine(gameDir, entry.FullName));
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (File.Exists(destination))
                    {
                        File.SetAttributes(destination, FileAttributes.Normal);
                    }

                    entry.ExtractToFile(destination, true);
                }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // ignored
            }
        }
    }
    
    public async Task VerifyAndRepairChannelSdkAsync(string gameDir, GameServerScheme scheme,
        Action<string>? print = null, CancellationToken token = default, Action<long>? onBytesTransferred = null)
    {
        string versionFilePath = Path.Combine(gameDir, SdkVersionFileName);
        bool conflicted = !File.Exists(versionFilePath);

        if (!conflicted)
        {
            try
            {
                using var reader = File.OpenText(versionFilePath);
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { Length: > 0 } row)
                {
                    ChannelSdkVersionItem? item = JsonSerializer.Deserialize<ChannelSdkVersionItem>(row);
                    if (item is null || string.IsNullOrEmpty(item.RelativePath))
                    {
                        conflicted = true;
                        break;
                    }

                    string filePath = Path.Combine(gameDir, item.RelativePath);
                    if (!File.Exists(filePath))
                    {
                        conflicted = true;
                        break;
                    }

                    string actualMd5 = await HashUtility.Md5FileAsync(filePath, token).ConfigureAwait(false);
                    if (!actualMd5.Equals(item.Md5, StringComparison.OrdinalIgnoreCase))
                    {
                        conflicted = true;
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                conflicted = true;
            }
        }

        if (conflicted)
        {
            print?.Invoke("GameServer_ChannelSdkConflict".GetLocalized());
            await EnsureChannelSdkAsync(gameDir, scheme, print, token, onBytesTransferred).ConfigureAwait(false);
        }
    }
    
    private async Task ProcessDeprecatedFilesAsync(string gameDir, GameServerScheme scheme, Action<string>? print, CancellationToken token)
    {
        try
        {
            print?.Invoke("GameServer_ProcessingDeprecatedFiles".GetLocalized());

            string url = $"{scheme.HypApi}/getGameDeprecatedFileConfigs?channel={(int)scheme.Channel}&game_ids[]={scheme.GameId}&launcher_id={scheme.LauncherId}&sub_channel={(int)scheme.SubChannel}";
            string jsonResp = await _sophonBuildClient.GetStringWithRetryAsync(url, token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(jsonResp);
            if (!doc.RootElement.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!dataProp.TryGetProperty("deprecated_file_configs", out var configsProp) || configsProp.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var configs in configsProp.EnumerateArray())
            {
                if (!configs.TryGetProperty("deprecated_files", out var filesProp) || filesProp.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var file in filesProp.EnumerateArray())
                {
                    if (!file.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? name = nameProp.GetString();
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    string filePath = Path.Combine(gameDir, name);
                    if (!File.Exists(filePath))
                    {
                        continue;
                    }

                    try
                    {
                        File.SetAttributes(filePath, FileAttributes.Normal);
                        File.Move(filePath, $"{filePath}.backup", true);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }
        catch (Exception ex)
        {
            print?.Invoke(string.Format("GameServer_DeprecatedFileFetchFailed".GetLocalized(), ex.Message));
        }
    }


    private async Task<(string? Url, long Size, string Md5)> GetChannelSdkPackageAsync(GameServerScheme scheme, CancellationToken token)
    {
        string url = $"{scheme.HypApi}/getGameChannelSDKs?channel={(int)scheme.Channel}&game_ids[]={scheme.GameId}&launcher_id={scheme.LauncherId}&sub_channel={(int)scheme.SubChannel}";
        string jsonResp = await _sophonBuildClient.GetStringWithRetryAsync(url, token).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(jsonResp);
        if (!doc.RootElement.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.Object)
        {
            return (null, 0, string.Empty);
        }

        if (!dataProp.TryGetProperty("game_channel_sdks", out var sdksProp) || sdksProp.ValueKind != JsonValueKind.Array)
        {
            return (null, 0, string.Empty);
        }
        
        foreach (var sdk in sdksProp.EnumerateArray())
        {
            if (!sdk.TryGetProperty("channel_sdk_pkg", out var pkgProp) || pkgProp.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!pkgProp.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? sdkPkgUrl = urlProp.GetString();
            if (string.IsNullOrEmpty(sdkPkgUrl))
            {
                continue;
            }

            long size = pkgProp.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number ? sizeProp.GetInt64() : 0;
            string md5 = pkgProp.TryGetProperty("md5", out var md5Prop) && md5Prop.ValueKind == JsonValueKind.String ? md5Prop.GetString() ?? string.Empty : string.Empty;
            return (sdkPkgUrl, size, md5);
        }

        return (null, 0, string.Empty);
    }
}
