namespace FufuLauncher.Helpers;

public static class Localization
{
    private static readonly Dictionary<string, string> Strings = new(StringComparer.OrdinalIgnoreCase)
    {
        // 服务器前缀
        ["GameServer_SchemePrefixOfficial"] = "官方服",
        ["GameServer_SchemePrefixOversea"] = "国际服",
        ["GameServer_SchemePrefixBilibili"] = "B服",

        // 下载相关
        ["Download_Connecting"] = "正在连接下载服务器...",
        ["Download_VoiceOnly"] = "仅下载语音包...",
        ["Download_FetchingManifest"] = "正在获取 {0} 资源清单...",
        ["Download_TaskStart"] = "开始下载：共 {0} 个文件 ({1})",
        ["Download_MovingFiles"] = "下载完成，正在整理合并游戏文件...",
        ["Download_AllDone"] = "国服资源下载完成！",
        ["Download_UserCancelled"] = "下载已取消",
        ["Download_FileUnfixable"] = "文件下载失败: {0}",
        ["Download_FileFailed"] = "有 {0} 个文件下载失败，请重试",
        ["Download_VerifyFailed"] = "文件校验失败: {0}",
        ["Download_FileException"] = "处理文件异常 {0}: {1}",

        // 转换阶段
        ["GameServer_StageFetchBranches"] = "正在获取服务器分支信息...",
        ["GameServer_StageDecodeManifests"] = "正在解析双端资源清单...",
        ["GameServer_StageDiff"] = "正在比对两端差异文件...",
        ["GameServer_StageDownloadChunks"] = "正在下载差异数据块...",
        ["GameServer_StageVerify"] = "正在校验文件完整性...",
        ["GameServer_StageApply"] = "正在应用差异文件并重构目录...",
        ["GameServer_StageSdk"] = "正在配置渠道 SDK...",
        ["GameServer_StageCleanup"] = "正在清理临时差分缓存...",
        ["GameServer_StageDone"] = "服务器转换完成！",
        ["GameServer_MergeDataFolder"] = "检测到目标数据目录已存在（历史残留），正在合并数据目录：{0} ➔ {1}",
        ["GameServer_ConvertSuccess"] = "已成功切换至 {0}！",
        ["GameServer_ConvertFailed"] = "服务器转换失败: {0}"
    };

    public static string GetLocalized(this string key)
    {
        if (Strings.TryGetValue(key, out var val))
        {
            return val;
        }
        return key;
    }
}
