/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using FufuLauncher.Protobuf;

namespace FufuLauncher.Models.GameServer;

public sealed record SophonChunk(string UrlPrefix, string UrlSuffix, AssetChunk AssetChunk)
{
    public string DownloadUrl => $"{UrlPrefix}/{AssetChunk.ChunkName}{UrlSuffix}";
    public string ExpectedHashPrefix => AssetChunk.ChunkName.Split('_')[0];
}
