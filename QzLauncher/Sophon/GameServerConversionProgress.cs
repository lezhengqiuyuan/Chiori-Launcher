/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
namespace FufuLauncher.Models.GameServer;

public readonly record struct GameServerConversionProgress(
    string Stage,
    int TotalChunks,
    int DoneChunks,
    long TotalBytes,
    long DoneBytes,
    string? ChunkName,
    IReadOnlyList<string>? PendingChunks = null)
{
    public static GameServerConversionProgress Status(string stage) => new(stage, 0, 0, 0, 0, null);
    
    public static GameServerConversionProgress Reset(string stage, int totalChunks, long totalBytes, IReadOnlyList<string>? pendingChunks = null) =>
        new(stage, totalChunks, 0, totalBytes, 0, null, pendingChunks);

    public double Percent => TotalChunks > 0 ? DoneChunks * 100.0 / TotalChunks : 0;
}
