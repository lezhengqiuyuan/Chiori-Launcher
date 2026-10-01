/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Text.Json.Serialization;

namespace FufuLauncher.Models.GameServer;

public sealed class ChannelSdkVersionItem
{
    [JsonPropertyName("remoteName")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = string.Empty;

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }
}
