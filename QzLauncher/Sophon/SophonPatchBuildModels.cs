/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Text.Json.Serialization;

namespace FufuLauncher.Models.GameServer;

public sealed class SophonBranchPayload
{
    [JsonPropertyName("package_id")]
    public string? PackageId { get; set; }

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;
    
    [JsonPropertyName("diff_tags")]
    public List<string> DiffTags { get; set; } = [];

    [JsonPropertyName("categories")]
    public List<SophonBranchCategory>? Categories { get; set; }
}

public sealed class SophonBranchCategory
{
    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }

    [JsonPropertyName("matching_field")]
    public string? MatchingField { get; set; }
}

public sealed class SophonPatchBuildResponse
{
    [JsonPropertyName("build_id")]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("patch_id")]
    public string PatchId { get; set; } = string.Empty;

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("manifests")]
    public List<SophonPatchManifestInfo> Manifests { get; set; } = [];
}

public sealed class SophonPatchManifestInfo
{
    [JsonPropertyName("category_id")]
    public string CategoryId { get; set; } = string.Empty;

    [JsonPropertyName("category_name")]
    public string CategoryName { get; set; } = string.Empty;

    [JsonPropertyName("manifest")]
    public SophonPatchManifestRef Manifest { get; set; } = new();
    
    [JsonPropertyName("diff_download")]
    public SophonPatchDownloadInfo? DiffDownload { get; set; }
    
    [JsonPropertyName("manifest_download")]
    public SophonPatchDownloadInfo? ManifestDownload { get; set; }

    [JsonPropertyName("matching_field")]
    public string MatchingField { get; set; } = string.Empty;
    
    [JsonPropertyName("stats")]
    public Dictionary<string, SophonPatchStats> Stats { get; set; } = [];
}

public sealed class SophonPatchManifestRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("checksum")]
    public string Checksum { get; set; } = string.Empty;
}

public sealed class SophonPatchDownloadInfo
{
    [JsonPropertyName("encryption")]
    public uint Encryption { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("compression")]
    public uint Compression { get; set; }

    [JsonPropertyName("url_prefix")]
    public string UrlPrefix { get; set; } = string.Empty;

    [JsonPropertyName("url_suffix")]
    public string? UrlSuffix { get; set; }
}

public sealed class SophonPatchStats
{
    [JsonPropertyName("compressed_size")]
    public long CompressedSize { get; set; }

    [JsonPropertyName("uncompressed_size")]
    public long UncompressedSize { get; set; }

    [JsonPropertyName("file_count")]
    public uint FileCount { get; set; }

    [JsonPropertyName("chunk_count")]
    public uint ChunkCount { get; set; }
}

public sealed class SophonDecodedPatchBuild
{
    public SophonDecodedPatchBuild(string originalTag, string tag, long downloadTotalBytes, long downloadFileCount,
        long uncompressedTotalBytes, long installFileCount, List<SophonDecodedPatchManifest> manifests)
    {
        OriginalTag = originalTag;
        Tag = tag;
        DownloadTotalBytes = downloadTotalBytes;
        DownloadFileCount = downloadFileCount;
        UncompressedTotalBytes = uncompressedTotalBytes;
        InstallFileCount = installFileCount;
        Manifests = manifests;
    }
    
    public string OriginalTag { get; }
    
    public string Tag { get; }
    
    public long DownloadTotalBytes { get; }
    
    public long DownloadFileCount { get; }
    
    public long UncompressedTotalBytes { get; }
    
    public long InstallFileCount { get; }

    public List<SophonDecodedPatchManifest> Manifests { get; }
}

public sealed class SophonDecodedPatchManifest
{
    public SophonDecodedPatchManifest(string originalTag, string tag, string urlPrefix, string urlSuffix, PatchManifest data)
    {
        OriginalTag = originalTag;
        Tag = tag;
        UrlPrefix = urlPrefix;
        UrlSuffix = urlSuffix;
        Data = data;
    }

    public string OriginalTag { get; }

    public string Tag { get; }
    
    public string UrlPrefix { get; }
    
    public string UrlSuffix { get; }

    public PatchManifest Data { get; }
}

public sealed class SophonPatchAsset
{
    public SophonPatchAsset(string urlPrefix, string urlSuffix, PatchFileData fileData, PatchInfo patchInfo)
    {
        UrlPrefix = string.Intern(urlPrefix);
        UrlSuffix = string.Intern(urlSuffix);
        FileData = fileData;
        PatchInfo = patchInfo;
    }

    public string UrlPrefix { get; }

    public string UrlSuffix { get; }

    public PatchFileData FileData { get; }

    public PatchInfo PatchInfo { get; }

    public string PatchDownloadUrl => $"{UrlPrefix}/{PatchInfo.Id}{UrlSuffix}";
    
    public string ExpectedHashPrefix => PatchInfo.Id.Split('_')[0];
}
