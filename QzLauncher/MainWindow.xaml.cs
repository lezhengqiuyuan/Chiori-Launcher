using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FufuLauncher.Constants;
using FufuLauncher.Helpers;
using FufuLauncher.Models.GameServer;
using FufuLauncher.Services.GameServer;
using Microsoft.Win32;
using QzLauncher.Services;
using QzLauncher.Services.Injection;
using IniFile = QzLauncher.Services.Injection.IniFile;

namespace QzLauncher;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _convertCts;
    private bool _isGameRunning = false;
    // 下载/转换「预占互斥闸门」：进入任一操作即在弹框前置位，杜绝下载与转服并发执行
    private bool _opActive = false;
    private TaskCompletionSource<bool>? _modalTcs;
    private List<CdnNodeItem>? _benchmarkNodes;
    private CancellationTokenSource? _scanCts;
    private bool _hasVerifiedCnIntegrity = false;
    private string? _lastVerifiedDir;

    // 注入设置模块核心字段
    private PluginPresetService? _presetService;
    private IniFile? _currentIni;
    private IniFile? _chioriIni;
    private IniFile? _fpsIni;
    private readonly ObservableCollection<PluginSettingItem> _settingItems = new();
    private readonly List<PluginSettingItem> _allSettingItems = new();
    private string _currentCategoryFilter = "全部";
    private CancellationTokenSource? _pluginDownloadCts;

    private void CancelCurrentScan()
    {
        try
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = null;
        }
        catch { }
    }

    // 防呆：统一按钮状态管理
    private void UpdateButtonStates()
    {
        bool downloading = _downloadCts != null;
        bool converting  = _convertCts  != null;
        bool running     = _isGameRunning;
        bool starting    = _opActive && !downloading && !converting; // 确认框预占阶段（尚未真正开始）
        BtnDownload.IsEnabled = !converting && !running && !starting;
        BtnConvert.IsEnabled  = !downloading && !running && !starting;
        BtnLaunch.IsEnabled   = !downloading && !converting && !running && !starting;
    }

    // 防呆：检测原神进程是否在运行
    private static bool IsGameProcessRunning()
        => Process.GetProcessesByName("YuanShen").Length > 0
        || Process.GetProcessesByName("GenshinImpact").Length > 0;

    // 清理残留/挂起的原神游戏进程（用于注入失败后避免僵尸进程与二次拉起冲突）
    private static void KillGameProcesses()
    {
        foreach (var name in new[] { "GenshinImpact", "YuanShen" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(2000); }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
    }

    // 防呆：检查目标目录所在磁盘剩余空间
    private static (bool ok, double freeGb) CheckDiskSpace(string dir, long requiredBytes)
    {
        try
        {
            string root = Path.GetPathRoot(dir) ?? dir;
            var drive = new DriveInfo(root);
            double freeGb = drive.AvailableFreeSpace / 1_073_741_824.0;
            return (drive.AvailableFreeSpace >= requiredBytes, freeGb);
        }
        catch { return (true, 0); }
    }

    // 防呆：判断国际服客户端是否完整（exe>=50MB 且 Data目录下文件数>=200）
    private static bool IsOsClientComplete(string gameDir)
    {
        string osExe  = Path.Combine(gameDir, GameConstants.OS_EXE);
        string osData = Path.Combine(gameDir, GameConstants.OS_DATA_DIR);
        if (!File.Exists(osExe) || new FileInfo(osExe).Length < 50_000_000) return false;
        if (!Directory.Exists(osData)) return false;
        try { return Directory.EnumerateFiles(osData, "*", SearchOption.AllDirectories).Take(200).Count() >= 200; }
        catch { return false; }
    }

    // 防呆：拦截危险系统目录（盘根 + 系统保留目录本身或其子目录）
    private static bool IsPathDangerous(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return true; }

        // 拦截磁盘根目录（如 C:\、D:\）
        string root = (Path.GetPathRoot(full) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(full) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;

        // 系统保留目录黑名单（不包含 AppContext.BaseDirectory，避免误拦截）
        string[] blocked =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ];

        foreach (var b in blocked)
        {
            if (string.IsNullOrWhiteSpace(b)) continue;
            string nb;
            try { nb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { continue; }
            if (string.IsNullOrEmpty(nb)) continue;

            // 精确匹配 或 子目录（必须带路径分隔符，防止 D:\Game 被 D:\G 误匹配）
            if (string.Equals(full, nb, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(nb + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public MainWindow()
    {
        InitializeComponent();

        Logger.LogEmitted += (level, msg) =>
        {
            Dispatcher.Invoke(() =>
            {
                TxtGlobalLog.Text = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            });
        };

        Closing += MainWindow_Closing;

        Logger.Info("千织 7.0 专用启动器 v3.0 启动成功，日志系统已初始化。");
        RefreshEnvironment();
        UpdateButtonStates();

        Loaded += async (s, e) =>
        {
            InitializeInjectionModule();

            // 启动时自动弹出使用教程
            await ShowTutorialDialogAsync();

            string gameDir = App.Config.GameDir;
            bool isUnlocated = string.IsNullOrWhiteSpace(gameDir)
                || !Directory.Exists(gameDir)
                || PatchService.CheckEnvironment(gameDir) == GameEnvironmentState.InvalidDirectory;

            if (isUnlocated)
            {
                var selectNow = await ShowDialogAsync(
                    "选择客户端路径",
                    "当前尚未定位到有效原神 7.0 游戏客户端路径。\n\n请点击下方按钮选择您的游戏根目录（或指定用于下载 7.0 国服的文件夹）。",
                    "选择游戏路径",
                    "稍后设置");

                if (selectNow)
                {
                    BtnBrowseGameDir_Click(this, new RoutedEventArgs());
                }
            }
        };
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_downloadCts != null || _convertCts != null)
        {
            e.Cancel = true;
            var confirm = await ShowDialogAsync("确认退出", "当前正在进行文件下载或服务器转换！\n\n如果现在强制退出，可能导致文件未就绪。\n确定要中断并退出启动器吗？", "确定退出", "取消");
            if (confirm)
            {
                _downloadCts?.Cancel();
                _convertCts?.Cancel();
                Closing -= MainWindow_Closing;
                Close();
            }
            return;
        }

        if (_isGameRunning || IsGameProcessRunning())
        {
            e.Cancel = true;
            var confirm = await ShowDialogAsync(
                "游戏仍在运行中",
                "检测到原神游戏仍在运行中！\n\n建议在游戏退出后再关闭启动器。\n是否仍要强制关闭？",
                "强制退出",
                "取消");
            if (confirm)
            {
                Closing -= MainWindow_Closing;
                Close();
            }
        }
    }

    private void Log(string message)
    {
        Logger.Info(message);
    }

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        Logger.OpenLogFile();
    }

    // ==========================================
    // 自定义原神暗黑玻璃拟态弹窗系统
    // ==========================================
    private async Task<bool> ShowDialogAsync(string title, string message, string confirmText = "确认", string? cancelText = "取消")
    {
        if (!Dispatcher.CheckAccess())
        {
            return await Dispatcher.InvokeAsync(() => ShowDialogAsync(title, message, confirmText, cancelText)).Result;
        }

        ModalTitle.Text = title;
        ModalMessage.Text = message;
        TxtModalConfirmBtn.Text = confirmText;

        if (string.IsNullOrEmpty(cancelText))
        {
            BtnModalCancel.Visibility = Visibility.Collapsed;
        }
        else
        {
            BtnModalCancel.Visibility = Visibility.Visible;
            TxtModalCancelBtn.Text = cancelText;
        }

        ModalDialogOverlay.Visibility = Visibility.Visible;
        _modalTcs = new TaskCompletionSource<bool>();
        return await _modalTcs.Task;
    }

    private void BtnModalConfirm_Click(object sender, RoutedEventArgs e)
    {
        ModalDialogOverlay.Visibility = Visibility.Collapsed;
        _modalTcs?.TrySetResult(true);
    }

    private void BtnModalCancel_Click(object sender, RoutedEventArgs e)
    {
        ModalDialogOverlay.Visibility = Visibility.Collapsed;
        _modalTcs?.TrySetResult(false);
    }

    // ==========================================
    // 游戏环境检测与界面刷新
    // ==========================================
    private void RefreshEnvironment()
    {
        string gameDir = App.Config.GameDir;

        if (string.IsNullOrWhiteSpace(gameDir))
        {
            TxtShortGameDir.Text = "点击选择路径";
        }
        else
        {
            TxtShortGameDir.Text = gameDir;
        }

        if (OfflineDiffService.HasEmbeddedPackage())
        {
            BtnCdnBoost.Visibility = Visibility.Collapsed;
        }
        else if (HostsManager.CheckCurrentBoostStatus(out var boundIp))
        {
            GameServerHttpClientProvider.OptimalCdnIp = boundIp;
        }

        var envState = PatchService.CheckEnvironment(gameDir);

        if (envState == GameEnvironmentState.IsOverseaClient)
        {
            TxtCurrentSchemeBadge.Text = GameServerScheme.OverseaOfficialDefault.DisplayName; // "国际服 | Official | Official"
            TxtCurrentSchemeBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
            DotStatus.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
            BadgeServerScheme.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0x53));

            TxtConvertDirection.Text = "当前: 国际服 ➔ 目标: 官方国服 (仅限 7.0)";
            BtnConvert.Content = "一键转换为国服 (CDN 直连下载)";
            TxtConvertStatus.Text = "当前为国际服。点击可直连米哈游官方 CDN 专线高速下载差分包无损还原国服。";

            UpdateLocalClientState(gameDir, isChina: false);
            UpdateInjectionSwitchState(isChinaClient: false);
        }
        else if (envState == GameEnvironmentState.IsChinaClient)
        {
            TxtCurrentSchemeBadge.Text = GameServerScheme.ChineseOfficialOfficial.DisplayName; // "官方服 | Official | Official"
            TxtCurrentSchemeBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xD8, 0xFF));
            DotStatus.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xB0, 0xFF));
            BadgeServerScheme.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xB0, 0xFF));

            bool isVerified = _hasVerifiedCnIntegrity && string.Equals(_lastVerifiedDir, gameDir, StringComparison.OrdinalIgnoreCase);

            TxtConvertDirection.Text = "当前: 官方国服 ➔ 目标: 国际服 (仅限 7.0)";
            if (isVerified)
            {
                BtnConvert.Content = "一键转换为国际服 (内置文件秒转)";
                TxtConvertStatus.Text = "✓ 7.0 完整性校验已通过！可直接使用内置文件 10 秒离线秒转国际服。";
            }
            else
            {
                BtnConvert.Content = "一键转换为国际服 (需先校验)";
                TxtConvertStatus.Text = "⚠️ 国服转国际服必须先进行一次完整性校验与补全。";
            }

            UpdateLocalClientState(gameDir, isChina: true);
            UpdateInjectionSwitchState(isChinaClient: true);
        }
        else
        {
            TxtCurrentSchemeBadge.Text = "未定位游戏客户端";
            TxtCurrentSchemeBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x98, 0xAC));
            DotStatus.Fill = new SolidColorBrush(Color.FromRgb(0x8C, 0x98, 0xAC));
            BadgeServerScheme.BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x46, 0x68));

            TxtConvertDirection.Text = "未检测到有效客户端";
            BtnConvert.Content = "待就绪";
            TxtConvertStatus.Text = "请点击上方【游戏目录】选择路径，或直接下载国服。";

            PbDownload.Value = 0;
            TxtDownloadPercent.Text = "0.0%";
            TxtDownloadPercent.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xD8, 0xFF));
            TxtDownloadBytes.Text = "0 MB / 0 MB";
            TxtDownloadStatus.Text = "待命中。点击即可下载官方国服 7.0 完整资源。";
            BtnDownload.Content = "开始下载国服";
            BtnDownload.Background = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));
            UpdateInjectionSwitchState(isChinaClient: false);
        }
    }

    private void UpdateLocalClientState(string gameDir, bool isChina)
    {
        CancelCurrentScan();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        // 异步统计，避免阻塞 UI 渲染
        Task.Run(() =>
        {
            long totalBytes = 0;
            int fileCount = 0;
            try
            {
                var dir = new DirectoryInfo(gameDir);
                if (dir.Exists)
                {
                    foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        if (token.IsCancellationRequested) return;
                        if (f.FullName.Contains("staging", StringComparison.OrdinalIgnoreCase)) continue;
                        totalBytes += f.Length;
                        fileCount++;
                    }
                }
            }
            catch { }

            if (token.IsCancellationRequested) return;

            Dispatcher.Invoke(() =>
            {
                if (_downloadCts != null || token.IsCancellationRequested) return;

                PbDownload.Value = 100;
                TxtDownloadPercent.Text = "100.0%";
                TxtDownloadPercent.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
                TxtDownloadBytes.Text = $"已就绪: {totalBytes / 1073741824.0:F1} GB ({fileCount} 文件)";
                TxtDownloadStatus.Text = isChina
                    ? "本地国服 7.0 资源已就绪。可点击下方按钮重新校验。"
                    : "本地国际服 7.0 资源已就绪。已满足专用补丁运行环境。";
                BtnDownload.Content = "完整性校验与补全";
                BtnDownload.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
            });
        }, token);
    }

    private async void BtnBrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCts != null || _convertCts != null || _isGameRunning)
        {
            await ShowDialogAsync("提示", "当前有任务正在进行或游戏正在运行中，无法更改游戏目录！", "确定", null);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "请选择原神游戏根目录 (包含 YuanShen.exe 或 GenshinImpact.exe)",
            InitialDirectory = Directory.Exists(App.Config.GameDir) ? App.Config.GameDir : @"C:\"
        };

        if (dialog.ShowDialog() == true)
        {
            string chosen = dialog.FolderName;
            if (IsPathDangerous(chosen))
            {
                await ShowDialogAsync(
                    "⚠️ 路径不安全",
                    $"所选路径 [{chosen}] 为系统保留目录或磁盘根目录，\n不允许将游戏安装至此处，以免损坏系统文件！\n\n请选择一个专用游戏文件夹（如 D:\\Games\\Genshin）。",
                    "确定", null);
                return;
            }
            // 版本防呆：所选目录若含非 7.0 客户端（7.1 / 6.x 等），先提示改选空文件夹；用户坚持则二次警告后放行
            var chosenEnv = PatchService.CheckEnvironment(chosen);
            bool hasClient = chosenEnv == GameEnvironmentState.IsChinaClient || chosenEnv == GameEnvironmentState.IsOverseaClient;
            if (hasClient && !(GetClientVersion(chosen) is string cv && !string.IsNullOrEmpty(cv) && cv.StartsWith("7.0")))
            {
                string? verDesc = GetClientVersion(chosen);
                verDesc = string.IsNullOrEmpty(verDesc) ? "未知版本" : verDesc;

                bool goOn = await ShowDialogAsync(
                    "⚠ 检测到非 7.0 客户端",
                    $"所选文件夹 [{chosen}]\n检测到一个【{verDesc}】游戏客户端，并非本启动器专供的 7.0 版本！\n\n本启动器的下载 / 补丁 / 注入 / 互转均严格锁定 7.0.0，在 7.1 / 6.x 等其他版本目录上使用可能导致文件损坏或功能异常。\n\n强烈建议重新选择一个【空文件夹】。是否仍要坚持使用该目录？",
                    "仍要用这个文件夹", "重新选择");
                if (!goOn) return;

                bool confirm = await ShowDialogAsync(
                    "⚠ 最后确认：风险自负",
                    $"你正在坚持将【非 7.0 客户端】目录设为游戏目录：\n{chosen}\n\n请再次确认：本启动器不支持 7.0 以外的版本，后续一切异常需自行承担。\n\n确定要继续吗？",
                    "我已知风险，确定使用", "取消");
                if (!confirm) return;
            }

            App.Config.GameDir = chosen;
            App.Config.Save();
            _hasVerifiedCnIntegrity = false;
            _lastVerifiedDir = null;
            RefreshEnvironment();
            Log($"已更新游戏目录: {chosen}");
        }
    }

    // ==========================================
    // 功能 1：下载 7.0 国服版本 / 完整性校验
    // ==========================================
    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCts != null)
        {
            _downloadCts.Cancel();
            BtnDownload.IsEnabled = false;
            TxtDownloadStatus.Text = "正在取消下载...";
            return;
        }
        if (_opActive)
        {
            await ShowDialogAsync("提示", "当前已有下载或转换任务正在进行，请等待其完成或先取消当前任务后再操作！", "确定", null);
            return;
        }
        _opActive = true;
        UpdateButtonStates();
        bool handedOff = false;
        try
        {

        string gameDir = App.Config.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            var dialog = new OpenFolderDialog
            {
                Title = "请选择国服 7.0 游戏下载安装目录",
                InitialDirectory = Directory.Exists(gameDir) ? gameDir : @"D:\"
            };
            if (dialog.ShowDialog() != true) return;

            string chosen = dialog.FolderName;
            if (IsPathDangerous(chosen))
            {
                await ShowDialogAsync("⚠️ 路径不安全", $"所选路径 [{chosen}] 为系统保留目录或磁盘根目录，\n不允许将游戏安装至此处，以免损坏系统文件！\n\n请选择一个专用游戏文件夹（如 D:\\Games\\Genshin）。", "确定", null);
                return;
            }

            App.Config.GameDir = chosen;
            App.Config.Save();
            gameDir = chosen;
            RefreshEnvironment();
        }

        var envState = PatchService.CheckEnvironment(gameDir);
        if (envState == GameEnvironmentState.IsChinaClient)
        {
            var res = await ShowDialogAsync(
                "提示：本地已存在国服",
                "检测到当前游戏目录中已存在【国服客户端】！\n\n继续执行将进行快速完整性比对与增量补全（系统已开启秒级跳过已有完好文件，无需重新下载整包）。\n\n是否开始比对与增量补全？",
                "开始校验",
                "取消");

            if (!res) return;
        }
        else if (envState == GameEnvironmentState.IsOverseaClient)
        {
            if (IsOsClientComplete(gameDir))
            {
                // 国际服完整：强制走差分转换
                var res = await ShowDialogAsync(
                    "路径冲突：已是完整国际服",
                    "当前目录已存在【完整国际服客户端】！\n\n不允许直接在此目录下载国服（会损坏现有文件）。\n\n推荐使用【国服/国际服互转】差分转换，仅需下载极少量差异包即可秒切。\n\n是否立即启动差分转换？",
                    "立即差分转换",
                    "取消");
                if (res) { handedOff = true; _opActive = false; BtnConvert_Click(sender, e); }
                return;
            }
            else
            {
                // 国际服不完整（下载到一半）：给用户两个选择
                var res = await ShowDialogAsync(
                    "检测到不完整的国际服文件",
                    "当前目录存在【未下载完整的国际服文件】。\n\n选项：\n• 点【继续补全国际服】：先完成国际服下载，再用差分转换到国服。\n• 点【换目录下国服】：选择另一个空目录直接全量下载国服。\n\n请问如何操作？",
                    "换目录下国服",
                    "继续补全国际服");
                if (!res)
                {
                    // 用户选补全国际服：直接返回，让用户自行操作下载
                    return;
                }
                // 用户选换目录：弹选目录对话框
                var dlg = new OpenFolderDialog { Title = "请选择一个空目录用于下载国服" };
                if (dlg.ShowDialog() != true) return;
                string newDir = dlg.FolderName;
                if (IsPathDangerous(newDir))
                {
                    await ShowDialogAsync("路径不安全", $"所选路径 [{newDir}] 不允许使用，请选择专用游戏文件夹。", "确定", null);
                    return;
                }
                App.Config.GameDir = newDir;
                App.Config.Save();
                gameDir = newDir;
                _hasVerifiedCnIntegrity = false;
                _lastVerifiedDir = null;
                RefreshEnvironment();
            }
        }

        // 防呆：检测原神进程是否在运行
        if (IsGameProcessRunning())
        {
            await ShowDialogAsync("游戏运行中", "检测到原神游戏进程正在运行！\n\n请先完全退出游戏后，再执行下载/校验操作。", "确定", null);
            return;
        }

        // 防呆：磁盘空间检查（国服约 100 GB）
        var (diskOk, freeGb) = CheckDiskSpace(gameDir, 100L * 1024 * 1024 * 1024);
        if (!diskOk)
        {
            var cont = await ShowDialogAsync(
                "磁盘空间不足",
                $"目标磁盘剩余空间仅 {freeGb:F1} GB，\n下载国服 7.0 完整资源约需 100 GB！\n\n强烈建议先清理磁盘空间，否则下载中途可能因磁盘满而失败。\n\n是否仍要继续？",
                "强制继续",
                "取消");
            if (!cont) return;
        }

        Directory.CreateDirectory(gameDir);

        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;
        UpdateButtonStates();

        BtnDownload.Content = "取消任务";
        BtnDownload.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        PbDownload.Value = 0;
        TxtDownloadPercent.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xD8, 0xFF));
        TxtDownloadStatus.Text = "正在连接米哈游官方 Sophon CDN 获取最新清单...";
        Log("开始获取原神 7.0 国服资源清单并比对本地文件...");

        try
        {
            await SophonService.DownloadChinaAsync(
                gameDir,
                log =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        TxtDownloadStatus.Text = log;
                        Log(log);
                    });
                },
                (downloaded, total, doneFiles, totalFiles) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (total > 0)
                        {
                            double percent = (double)downloaded / total * 100.0;
                            PbDownload.Value = percent;
                            TxtDownloadPercent.Text = $"{percent:F1}%";
                            TxtDownloadBytes.Text = $"{downloaded / 1048576.0:F1} MB / {total / 1048576.0:F1} MB ({doneFiles}/{totalFiles})";
                        }
                    });
                },
                token,
                "7.0.0");

            TxtDownloadStatus.Text = "国服资源校验与补全完成！全部文件完好就绪。";
            PbDownload.Value = 100;
            TxtDownloadPercent.Text = "100.0%";
            TxtDownloadPercent.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
            Log("原神 7.0 国服资源完整性校验与补全已完成！");

            _hasVerifiedCnIntegrity = true;
            _lastVerifiedDir = gameDir;
            RefreshEnvironment();

            await ShowDialogAsync(
                "校验完成",
                "原神 7.0 国服完整性校验与补全已成功完成！\n全部文件均已通过官方 7.0 清单比对与校验。\n\n现已满足安全转服条件，可随时点击右侧【一键转换为国际服】进行秒转。",
                "确定",
                null);
        }
        catch (OperationCanceledException)
        {
            TxtDownloadStatus.Text = "任务已由用户取消。";
            Log("用户取消了资源下载/校验。");
        }
        catch (Exception ex)
        {
            TxtDownloadStatus.Text = $"处理出错: {ex.Message}";
            Log($"下载/校验失败: {ex.Message}");
            await ShowDialogAsync("操作失败", $"下载或校验过程中出现异常:\n{ex.Message}", "确定", null);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            RefreshEnvironment();
            UpdateButtonStates();
        }
        }
        finally
        {
            if (!handedOff) _opActive = false;
            UpdateButtonStates();
        }
    }

    // 检测客户端版本号 (读取 config.ini 或 exe 版本元数据)
    private static string? GetClientVersion(string gameDir)
    {
        try
        {
            string iniPath = Path.Combine(gameDir, "config.ini");
            if (File.Exists(iniPath))
            {
                foreach (var line in File.ReadAllLines(iniPath))
                {
                    var t = line.Trim();
                    if (t.StartsWith("game_version", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = t.Split('=');
                        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
                        {
                            return parts[1].Trim();
                        }
                    }
                }
            }

            string cnExe = Path.Combine(gameDir, "YuanShen.exe");
            if (File.Exists(cnExe))
            {
                var vi = FileVersionInfo.GetVersionInfo(cnExe);
                if (!string.IsNullOrEmpty(vi.FileVersion)) return vi.FileVersion;
                if (!string.IsNullOrEmpty(vi.ProductVersion)) return vi.ProductVersion;
            }

            string osExe = Path.Combine(gameDir, "GenshinImpact.exe");
            if (File.Exists(osExe))
            {
                var vi = FileVersionInfo.GetVersionInfo(osExe);
                if (!string.IsNullOrEmpty(vi.FileVersion)) return vi.FileVersion;
                if (!string.IsNullOrEmpty(vi.ProductVersion)) return vi.ProductVersion;
            }
        }
        catch { }
        return null;
    }

    // ==========================================
    // 功能 2：国服版本和国际服版本互切换 (仅限 7.0 版本)
    // ==========================================
    private async void BtnConvert_Click(object sender, RoutedEventArgs e)
    {
        if (_convertCts != null)
        {
            _convertCts.Cancel();
            BtnConvert.IsEnabled = false;
            TxtConvertStatus.Text = "正在取消转换...";
            return;
        }
        if (_opActive)
        {
            await ShowDialogAsync("提示", "当前已有下载或转换任务正在进行，请等待其完成或先取消当前任务后再操作！", "确定", null);
            return;
        }
        _opActive = true;
        UpdateButtonStates();
        try
        {

        // 防呆：互斥检查
        if (_downloadCts != null || _isGameRunning)
        {
            await ShowDialogAsync("提示", "当前有下载任务正在进行或游戏正在运行中，无法进行服务器转换！", "确定", null);
            return;
        }

        // 防呆：检测原神进程是否在运行
        if (IsGameProcessRunning())
        {
            await ShowDialogAsync("游戏运行中", "检测到原神游戏进程正在运行！\n\n请先完全退出游戏后，再执行服务器转换操作，以免文件损坏或被占用。", "确定", null);
            return;
        }

        string gameDir = App.Config.GameDir;
        var envState = PatchService.CheckEnvironment(gameDir);

        if (envState != GameEnvironmentState.IsChinaClient && envState != GameEnvironmentState.IsOverseaClient)
        {
            await ShowDialogAsync("提示", "请先点击上方【游戏目录】指定有效的客户端安装路径！", "确定", null);
            return;
        }

        // 严格防呆：国际服与国服互转仅支持 7.0 版本
        string? clientVer = GetClientVersion(gameDir);
        if (!string.IsNullOrEmpty(clientVer) && !clientVer.StartsWith("7.0"))
        {
            await ShowDialogAsync(
                "版本不兼容 (仅限 7.0)",
                $"国际服与国服互转【仅支持 7.0 版本】！\n\n检测到当前客户端版本为：{clientVer}\n本启动器专为 7.0 设计，不支持非 7.0 客户端（如 6.x 或 7.1 等）互转。\n\n如需体验 7.0 请先点击【下载 7.0 国服】获取对应版本。",
                "我知道了",
                null);
            return;
        }

        // 防呆：检查磁盘空间（至少留 3 GB）
        var (diskOk, freeGb) = CheckDiskSpace(gameDir, 3L * 1024 * 1024 * 1024);
        if (!diskOk)
        {
            var cont = await ShowDialogAsync("磁盘空间过低", $"目标磁盘剩余空间仅 {freeGb:F1} GB，转换需要释放和缓存必要组件。\n建议预留至少 3 GB 空间。是否仍要继续？", "强制继续", "取消");
            if (!cont) return;
        }

        GameServerScheme currentScheme = envState == GameEnvironmentState.IsChinaClient
            ? GameServerScheme.ChineseOfficialOfficial
            : GameServerScheme.OverseaOfficialDefault;

        GameServerScheme targetScheme = envState == GameEnvironmentState.IsChinaClient
            ? GameServerScheme.OverseaOfficialDefault
            : GameServerScheme.ChineseOfficialOfficial;

        bool hasEmbedded = OfflineDiffService.HasEmbeddedPackage();
        bool isCnToOs = targetScheme == GameServerScheme.OverseaOfficialDefault;
        bool isVerified = _hasVerifiedCnIntegrity && string.Equals(_lastVerifiedDir, gameDir, StringComparison.OrdinalIgnoreCase);

        if (isCnToOs)
        {
            if (!isVerified)
            {
                var doCheckAndConvert = await ShowDialogAsync(
                    "必须先进行完整性校验",
                    "【国服转国际服安全规范】\n国服转国际服必须先进行一次完整性校验与补全！\n\n检测到当前尚未对本地 7.0 国服客户端执行完整性校验。为防止底层游戏文件损坏、缺失或版本不匹配，必须先完成官方 7.0 完整性校验与补全。\n\n点击【立即校验并转换】将自动执行 7.0 资源校验与补齐，校验通过后自动转换为国际服；\n您也可以点击【取消】后手动点击左侧【完整性校验与补全】。",
                    "立即校验并转换",
                    "取消");

                if (!doCheckAndConvert) return;
            }
            else
            {
                var confirm = await ShowDialogAsync(
                    "确认服务器转换 (仅限 7.0)",
                    $"确定将客户端从 [{currentScheme.DisplayName}]\n转换为 [{targetScheme.DisplayName}] 吗？\n\n★ 本地国服 7.0 完整性校验已通过！\n★ 启动器已内置 7.0 国际服专属核心差异资源包，无需网络下载，10秒内极速离线秒转！",
                    "开始转换",
                    "取消");

                if (!confirm) return;
            }
        }
        else
        {
            var confirm = await ShowDialogAsync(
                "确认服务器转换 (仅限 7.0)",
                $"确定将客户端从 [{currentScheme.DisplayName}]\n转换为 [{targetScheme.DisplayName}] 吗？\n\n★ 国际服转国服（仅限 7.0）：\n直连米哈游官方国内高速 CDN（腾讯云/网宿专线），差分下载并合成 100% 正版官方国服 7.0.0 YuanShen.exe 与完整运行库。",
                "开始转换",
                "取消");

            if (!confirm) return;
        }

        // 释放任何后台扫描线程持有的文件和目录句柄，防止重命名报 Access is denied
        CancelCurrentScan();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        _convertCts = new CancellationTokenSource();
        var token = _convertCts.Token;
        UpdateButtonStates();

        BtnConvert.Content = "取消转换";
        BtnConvert.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        PbConvert.Value = 0;
        TxtConvertStatus.Text = targetScheme == GameServerScheme.OverseaOfficialDefault && hasEmbedded
            ? (isVerified ? "正在准备从单文件释放内置资产..." : "正在准备进行完整性校验...")
            : "正在比对双端差异清单...";
        Log($"开始服务器转换: {currentScheme.DisplayName} -> {targetScheme.DisplayName}");

        if (!hasEmbedded && targetScheme == GameServerScheme.OverseaOfficialDefault && string.IsNullOrEmpty(GameServerHttpClientProvider.OptimalCdnIp))
        {
            TxtConvertStatus.Text = "正在自动优选海外 CDN 香港直连节点...";
            try
            {
                var fastNodes = await HostsManager.BenchmarkAllNodesAsync();
                var optimal = fastNodes.FirstOrDefault(n => n.LatencyMs >= 0);
                if (optimal != null)
                {
                    GameServerHttpClientProvider.OptimalCdnIp = optimal.Ip;
                    Log($"[CDN 加速] 已自动锁定香港低延迟直连节点: {optimal.Ip} ({optimal.LatencyMs} ms)");
                    _ = Task.Run(() => HostsManager.ApplyHostsBoost(optimal.Ip));
                }
            }
            catch { }
        }

        var progress = new Progress<GameServerConversionProgress>(p =>
        {
            Dispatcher.Invoke(() =>
            {
                TxtConvertStatus.Text = p.ChunkName != null ? $"{p.Stage} ({p.ChunkName})" : p.Stage;
                if (p.TotalChunks > 0)
                {
                    PbConvert.Value = p.Percent;
                    TxtConvertPercent.Text = $"{p.Percent:F1}%";
                    TxtConvertBytes.Text = p.TotalBytes > 0
                        ? $"{p.DoneBytes / 1048576.0:F1} MB / {p.TotalBytes / 1048576.0:F1} MB ({p.DoneChunks}/{p.TotalChunks})"
                        : $"{p.DoneChunks}/{p.TotalChunks} 个组件";
                }
            });
        });

        try
        {
            if (targetScheme == GameServerScheme.OverseaOfficialDefault)
            {
                // 1. 国服转国际服：必须使用启动器内置 7.0 资源包离线极速秒转
                if (!hasEmbedded)
                {
                    await ShowDialogAsync("缺少内置资源", "国服转国际服需要内置离线资源包，但未在启动器中找到内置文件！", "确定", null);
                    return;
                }

                // 若尚未通过校验，执行第 1 步：官方 7.0 完整性校验与补全
                if (!isVerified)
                {
                    TxtConvertStatus.Text = "【第 1/2 步】正在连接官方 CDN 执行 7.0 完整性校验与补全...";
                    Log("国服转国际服：开始执行第 1 步【7.0 完整性校验与补全】...");

                    await SophonService.DownloadChinaAsync(
                        gameDir,
                        log => Dispatcher.Invoke(() =>
                        {
                            TxtConvertStatus.Text = $"【第 1/2 步 校验中】{log}";
                            TxtDownloadStatus.Text = log;
                            Log(log);
                        }),
                        (downloaded, total, doneFiles, totalFiles) => Dispatcher.Invoke(() =>
                        {
                            if (total > 0)
                            {
                                double percent = (double)downloaded / total * 100.0;
                                double convertPercent = percent * 0.8;
                                PbConvert.Value = convertPercent;
                                TxtConvertPercent.Text = $"{convertPercent:F1}%";
                                TxtConvertBytes.Text = $"{downloaded / 1048576.0:F1} MB / {total / 1048576.0:F1} MB ({doneFiles}/{totalFiles})";

                                PbDownload.Value = percent;
                                TxtDownloadPercent.Text = $"{percent:F1}%";
                                TxtDownloadBytes.Text = $"{downloaded / 1048576.0:F1} MB / {total / 1048576.0:F1} MB ({doneFiles}/{totalFiles})";
                            }
                        }),
                        token,
                        "7.0.0");

                    _hasVerifiedCnIntegrity = true;
                    _lastVerifiedDir = gameDir;
                    Log("国服 7.0 完整性校验与补全已完成！开始执行第 2 步：内置文件离线秒转国际服...");
                    TxtConvertStatus.Text = "【第 2/2 步】完整性校验通过！正在释放内置资源秒转为国际服...";
                    PbConvert.Value = 85;
                    TxtConvertPercent.Text = "85.0%";
                }

                // 执行第 2 步（或已校验后的直接转换）：使用内置单文件极速离线秒转
                await OfflineDiffService.ConvertToOverseaOfflineAsync(
                    gameDir,
                    progress,
                    msg => Dispatcher.Invoke(() => Log(msg)),
                    token);
            }
            else if (targetScheme == GameServerScheme.ChineseOfficialOfficial)
            {
                // 2. 国际服转国服：直接走米哈游官方 Sophon CDN 差分下载（显式锁定 7.0.0，绝不下成 7.1/6.6 等其他版本）
                await SophonService.ConvertServerAsync(
                    gameDir,
                    currentScheme,
                    targetScheme,
                    msg => Dispatcher.Invoke(() => Log(msg)),
                    progress,
                    token,
                    targetTag: "7.0.0");

                // 确保目录下不并存国际服主程序
                string osExe = Path.Combine(gameDir, "GenshinImpact.exe");
                string osExeBak = Path.Combine(gameDir, "GenshinImpact.exe.os_bak");
                if (File.Exists(osExe))
                {
                    OfflineDiffService.SafeMoveFile(osExe, osExeBak);
                }
            }

            TxtConvertStatus.Text = $"转换成功！现已切换为 {targetScheme.DisplayName}";
            PbConvert.Value = 100;
            TxtConvertPercent.Text = "100.0%";
            Log($"服务器转换成功，现已切换为 {targetScheme.DisplayName}");
            RefreshEnvironment();

            string successMsg = targetScheme == GameServerScheme.OverseaOfficialDefault
                ? $"客户端已成功转换为 [{targetScheme.DisplayName}]！\n现已满足专用扩展补丁环境要求，可随时启动游戏。"
                : $"客户端已成功转换为 [{targetScheme.DisplayName}]！\n已恢复为官方纯净国服。如需加载专用扩展补丁，请随时切换为国际服。";

            await ShowDialogAsync("转换成功", successMsg, "确定", null);
        }
        catch (OperationCanceledException)
        {
            TxtConvertStatus.Text = "转换已取消。";
            Log("用户取消了服务器转换。");
        }
        catch (Exception ex)
        {
            TxtConvertStatus.Text = $"转换失败: {ex.Message}";
            Log($"服务器转换异常: {ex.Message}");
            await ShowDialogAsync("转换失败", $"服务器转换出错:\n{ex.Message}", "确定", null);
        }
        finally
        {
            _convertCts?.Dispose();
            _convertCts = null;
            BtnConvert.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00));
            RefreshEnvironment();
            UpdateButtonStates();
        }
        }
        finally
        {
            _opActive = false;
            UpdateButtonStates();
        }
    }

    // ==========================================
    // 功能 3 & 4：启动游戏 (替换补丁/备份/退出恢复)
    // ==========================================
    private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_isGameRunning || IsGameProcessRunning())
        {
            await ShowDialogAsync("提示", "检测到原神游戏已在运行中，请勿重复启动！", "确定", null);
            return;
        }

        if (_downloadCts != null || _convertCts != null)
        {
            await ShowDialogAsync("提示", "当前有下载或转换任务正在进行中，请等待任务完成后再启动游戏！", "确定", null);
            return;
        }

        string gameDir = App.Config.GameDir;

        var envState = PatchService.CheckEnvironment(gameDir);

        // 用户明确要求：国服严禁注入启动，补丁与插件仅支持国际服
        if (envState == GameEnvironmentState.IsChinaClient)
        {
            App.Config.UseInjection = false;
            App.Config.Save();
            UpdateInjectionSwitchState(isChinaClient: true);

            var result = await ShowDialogAsync(
                "🛡️ 安全警告：国服严禁注入启动",
                "原神国服（官方服务器）反作弊机制极度严苛，严禁注入任何第三方插件或专用补丁，否则面临封号风险！\n\n【千织专用补丁与注入插件】遵循最高安全准则，绝不允许在国服运行，仅支持在【国际服】客户端下使用。\n\n检测到您当前游戏目录为【国服客户端】。\n请问是否立即一键转换为【国际服】？",
                "一键转换为国际服",
                "取消");

            if (result)
            {
                BtnConvert_Click(sender, e);
            }
            return;
        }

        if (envState != GameEnvironmentState.IsOverseaClient)
        {
            await ShowDialogAsync("错误", "未在指定目录下检测到有效的原神游戏客户端！请点击右上角【游戏目录】进行选择。", "确定", null);
            return;
        }

        string exePath = Path.Combine(gameDir, GameConstants.OS_EXE); // GenshinImpact.exe
        if (!File.Exists(exePath))
        {
            await ShowDialogAsync("错误", $"未找到国际服主程序 {GameConstants.OS_EXE}，请先执行转换！", "确定", null);
            return;
        }

        _isGameRunning = true;
        UpdateButtonStates();
        TxtLaunchBtnText.Text = "正在部署插件...";
        TxtLaunchSubNotice.Text = "备份官方 Astrolabe.dll 并加载专用扩展...";
        Log("准备启动：备份官方 Astrolabe.dll 并写入专用扩展补丁...");

        try
        {
            // 1. 替换千织 Astrolabe 补丁（服务器地址已写死在补丁内，无需外部配置）
            bool patched = await Task.Run(() => PatchService.ApplyPatch(gameDir));
            if (!patched)
            {
                TxtLaunchBtnText.Text = "启动游戏";
                TxtLaunchSubNotice.Text = "替换补丁失败，请检查文件是否被占用";
                Log("替换 Astrolabe.dll 失败！");
                await ShowDialogAsync("错误", "替换 Astrolabe.dll 补丁失败，请确保游戏未在运行且未被占用！", "确定", null);
                return;
            }

            // 2. 启动国际服游戏（注入模式 vs 纯净模式）
            int gamePid = 0;
            if (App.Config.UseInjection && envState == GameEnvironmentState.IsOverseaClient)
            {
                TxtLaunchBtnText.Text = "正在注入启动...";
                TxtLaunchSubNotice.Text = "调用核心引擎挂起拉起并注入插件...";
                Log("[启动流程] 已开启注入模式，正在调用 Launcher.dll 挂起注入...");

                string targetPlugin = App.Config.SelectedPlugin ?? "ChioriPlugin";
                string? dllPath = InjectionService.ResolveTargetPluginDll(targetPlugin);

                if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
                {
                    // 修复「注入设置不生效」：插件缺失不再静默降级普通启动，明确报错并中止，避免用户误以为已注入
                    TxtLaunchBtnText.Text = "启动游戏";
                    TxtLaunchSubNotice.Text = "插件未就绪，注入已中止";
                    TxtInjectionStatus.Text = "⚠ 注入失败：未找到可用插件 DLL，请先到【注入设置】下载/启用插件";
                    Log("[启动流程] ✖ 插件目录未找到有效 DLL，已中止注入启动（不再静默降级）。");
                    await ShowDialogAsync("注入无法生效", $"已开启注入，但在插件目录未找到【{targetPlugin}】的有效插件 DLL。\n\n请前往顶部【注入设置】下载或启用插件后重试。\n\n（为防止「以为已注入其实没注入」的误导，启动器已中止本次启动。）", "我知道了", null);
                    return;
                }

                Log($"[启动流程] 目标插件 DLL: {dllPath}");
                var (injectOk, injectMsg, pid) = await Task.Run(() => InjectionService.LaunchGameWithPluginNative(exePath, dllPath, ""));
                if (!injectOk)
                {
                    // 修复「开注入有时无法启动」：注入失败时 Launcher.dll 可能已创建挂起的僵尸游戏进程，
                    // 必须先清理，否则再拉起会与之冲突导致窗口出不来 / 卡「运行中」。
                    Log($"[启动流程] ⚠ 原生挂起注入失败: {injectMsg}，先清理可能残留的挂起进程再普通启动...");
                    KillGameProcesses();
                    await Task.Delay(400);

                    TxtInjectionStatus.Text = "⚠ 插件注入失败，已改为无注入普通启动";
                    bool started = await Task.Run(() => LaunchProcessElevated(exePath, gameDir));
                    if (!started)
                    {
                        TxtLaunchBtnText.Text = "启动游戏";
                        TxtLaunchSubNotice.Text = "游戏启动失败";
                        await ShowDialogAsync("启动失败", $"注入失败且普通启动也未成功:\n{injectMsg}", "确定", null);
                        return;
                    }
                    await ShowDialogAsync("注入未生效", $"插件注入失败：{injectMsg}\n\n已为你改用【无注入普通启动】，游戏可正常运行，但千织插件不会生效。", "我知道了", null);
                }
                else
                {
                    gamePid = pid;
                    Log($"[启动流程] ★ 原生挂起注入启动成功: {injectMsg}");
                    Dispatcher.Invoke(() =>
                    {
                        string pidDisplay = gamePid > 0 ? $"(PID={gamePid})" : "";
                        TxtInjectionStatus.Text = $"⚡ 插件已成功注入生效 {pidDisplay} · 120 帧率已解锁";
                    });
                }
            }
            else
            {
                // 未开启注入或非注入模式：管理员普通拉起
                TxtLaunchBtnText.Text = "正在启动游戏...";
                TxtLaunchSubNotice.Text = "以管理员权限拉起 GenshinImpact.exe...";
                Log("正在以 runas 提权拉起国际服原神客户端...");

                bool started = await Task.Run(() => LaunchProcessElevated(exePath, gameDir));
                if (!started)
                {
                    TxtLaunchBtnText.Text = "启动游戏";
                    TxtLaunchSubNotice.Text = "游戏启动失败或用户取消提权";
                    Log("游戏拉起失败。");
                    return;
                }
            }

            TxtLaunchBtnText.Text = "游戏运行中...";
            TxtLaunchSubNotice.Text = "千织补丁常驻生效中 · 游戏退出后可直接再次启动";
            Log("游戏已成功启动，后台守护中...");

            // 3. 后台非阻塞监控游戏退出
            await Task.Run(() => WaitForProcessExit("GenshinImpact"));

            // 4. 游戏退出（补丁常驻，无需还原）
            TxtLaunchBtnText.Text = "启动游戏";
            TxtLaunchSubNotice.Text = "游戏已退出 · 千织补丁常驻，可直接再次启动";
            Log("游戏进程已退出。千织补丁保持常驻，下次可直接启动。");
        }
        finally
        {
            _isGameRunning = false;
            UpdateButtonStates();
        }
    }

    private static bool LaunchProcessElevated(string exePath, string workDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workDir,
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Launch] 提权启动异常: {ex.Message}");
            return false;
        }
    }

    private static void WaitForProcessExit(string processName)
    {
        Thread.Sleep(5000);

        while (true)
        {
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length == 0)
            {
                break;
            }
            Thread.Sleep(2000);
        }
    }

    // ==========================================
    // 国际服 CDN 节点优选与 Hosts 加速系统
    // ==========================================
    private async void BtnCdnBoost_Click(object sender, RoutedEventArgs e)
    {
        CdnBoostDialogOverlay.Visibility = Visibility.Visible;
        UpdateCdnBoostStatusDisplay();
        if (_benchmarkNodes == null || _benchmarkNodes.Count == 0)
        {
            await RunBenchmarkAsync();
        }
    }

    private void BtnCloseCdnBoost_Click(object sender, RoutedEventArgs e)
    {
        CdnBoostDialogOverlay.Visibility = Visibility.Collapsed;
    }

    private async void BtnRefreshBenchmark_Click(object sender, RoutedEventArgs e)
    {
        await RunBenchmarkAsync();
    }

    private async Task RunBenchmarkAsync()
    {
        BtnRefreshBenchmark.IsEnabled = false;
        BtnApplyOptimal.IsEnabled = false;
        TxtCdnBoostCurrentState.Text = "正在多线程并发测速亚太 CloudFront 节点...";
        TxtCdnBoostCurrentState.Foreground = (Brush)FindResource("AccentGold");

        try
        {
            _benchmarkNodes = await HostsManager.BenchmarkAllNodesAsync();
            IcNodesList.ItemsSource = null;
            IcNodesList.ItemsSource = _benchmarkNodes;

            UpdateCdnBoostStatusDisplay();
        }
        catch (Exception ex)
        {
            TxtCdnBoostCurrentState.Text = $"测速失败: {ex.Message}";
        }
        finally
        {
            BtnRefreshBenchmark.IsEnabled = true;
            BtnApplyOptimal.IsEnabled = true;
        }
    }

    private void UpdateCdnBoostStatusDisplay()
    {
        if (HostsManager.CheckCurrentBoostStatus(out string currentIp))
        {
            TxtCdnBoostCurrentState.Text = $"已绑定香港直连优选节点 [{currentIp}] · Hosts 加速生效中";
            TxtCdnBoostCurrentState.Foreground = (Brush)FindResource("AccentGreen");
        }
        else if (!string.IsNullOrEmpty(GameServerHttpClientProvider.OptimalCdnIp))
        {
            TxtCdnBoostCurrentState.Text = $"内存直连加速生效中 [{GameServerHttpClientProvider.OptimalCdnIp}] · Hosts 未写入";
            TxtCdnBoostCurrentState.Foreground = (Brush)FindResource("AccentBlue");
        }
        else
        {
            TxtCdnBoostCurrentState.Text = "系统默认 DNS 解析 (未加速，直连可能绕路欧美)";
            TxtCdnBoostCurrentState.Foreground = (Brush)FindResource("TextMuted");
        }
    }

    private async void BtnApplyOptimal_Click(object sender, RoutedEventArgs e)
    {
        if (_benchmarkNodes == null || _benchmarkNodes.Count == 0)
        {
            await RunBenchmarkAsync();
        }

        var optimal = _benchmarkNodes?.FirstOrDefault(n => n.LatencyMs >= 0);
        if (optimal == null)
        {
            await ShowDialogAsync("测速提示", "未检测到可达的亚太 CDN 节点，请检查本地网络连接！", "确定", null);
            return;
        }

        BtnApplyOptimal.IsEnabled = false;
        var (success, msg) = await Task.Run(() => HostsManager.ApplyHostsBoost(optimal.Ip));
        BtnApplyOptimal.IsEnabled = true;

        if (success)
        {
            GameServerHttpClientProvider.OptimalCdnIp = optimal.Ip;
            UpdateCdnBoostStatusDisplay();
            Log($"[CDN 加速] {msg}");
            await ShowDialogAsync("加速成功", $"已成功将海外 CDN (autopatchhk.yuanshen.com)\n解析绑定至最优香港节点:\n\n{optimal.Name} ({optimal.Ip})\n实测握手延时: {optimal.LatencyMs} ms\n\n已为您自动刷新 Windows 本地 DNS 缓存，现在转服将获得极速下载体验！", "太棒了", null);
        }
        else
        {
            await ShowDialogAsync("写入失败", msg, "确定", null);
        }
    }

    private async void BtnRestoreHosts_Click(object sender, RoutedEventArgs e)
    {
        BtnRestoreHosts.IsEnabled = false;
        var (success, msg) = await Task.Run(() => HostsManager.RestoreHosts());
        BtnRestoreHosts.IsEnabled = true;

        if (success)
        {
            GameServerHttpClientProvider.OptimalCdnIp = null;
            UpdateCdnBoostStatusDisplay();
            Log("[CDN 加速] 已恢复系统默认 Hosts 解析。");
            await ShowDialogAsync("已恢复默认", "已从系统 Hosts 中移除 CDN 直连加速规则，\n并刷新了系统 DNS 缓存。", "确定", null);
        }
        else
        {
            await ShowDialogAsync("还原失败", msg, "确定", null);
        }
    }

    // ========================================================
    // 注入设置功能模块 (100% 深度适配千织启动器原生视觉与交互规范)
    // ========================================================

    private void UpdateInjectionSwitchState(bool isChinaClient)
    {
        if (ChkEnableInjection == null) return;

        if (isChinaClient)
        {
            App.Config.UseInjection = false;
            ChkEnableInjection.IsChecked = false;
            ChkEnableInjection.IsEnabled = false;

            if (TxtInjectionSubLabel != null)
            {
                TxtInjectionSubLabel.Text = "🛡️ 国服锁定 (严禁注入)";
                TxtInjectionSubLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x70, 0x43));
            }
            if (BorderInjectionSwitch != null)
            {
                BorderInjectionSwitch.ToolTip = "国服客户端反作弊机制极严，千织启动器已开启最高安全防护，严禁注入任何插件以防封号！";
            }
            if (TxtInjectionStatus != null)
            {
                TxtInjectionStatus.Text = "🛡️ 当前为国服客户端 · 开启最高安全保护 · 严禁注入任何插件（仅国际服支持注入）";
            }
        }
        else
        {
            ChkEnableInjection.IsEnabled = true;
            ChkEnableInjection.IsChecked = App.Config.UseInjection;

            if (TxtInjectionSubLabel != null)
            {
                TxtInjectionSubLabel.Text = "启动游戏时加载插件";
                TxtInjectionSubLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            }
            if (BorderInjectionSwitch != null)
            {
                BorderInjectionSwitch.ToolTip = null;
            }
            if (TxtInjectionStatus != null)
            {
                TxtInjectionStatus.Text = App.Config.UseInjection 
                    ? "⚡ 注入模式已开启 · 启动国际服游戏时将自动注入插件" 
                    : "⚡ 注入模式已关闭 · 纯净补丁模式运行";
            }
        }
    }

    private void InitializeInjectionModule()
    {
        EnsureDefaultPluginsEnvironment();

        var envState = PatchService.CheckEnvironment(App.Config.GameDir);
        bool isChina = (envState == GameEnvironmentState.IsChinaClient);
        UpdateInjectionSwitchState(isChina);

        LoadInjectionSettings();
    }

    private void EnsureDefaultPluginsEnvironment()
    {
        try
        {
            ChioriWorkspace.EnsureInitialized();

            string chioriDir = ChioriWorkspace.ChioriPluginDir;
            string chioriIni = Path.Combine(chioriDir, "config.ini");
            if (!File.Exists(chioriIni))
            {
                Directory.CreateDirectory(chioriDir);
                ExtractEmbeddedResource("DefaultChioriConfig.ini", chioriIni);
            }

            string fpsDir = ChioriWorkspace.FpsPluginDir;
            string fpsIni = Path.Combine(fpsDir, "config.ini");
            if (!File.Exists(fpsIni))
            {
                Directory.CreateDirectory(fpsDir);
                ExtractEmbeddedResource("DefaultFpsConfig.ini", fpsIni);
            }
        }
        catch { }
    }

    private static void ExtractEmbeddedResource(string resourceLogicalName, string targetFilePath)
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceLogicalName);
            if (stream == null) return;
            using var fs = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);
            stream.CopyTo(fs);
        }
        catch { }
    }

    private void TabMainView_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ViewMainPanel.Visibility = Visibility.Visible;
        ViewInjectionPanel.Visibility = Visibility.Collapsed;

        TabMainView.Background = (Brush)FindResource("AccentGold");
        TabMainView.BorderThickness = new Thickness(0);
        TxtTabMainText.Foreground = new SolidColorBrush(Color.FromRgb(0x3E, 0x27, 0x00));
        TxtTabMainText.FontWeight = FontWeights.Bold;

        TabInjectionView.Background = new SolidColorBrush(Color.FromArgb(0x80, 0x0F, 0x15, 0x22));
        TabInjectionView.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x3E, 0x58));
        TabInjectionView.BorderThickness = new Thickness(1);
        TxtTabInjectionText.Foreground = (Brush)FindResource("TextMuted");
        TxtTabInjectionText.FontWeight = FontWeights.SemiBold;
    }

    private void TabInjectionView_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ViewMainPanel.Visibility = Visibility.Collapsed;
        ViewInjectionPanel.Visibility = Visibility.Visible;

        TabInjectionView.Background = (Brush)FindResource("AccentGold");
        TabInjectionView.BorderThickness = new Thickness(0);
        TxtTabInjectionText.Foreground = new SolidColorBrush(Color.FromRgb(0x3E, 0x27, 0x00));
        TxtTabInjectionText.FontWeight = FontWeights.Bold;

        TabMainView.Background = new SolidColorBrush(Color.FromArgb(0x80, 0x0F, 0x15, 0x22));
        TabMainView.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x3E, 0x58));
        TabMainView.BorderThickness = new Thickness(1);
        TxtTabMainText.Foreground = (Brush)FindResource("TextMuted");
        TxtTabMainText.FontWeight = FontWeights.SemiBold;
    }

    // ==========================================
    // 启动器使用教程弹窗
    // ==========================================
    public async Task ShowTutorialDialogAsync()
    {
        string tutorialText =
            "【千织 7.0 专用启动器 v3.0 · 零基础三步上手】\n\n" +
            "⚠ 先看前提（很重要）：\n" +
            "• 本启动器只做【原神 7.0.0】：下载 / 校验 / 互转 / 补丁全部锁死 7.0，绝不会下成 7.1、6.6 等其他版本。\n" +
            "• 专用补丁与插件【只能在国际服使用】，国服严禁注入（否则有封号风险，启动器会自动拦截）。\n\n" +
            "──────────── 第一步：选游戏目录 ────────────\n" +
            "• 点右上角【游戏目录】，选一个【空文件夹】用来装游戏（例如 D:\\Games\\Genshin7.0）。\n" +
            "• 已经下过国服 / 国际服的，就直接选那个已有客户端所在文件夹。\n\n" +
            "──────────── 第二步：弄到 7.0 国际服 ────────────\n" +
            "（二选一，做完你就有一个可用的 7.0 国际服）\n" +
            "• 路线A 全新下载：点【开始下载国服】→ 直连米哈游官方 CDN 下 7.0.0 国服（约 100GB，下完自动校验补全）。\n" +
            "• 然后点【国服/国际服互转】里的转换，把国服一键转成国际服（用启动器内置差异包，离线秒转，不费流量）。\n" +
            "• 路线B 已有国际服：目录里已是 7.0 国际服的话，跳过本步，直接进第三步。\n" +
            "※ 反向的【国际服→国服】才会走 CDN 差分下载，同样只锁 7.0.0。\n\n" +
            "──────────── 第三步：开注入并启动 ────────────\n" +
            "• 顶部切到【注入设置】，勾选【启用注入】，选要加载的插件（千织 / FPS）。\n" +
            "• 回到【启动主页】点【启动游戏】。启动器会：替换 Astrolabe 补丁 → 挂起拉起游戏 → 注入插件。\n" +
            "• 补丁常驻生效，游戏退出后不用还原，下次可直接再启动。\n" +
            "• 游戏内快捷键：【F7】显隐性能悬浮窗、【F11】热重载配置、【INS】自由视角。\n\n" +
            "──────────── 可选：网络加速 ────────────\n" +
            "• 转服 / 连接海外卡顿时，点【国服/国际服互转】卡片上的【⚡ 节点优选】，自动测速选亚太最低延迟节点，一键写入系统 Hosts 加速；用【↺ 恢复默认】可随时撤销。\n\n" +
            "💡 启动没吃到插件？多半是【注入设置】没开、或当前目录不是国际服、或插件未下载完整——按上面三步核对即可。\n\n" +
            "※ 本教程可随时点顶部【📖 使用教程】再次查看。";

        await ShowDialogAsync("千织 7.0 启动器使用教程 v3.0", tutorialText, "我知道了", null);
    }

    private async void BtnOpenTutorial_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        await ShowTutorialDialogAsync();
    }

    private void BtnFilterCategory_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
        {
            UpdateFilterPillStyles(tag);
            ApplyCategoryFilter(tag);
        }
    }

    private void UpdateFilterPillStyles(string activeTag)
    {
        var pills = new (Border? bd, TextBlock? txt, string tag)[]
        {
            (BtnFilterAll, TxtFilterAll, "全部"),
            (BtnFilterHud, TxtFilterHud, "悬浮窗"),
            (BtnFilterGraphics, TxtFilterGraphics, "画质帧率"),
            (BtnFilterCamera, TxtFilterCamera, "视角摄影"),
            (BtnFilterUtility, TxtFilterUtility, "便捷辅助")
        };

        foreach (var p in pills)
        {
            if (p.bd == null || p.txt == null) continue;
            bool isActive = p.tag == activeTag;
            if (isActive)
            {
                p.bd.Background = (Brush)FindResource("AccentGold");
                p.bd.BorderThickness = new Thickness(0);
                p.txt.Foreground = new SolidColorBrush(Color.FromRgb(0x3E, 0x27, 0x00));
                p.txt.FontWeight = FontWeights.Bold;
            }
            else
            {
                p.bd.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x25, 0x38));
                p.bd.BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x49, 0x6B));
                p.bd.BorderThickness = new Thickness(1);
                p.txt.Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                p.txt.FontWeight = FontWeights.SemiBold;
            }
        }
    }

    private void ApplyCategoryFilter(string categoryTag)
    {
        _currentCategoryFilter = categoryTag;
        _settingItems.Clear();

        IEnumerable<PluginSettingItem> filtered = categoryTag switch
        {
            "悬浮窗" => _allSettingItems.Where(i => i.Category == "悬浮窗"),
            "画质帧率" => _allSettingItems.Where(i => i.Category == "画质帧率"),
            "视角摄影" => _allSettingItems.Where(i => i.Category == "视角摄影"),
            "便捷辅助" => _allSettingItems.Where(i => i.Category == "便捷辅助"),
            _ => _allSettingItems // "全部"
        };

        foreach (var item in filtered)
        {
            _settingItems.Add(item);
        }

        IcSettingsGrid.ItemsSource = null;
        IcSettingsGrid.ItemsSource = _settingItems;
    }

    private void BtnViewManual_Click(object sender, RoutedEventArgs e)
    {
        ManualDialogOverlay.Visibility = Visibility.Visible;
    }

    private void BtnCloseManualDialog_Click(object sender, RoutedEventArgs e)
    {
        ManualDialogOverlay.Visibility = Visibility.Collapsed;
    }

    private void LoadInjectionSettings(string? targetPlugin = null)
    {
        string chioriIniPath = Path.Combine(ChioriWorkspace.ChioriPluginDir, "config.ini");
        string fpsIniPath = Path.Combine(ChioriWorkspace.FpsPluginDir, "config.ini");
        string presetsDir = ChioriWorkspace.PresetsDir;
        string targetDll = InjectionService.ResolveTargetPluginDll("ChioriPlugin") ?? string.Empty;

        _chioriIni = new IniFile(chioriIniPath);
        _fpsIni = new IniFile(fpsIniPath);
        _currentIni = _chioriIni;

        _presetService = new PluginPresetService(presetsDir, _chioriIni, targetDll);
        _presetService.PresetsChanged += () =>
        {
            Dispatcher.Invoke(() =>
            {
                if (TxtCurrentPresetLabel != null)
                {
                    TxtCurrentPresetLabel.Text = _presetService?.CurrentPreset?.Name ?? "默认预设";
                }
                if (IcPresetsList != null)
                {
                    IcPresetsList.ItemsSource = null;
                    IcPresetsList.ItemsSource = _presetService?.AvailablePresets;
                }
            });
        };

        _presetService.LoadPresets();

        // 刷新头部信息
        TxtPluginName.Text = "千织注入增强与性能监控系统";
        TxtPluginDesc.Text = "全模块聚合设置 · 画质/广角/自由视角/性能悬浮窗HUD";
        TxtPluginAuthor.Text = "千织 7.0 专用";
        TxtPluginDate.Text = "默认纯净全关";

        _allSettingItems.Clear();

        // ========================================================
        // 1. 加载【性能监控悬浮窗 HUD】全部模块
        // ========================================================
        bool isFpsEnabled = ChioriWorkspace.IsFpsPluginEnabled;
        var masterFpsItem = new PluginSettingItem(
            "MasterFpsHud",
            "启用性能监控悬浮窗 (HUD)",
            "bool",
            isFpsEnabled ? "1" : "0",
            "",
            (sec, key, newVal) =>
            {
                bool enabled = newVal == "1" || string.Equals(newVal, "true", StringComparison.OrdinalIgnoreCase);
                ChioriWorkspace.SetFpsPluginEnabled(enabled);
                App.Config.EnableFpsHud = enabled;
                App.Config.Save();
                Log($"[悬浮窗] 用户切换悬浮窗主开关: {(enabled ? "启用" : "禁用")}");
            },
            category: "悬浮窗",
            targetPlugin: "FPS"
        );
        _allSettingItems.Add(masterFpsItem);

        var fpsIniData = _fpsIni.ReadAll();
        foreach (var sec in fpsIniData)
        {
            if (sec.Key.Equals("General", StringComparison.OrdinalIgnoreCase)) continue;
            var dic = sec.Value;
            string name = dic.GetValueOrDefault("Name", sec.Key);
            string type = dic.GetValueOrDefault("Type", "string");
            string val = dic.GetValueOrDefault("Value", "");
            string help = dic.GetValueOrDefault("help", "");

            var item = new PluginSettingItem(
                sec.Key,
                name,
                type,
                val,
                help,
                (section, key, newVal) =>
                {
                    _fpsIni.WriteValue(section, key, newVal);
                    Log($"[悬浮窗设置] 已更新 {name} = {newVal}");

                    if ((newVal == "1" || string.Equals(newVal, "true", StringComparison.OrdinalIgnoreCase)) &&
                        !ChioriWorkspace.IsFpsPluginEnabled)
                    {
                        ChioriWorkspace.SetFpsPluginEnabled(true);
                        App.Config.EnableFpsHud = true;
                        App.Config.Save();
                        masterFpsItem.BoolValue = true;
                        Log("[悬浮窗设置] 检测到子项开启，已自动联动激活悬浮窗主开关。");
                    }
                },
                category: "悬浮窗",
                targetPlugin: "FPS"
            );
            _allSettingItems.Add(item);
        }

        // ========================================================
        // 2. 加载【千织核心增强】全部模块 (40+ 项)
        // ========================================================
        var chioriIniData = _chioriIni.ReadAll();
        foreach (var sec in chioriIniData)
        {
            if (sec.Key.Equals("General", StringComparison.OrdinalIgnoreCase)) continue;
            if (sec.Key.Equals("DEV", StringComparison.OrdinalIgnoreCase)) continue;

            var dic = sec.Value;
            string name = dic.GetValueOrDefault("Name", sec.Key);
            string type = dic.GetValueOrDefault("Type", "string");
            string val = dic.GetValueOrDefault("Value", "");
            string help = dic.GetValueOrDefault("help", "");

            string category = "便捷辅助";
            string k = sec.Key;
            if (k.Equals("FpsUnlock", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("TargetFps", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("VSync", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("LowRenderScale", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("RenderScaleValue", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("DisableFog", StringComparison.OrdinalIgnoreCase))
            {
                category = "画质帧率";
            }
            else if (k.StartsWith("FreeCam", StringComparison.OrdinalIgnoreCase) ||
                     k.StartsWith("Camera", StringComparison.OrdinalIgnoreCase) ||
                     k.Equals("EnableFreeCam", StringComparison.OrdinalIgnoreCase) ||
                     k.Equals("EnableCameraOffset", StringComparison.OrdinalIgnoreCase) ||
                     k.Equals("DisableCameraMove", StringComparison.OrdinalIgnoreCase) ||
                     k.Equals("FovUnlock", StringComparison.OrdinalIgnoreCase) ||
                     k.Equals("FovValue", StringComparison.OrdinalIgnoreCase) ||
                     k.Equals("FovLimitCheck", StringComparison.OrdinalIgnoreCase))
            {
                category = "视角摄影";
            }

            var item = new PluginSettingItem(
                sec.Key,
                name,
                type,
                val,
                help,
                (section, key, newVal) =>
                {
                    _chioriIni.WriteValue(section, key, newVal);
                    _presetService.UpdateCurrentPresetValue(section, key, newVal);
                    Log($"[注入设置] 已更新 {name} = {newVal}");

                    if (string.Equals(section, "FpsUnlock", StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(newVal, "1") || string.Equals(newVal, "true", StringComparison.OrdinalIgnoreCase)))
                    {
                        Dispatcher.Invoke(async () =>
                        {
                            var vsyncVal = _chioriIni.Read("VSync", "Value");
                            bool vsyncDisabled = string.Equals(vsyncVal, "1") || string.Equals(vsyncVal, "true", StringComparison.OrdinalIgnoreCase);
                            if (!vsyncDisabled)
                            {
                                var autoEnable = await ShowDialogAsync(
                                    "💡 温馨提示（关于垂直同步）",
                                    "在“注入设置”中，如果开启了【解锁帧率限制】（如 120 帧），建议同时勾选开启【禁用垂直同步】。\n\n若您的显示器为 60Hz 且未禁用垂直同步，游戏会被显卡强制同步在显示器刷新率上限（60 帧）。开启【禁用垂直同步】后即可突破该限制！\n\n是否立即为您自动开启【禁用垂直同步】？",
                                    "立即开启禁用垂直同步",
                                    "保持当前设置");

                                if (autoEnable)
                                {
                                    _chioriIni.WriteValue("VSync", "Value", "1");
                                    _presetService.UpdateCurrentPresetValue("VSync", "Value", "1");
                                    var vsyncItem = _allSettingItems.FirstOrDefault(i => string.Equals(i.SectionKey, "VSync", StringComparison.OrdinalIgnoreCase));
                                    if (vsyncItem != null)
                                    {
                                        vsyncItem.BoolValue = true;
                                    }
                                    Log("[注入设置] 已响应用户确认，自动开启【禁用垂直同步】。");
                                }
                            }
                        });
                    }
                },
                category: category,
                targetPlugin: "ChioriPlugin"
            );
            _allSettingItems.Add(item);
        }

        ApplyCategoryFilter(_currentCategoryFilter);
    }

    private async void ChkEnableInjection_Click(object sender, RoutedEventArgs e)
    {
        var envState = PatchService.CheckEnvironment(App.Config.GameDir);
        if (envState == GameEnvironmentState.IsChinaClient && ChkEnableInjection.IsChecked == true)
        {
            // 国服绝对严禁开启注入：强行撤销勾选并弹窗警告阻断
            UpdateInjectionSwitchState(isChinaClient: true);
            App.Config.Save();

            Log("[安全拦截] 检测到国服客户端，已强行拦截注入开启请求！");

            var switchNow = await ShowDialogAsync(
                "🛡️ 安全警告：国服严禁注入插件",
                "原神国服（官方服务器）反作弊机制极度严苛，向国服进程注入任何插件将直接面临封号风险！\n\n【千织注入功能】遵循最高安全准则，绝不允许在国服运行，仅支持在【国际服】客户端下使用。\n\n请问是否立即将客户端转换为【国际服】？",
                "一键转换为国际服",
                "保持纯净国服");

            if (switchNow)
            {
                BtnConvert_Click(sender, e);
            }
            return;
        }

        bool isEnabled = ChkEnableInjection.IsChecked == true;
        App.Config.UseInjection = isEnabled;
        App.Config.Save();

        UpdateInjectionSwitchState(isChinaClient: false);
        Log($"[注入服务] 用户切换注入开关: {(isEnabled ? "启用" : "禁用")}");
    }

    private void PresetItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PresetModel preset)
        {
            _presetService?.SwitchPreset(preset);
            if (!string.IsNullOrEmpty(App.Config.SelectedPlugin))
            {
                LoadInjectionSettings(App.Config.SelectedPlugin);
            }
            Log($"[注入设置] 已切换至预设: {preset.Name}");
        }
    }

    private async void BtnNewPreset_Click(object sender, RoutedEventArgs e)
    {
        int nextNum = (_presetService?.AvailablePresets.Count ?? 0) + 1;
        var p = _presetService?.CreateNewPreset($"预设 {nextNum}");
        if (p != null)
        {
            Log($"[注入设置] 已新建预设: {p.Name}");
            await ShowDialogAsync("新建预设", $"已成功创建并应用新预设 [{p.Name}]！", "确定", null);
        }
    }

    private async void BtnDeletePreset_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is PresetModel preset)
        {
            if (_presetService?.AvailablePresets.Count <= 1)
            {
                await ShowDialogAsync("提示", "至少需要保留一个配置预设，无法删除！", "确定", null);
                return;
            }

            bool confirm = await ShowDialogAsync("删除预设", $"确定要删除预设 [{preset.Name}] 吗？", "删除", "取消");
            if (confirm)
            {
                _presetService?.DeletePreset(preset);
                if (!string.IsNullOrEmpty(App.Config.SelectedPlugin))
                {
                    LoadInjectionSettings(App.Config.SelectedPlugin);
                }
                Log($"[注入设置] 已删除预设: {preset.Name}");
            }
        }
    }

    private async void BtnResetAllPresets_Click(object sender, RoutedEventArgs e)
    {
        bool confirm = await ShowDialogAsync("重置全部预设", "确定要清空所有自定义预设并恢复为初始默认预设吗？", "确定重置", "取消");
        if (confirm)
        {
            _presetService?.ResetAllPresets();
            if (!string.IsNullOrEmpty(App.Config.SelectedPlugin))
            {
                LoadInjectionSettings(App.Config.SelectedPlugin);
            }
            Log("[注入设置] 已重置所有预设为默认初始配置。");
        }
    }

    private void BtnFunctionsMenu_Click(object sender, RoutedEventArgs e)
    {
        var cm = new ContextMenu();
        
        var miDownload = new MenuItem { Header = "⬇ 下载 / 更新千织专属插件 (ChioriPlugin)" };
        miDownload.Click += (s, args) => StartPluginDownload();

        var miRepair = new MenuItem { Header = "🔧 修复 / 解压 FPS 插件" };
        miRepair.Click += async (s, args) =>
        {
            var (ok, msg) = PluginDownloadService.RepairFpsPlugin();
            Log($"[插件维护] {msg}");
            await ShowDialogAsync(ok ? "操作成功" : "操作失败", msg, "确定", null);
        };

        cm.Items.Add(miDownload);
        cm.Items.Add(miRepair);
        cm.PlacementTarget = BtnFunctionsMenu;
        cm.IsOpen = true;
    }

    private async void StartPluginDownload()
    {
        DownloadDialogOverlay.Visibility = Visibility.Visible;
        BtnCancelPluginDownload.Visibility = Visibility.Visible;
        BtnConfirmPluginDownload.IsEnabled = false;
        PbPluginDownload.Value = 0;
        TxtDownloadDialogStatus.Text = "正在初始化下载线路...";

        _pluginDownloadCts = new CancellationTokenSource();
        var downloader = new PluginDownloadService();
        downloader.ProgressChanged += (pct, status) =>
        {
            Dispatcher.Invoke(() =>
            {
                PbPluginDownload.Value = pct;
                TxtDownloadDialogStatus.Text = status;
            });
        };

        var (ok, msg) = await Task.Run(() => downloader.DownloadAndInstallPluginAsync(_pluginDownloadCts.Token));

        BtnCancelPluginDownload.Visibility = Visibility.Collapsed;
        BtnConfirmPluginDownload.IsEnabled = true;
        TxtDownloadDialogStatus.Text = msg;

        if (ok)
        {
            Log($"[插件下载] {msg}");
            LoadInjectionSettings(App.Config.SelectedPlugin ?? "ChioriPlugin");
        }
    }

    private void BtnCancelPluginDownload_Click(object sender, RoutedEventArgs e)
    {
        _pluginDownloadCts?.Cancel();
        DownloadDialogOverlay.Visibility = Visibility.Collapsed;
    }

    private void BtnConfirmPluginDownload_Click(object sender, RoutedEventArgs e)
    {
        DownloadDialogOverlay.Visibility = Visibility.Collapsed;
    }

    private void BtnSettingHelp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PluginSettingItem item && item.HasHelp)
        {
            TxtHelpDialogTitle.Text = $"{item.DisplayName} - 效果演示";
            string resolvedPath = item.HelpUrl;
            if (!Path.IsPathRooted(resolvedPath))
            {
                resolvedPath = Path.Combine(AppContext.BaseDirectory, item.HelpUrl);
            }

            if (File.Exists(resolvedPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(resolvedPath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    ImgHelpPreview.Source = bmp;
                }
                catch { ImgHelpPreview.Source = null; }
            }
            else
            {
                ImgHelpPreview.Source = null;
            }

            HelpDialogOverlay.Visibility = Visibility.Visible;
        }
    }

    private void BtnCloseHelpDialog_Click(object sender, RoutedEventArgs e)
    {
        HelpDialogOverlay.Visibility = Visibility.Collapsed;
        ImgHelpPreview.Source = null;
    }

    private async Task PerformPluginInjectionAsync(string processName)
    {
        // 终极安全熔断 1：坚决拦截国服进程名
        if (string.Equals(processName, "YuanShen", StringComparison.OrdinalIgnoreCase) || processName.Contains("YuanShen", StringComparison.OrdinalIgnoreCase))
        {
            Log("[注入引擎] ⚠️ 终极物理拦截：检测到国服进程 YuanShen，严禁注入任何插件！");
            return;
        }

        // 终极安全熔断 2：坚决拦截当前配置环境为国服
        var envState = PatchService.CheckEnvironment(App.Config.GameDir);
        if (envState == GameEnvironmentState.IsChinaClient)
        {
            Log("[注入引擎] ⚠️ 终极物理拦截：当前游戏环境为国服客户端，严禁注入任何插件！");
            return;
        }

        Log($"[注入引擎] 正在后台等待游戏进程 {processName}.exe 启动...");
        Process? gameProc = null;

        for (int i = 0; i < 30; i++)
        {
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length > 0)
            {
                gameProc = procs[0];
                break;
            }
            await Task.Delay(500);
        }

        if (gameProc == null)
        {
            Log($"[注入引擎] 等待超时，未捕获到 {processName}.exe 进程。");
            return;
        }

        // 终极安全熔断 3：获取到进程后，三重核查目标进程是否为国服
        try
        {
            string pName = gameProc.ProcessName;
            string? mPath = null;
            try { mPath = gameProc.MainModule?.FileName; } catch { }

            if (pName.Contains("YuanShen", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(mPath) && (mPath.Contains("YuanShen", StringComparison.OrdinalIgnoreCase) || mPath.Contains("YuanShen_Data", StringComparison.OrdinalIgnoreCase))))
            {
                Log("[注入引擎] ⚠️ 终极物理拦截：捕获到的进程被确认为原神国服，已紧急阻断注入！");
                return;
            }
        }
        catch { }

        // 稍微等待进程内部模块初始化（约 2 秒）
        await Task.Delay(2000);

        string targetPlugin = App.Config.SelectedPlugin ?? "ChioriPlugin";
        string? dllPath = InjectionService.ResolveTargetPluginDll(targetPlugin);

        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
        {
            Log($"[注入引擎] ⚠️ 未在 Plugins\\{targetPlugin} 找到可用 DLL，跳过注入。您可通过【功能】菜单进行下载。");
            return;
        }

        Log($"[注入引擎] 正在向游戏进程 (PID={gameProc.Id}) 注入插件: {Path.GetFileName(dllPath)}...");
        var (ok, msg) = InjectionService.InjectDll(gameProc.Id, dllPath);

        if (ok)
        {
            Log($"[注入引擎] ★ 成功: {msg}");
            Dispatcher.Invoke(() =>
            {
                TxtInjectionStatus.Text = $"⚡ 插件已成功注入 (PID={gameProc.Id}) · 功能生效中";
            });
        }
        else
        {
            Log($"[注入引擎] ⚠️ 注入失败: {msg}");
            Dispatcher.Invoke(() =>
            {
                TxtInjectionStatus.Text = $"⚠️ 注入失败: {msg}";
            });
        }
    }
}
