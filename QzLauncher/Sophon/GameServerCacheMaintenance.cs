/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Diagnostics;
using FufuLauncher.Helpers;

namespace FufuLauncher.Services.GameServer;

public static class GameServerCacheMaintenance
{
    public static void CleanLegacyCaches()
    {
        TryCleanLegacyGuidFolders(AppPaths.ServerCacheDir);
        TryCleanLegacyGuidFolders(AppPaths.VerifyCacheDir);
    }

    private static void TryCleanLegacyGuidFolders(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string directory in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(directory);
                if (name.Length == 32 && name.All(char.IsAsciiHexDigit))
                {
                    Directory.Delete(directory, true);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GameServerCacheMaintenance] 清理旧缓存失败 {root}: {ex.Message}");
        }
    }
}
