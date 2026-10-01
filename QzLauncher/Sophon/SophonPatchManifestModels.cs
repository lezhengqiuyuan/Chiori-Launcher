/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using ProtoBuf;

namespace FufuLauncher.Models.GameServer;

[ProtoContract]
public sealed class PatchManifest
{
    [ProtoMember(1)]
    public List<PatchFileData> FileDatas { get; set; } = [];

    [ProtoMember(2)]
    public List<PatchDeleteFilesEntry> DeleteFilesEntries { get; set; } = [];
}

[ProtoContract]
public sealed class PatchFileData
{
    [ProtoMember(1)]
    public string FileName { get; set; } = string.Empty;

    [ProtoMember(2)]
    public long FileSize { get; set; }

    [ProtoMember(3)]
    public string FileHash { get; set; } = string.Empty;

    [ProtoMember(4)]
    public List<PatchEntry> PatchesEntries { get; set; } = [];
}

[ProtoContract]
public sealed class PatchEntry
{
    [ProtoMember(1)]
    public string Key { get; set; } = string.Empty;

    [ProtoMember(2)]
    public PatchInfo? PatchInfo { get; set; }
}

[ProtoContract]
public sealed class PatchInfo
{
    [ProtoMember(1)]
    public string Id { get; set; } = string.Empty;

    [ProtoMember(2)]
    public string Tag { get; set; } = string.Empty;

    [ProtoMember(3)]
    public string BuildId { get; set; } = string.Empty;

    [ProtoMember(4)]
    public long PatchFileSize { get; set; }

    [ProtoMember(5)]
    public string PatchesFileHash { get; set; } = string.Empty;

    [ProtoMember(6)]
    public long PatchStartOffset { get; set; }

    [ProtoMember(7)]
    public long PatchLength { get; set; }
    
    [ProtoMember(8)]
    public string? OriginalFileName { get; set; }

    [ProtoMember(9)]
    public long OriginalFileSize { get; set; }

    [ProtoMember(10)]
    public string OriginalFileHash { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class PatchDeleteFilesEntry
{
    [ProtoMember(1)]
    public string Key { get; set; } = string.Empty;

    [ProtoMember(2)]
    public PatchDeleteFiles? DeleteFiles { get; set; }
}

[ProtoContract]
public sealed class PatchDeleteFiles
{
    [ProtoMember(1)]
    public List<PatchFileInfo> Infos { get; set; } = [];
}

[ProtoContract]
public sealed class PatchFileInfo
{
    [ProtoMember(1)]
    public string Name { get; set; } = string.Empty;

    [ProtoMember(2)]
    public long Size { get; set; }

    [ProtoMember(3)]
    public string Hash { get; set; } = string.Empty;
}
