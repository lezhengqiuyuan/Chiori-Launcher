using System.Windows;
using QzLauncher.Services;

namespace QzLauncher;

public partial class App : Application
{
    public static LauncherConfig Config { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                MessageBox.Show($"启动器遇到未处理异常：\n\n{ex.Message}\n\n详细堆栈：\n{ex.StackTrace}", "千织启动器严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"启动器发生界面异常：\n\n{args.Exception.Message}\n\n详细堆栈：\n{args.Exception.StackTrace}", "千织启动器错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Config = LauncherConfig.Load();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 补丁常驻模式：退出时不还原 Astrolabe.dll
        base.OnExit(e);
    }
}
