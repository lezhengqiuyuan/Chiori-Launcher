using System.IO;
using QzLauncher.Services;

namespace FufuLauncher.Helpers;

public static class AppPaths
{
    public static string ServerCacheDir => Path.Combine(ChioriWorkspace.RootDir, "Cache", "Server");
    public static string VerifyCacheDir => Path.Combine(ChioriWorkspace.RootDir, "Cache", "Verify");
}
