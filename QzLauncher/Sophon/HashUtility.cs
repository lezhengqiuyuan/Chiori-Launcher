using System.IO;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace FufuLauncher.Helpers;

public static class HashUtility
{
    public static string Md5File(string filepath)
    {
        using var md5 = MD5.Create();
        using var stream = new FileStream(filepath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hashBytes = md5.ComputeHash(stream);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    public static async Task<string> Md5FileAsync(string filepath, CancellationToken token = default)
    {
        using var md5 = MD5.Create();
        using var stream = new FileStream(filepath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] hashBytes = await md5.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    public static string Md5Bytes(byte[] data)
    {
        using var md5 = MD5.Create();
        return BitConverter.ToString(md5.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }

    public static string XxHash64Hex(Stream stream)
    {
        var algorithm = new XxHash64();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            algorithm.Append(buffer.AsSpan(0, read));
        }
        return BitConverter.ToString(algorithm.GetCurrentHash()).Replace("-", "").ToLowerInvariant();
    }

    public static async Task<string> XxHash64HexAsync(Stream stream, CancellationToken token = default)
    {
        var algorithm = new XxHash64();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read <= 0) break;
            algorithm.Append(buffer.AsSpan(0, read));
        }
        return BitConverter.ToString(algorithm.GetCurrentHash()).Replace("-", "").ToLowerInvariant();
    }
}
