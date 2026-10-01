using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QzLauncher.Services
{
    public class CdnNodeItem
    {
        public string Name { get; set; } = string.Empty;
        public string Ip { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public int LatencyMs { get; set; } = -1; // -1 表示超时或不可达
        public string StatusText => LatencyMs >= 0 ? $"{LatencyMs} ms" : "超时";
        public bool IsOptimal { get; set; }
    }

    public static class HostsManager
    {
        private const string SectionStart = "# >>> Chiori Launcher CDN Boost >>>";
        private const string SectionEnd = "# <<< Chiori Launcher CDN Boost <<<";
        private const string TargetDomain = "autopatchhk.yuanshen.com";

        // 精选 CloudFront 香港/亚太优质 Anycast 边缘节点池
        public static readonly (string Name, string Ip, string Region)[] CandidateNodes = new[]
        {
            ("香港 Anycast 优质 01", "18.165.183.33", "中国香港"),
            ("香港 Anycast 优质 02", "18.165.183.82", "中国香港"),
            ("香港 Anycast 优质 03", "18.165.183.94", "中国香港"),
            ("香港 Anycast 优质 04", "18.165.183.125", "中国香港"),
            ("亚太 Anycast 核心 05", "143.204.214.11", "亚太区域"),
            ("亚太 Anycast 核心 06", "143.204.214.33", "亚太区域"),
            ("亚太 Anycast 核心 07", "143.204.214.93", "亚太区域"),
            ("亚太 Anycast 核心 08", "143.204.214.112", "亚太区域"),
            ("东京 Anycast 高速 09", "13.35.114.17", "日本东京"),
            ("新加坡 Anycast 10", "99.84.181.25", "新加坡"),
            ("新加坡 Anycast 11", "99.84.181.67", "新加坡")
        };

        private static string HostsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        private static extern uint DnsFlushResolverCache();

        /// <summary>
        /// 并发测试所有候选节点的 TCP 443 握手延迟
        /// </summary>
        public static async Task<List<CdnNodeItem>> BenchmarkAllNodesAsync(IProgress<string>? progress = null)
        {
            progress?.Report("正在并发测试亚太 CDN 节点握手延时...");
            var tasks = CandidateNodes.Select(async node =>
            {
                var item = new CdnNodeItem
                {
                    Name = node.Name,
                    Ip = node.Ip,
                    Region = node.Region
                };

                item.LatencyMs = await MeasureTcpLatencyAsync(node.Ip, 443, 1500);
                return item;
            });

            var results = (await Task.WhenAll(tasks)).ToList();

            // 按延迟升序排序 (超时排在最后)
            results.Sort((a, b) =>
            {
                if (a.LatencyMs < 0 && b.LatencyMs < 0) return 0;
                if (a.LatencyMs < 0) return 1;
                if (b.LatencyMs < 0) return -1;
                return a.LatencyMs.CompareTo(b.LatencyMs);
            });

            // 标记最优节点
            var best = results.FirstOrDefault(r => r.LatencyMs >= 0);
            if (best != null)
            {
                best.IsOptimal = true;
            }

            return results;
        }

        /// <summary>
        /// 测量单个 IP 的 TCP 端口延迟 (毫秒)
        /// </summary>
        public static async Task<int> MeasureTcpLatencyAsync(string ip, int port, int timeoutMs = 1500)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var tcp = new TcpClient();
                var sw = Stopwatch.StartNew();
                
                await tcp.ConnectAsync(ip, port, cts.Token);
                sw.Stop();

                return (int)sw.ElapsedMilliseconds;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 检查当前 Hosts 中是否已应用直连加速
        /// </summary>
        public static bool CheckCurrentBoostStatus(out string boundIp)
        {
            boundIp = string.Empty;
            try
            {
                if (!File.Exists(HostsFilePath)) return false;
                var content = File.ReadAllText(HostsFilePath);
                var match = Regex.Match(content, @"# >>> Chiori Launcher CDN Boost >>>\s*([\d\.]+)\s+autopatchhk\.yuanshen\.com");
                if (match.Success)
                {
                    boundIp = match.Groups[1].Value.Trim();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"读取 Hosts 失败: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 将优选 IP 安全写入系统 Hosts 文件
        /// </summary>
        public static (bool Success, string Message) ApplyHostsBoost(string optimalIp)
        {
            try
            {
                var hostsPath = HostsFilePath;
                var backupPath = Path.Combine(Path.GetDirectoryName(hostsPath)!, "hosts.chiori.bak");

                var originalContent = File.Exists(hostsPath) ? File.ReadAllText(hostsPath, Encoding.UTF8) : string.Empty;

                // 备份原始文件
                if (!File.Exists(backupPath) && !string.IsNullOrEmpty(originalContent))
                {
                    try { File.WriteAllText(backupPath, originalContent, Encoding.UTF8); } catch { }
                }

                // 清除原先已有的加速块
                var cleanedContent = RemoveBoostSection(originalContent).TrimEnd();

                // 构建新的加速块
                var sb = new StringBuilder(cleanedContent);
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                }

                sb.AppendLine(SectionStart);
                sb.AppendLine($"# 千织启动器优选直连节点 (生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss})");
                sb.AppendLine($"{optimalIp} {TargetDomain}");
                sb.AppendLine(SectionEnd);

                File.WriteAllText(hostsPath, sb.ToString(), new UTF8Encoding(false));

                // 刷新系统 DNS 缓存
                FlushSystemDnsCache();

                Logger.Info($"[HostsManager] 成功将 {TargetDomain} 优选绑定至 {optimalIp}");
                return (true, $"已成功优选绑定至香港节点 {optimalIp}，延迟已降至最低！");
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "权限不足：修改系统 Hosts 需要管理员权限，请右键以管理员身份运行启动器。");
            }
            catch (Exception ex)
            {
                return (false, $"写入 Hosts 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从系统 Hosts 文件中移除加速配置，恢复默认
        /// </summary>
        public static (bool Success, string Message) RestoreHosts()
        {
            try
            {
                var hostsPath = HostsFilePath;
                if (!File.Exists(hostsPath))
                {
                    return (true, "Hosts 文件不存在，无需还原。");
                }

                var content = File.ReadAllText(hostsPath, Encoding.UTF8);
                var cleanedContent = RemoveBoostSection(content);

                File.WriteAllText(hostsPath, cleanedContent, new UTF8Encoding(false));
                FlushSystemDnsCache();

                Logger.Info("[HostsManager] 已从 Hosts 中移除 CDN 加速规则，恢复系统默认 DNS。");
                return (true, "已恢复系统默认 DNS 解析规则。");
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "权限不足：还原系统 Hosts 需要管理员权限，请右键以管理员身份运行启动器。");
            }
            catch (Exception ex)
            {
                return (false, $"还原 Hosts 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 移除 Hosts 文本中的标记块
        /// </summary>
        private static string RemoveBoostSection(string content)
        {
            var pattern = $@"\r?\n?{Regex.Escape(SectionStart)}[\s\S]*?{Regex.Escape(SectionEnd)}\r?\n?";
            return Regex.Replace(content, pattern, "\r\n", RegexOptions.Multiline);
        }

        /// <summary>
        /// 刷新 Windows 本地 DNS 缓存
        /// </summary>
        public static void FlushSystemDnsCache()
        {
            try
            {
                DnsFlushResolverCache();
            }
            catch { }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ipconfig",
                    Arguments = "/flushdns",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(1000);
            }
            catch { }
        }
    }
}
