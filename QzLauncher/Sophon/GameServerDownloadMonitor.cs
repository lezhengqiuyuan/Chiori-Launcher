/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
namespace FufuLauncher.Models.GameServer;

public sealed class GameServerDownloadMonitor
{
    private long _totalBytesTransferred;
    public long TotalBytesTransferred => Interlocked.Read(ref _totalBytesTransferred);
    public void AddBytes(long bytes) => Interlocked.Add(ref _totalBytesTransferred, bytes);
    public void Reset() => Interlocked.Exchange(ref _totalBytesTransferred, 0);
}
