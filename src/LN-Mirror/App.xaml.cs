using System.Windows;
using System.Windows.Threading;
using LNMirror.Core;
using LNMirror.Core.Configuration;
using LNMirror.Core.Diagnostics;
using LNMirror.Views;

namespace LNMirror;

public partial class App : Application
{
    private AppController? _controller;
    private MainWindow? _mainWindow;
    private ToolbarWindow? _toolbarWindow;
    private TrayIconService? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("[应用] 未处理的异常", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "未知"));

        var args = e.Args ?? Array.Empty<string>();

        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            MessageBox.Show($"LN-Mirror {version}", "LN-Mirror", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            AppLog.Initialize(SettingsStore.LogDirectory);

            int reportIndex = Array.FindIndex(args, a => a.Equals("--report", StringComparison.OrdinalIgnoreCase));
            if (reportIndex >= 0)
            {
                // 无界面模式：用于交付前自动核对，也方便在不方便看窗口时留下结果
                var path = reportIndex + 1 < args.Length
                    ? args[reportIndex + 1]
                    : Path.Combine(SettingsStore.LogDirectory, "selftest.txt");

                var report = await SelfTest.RunAsync();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                await File.WriteAllTextAsync(path, report);
                AppLog.Info($"[自检] 报告已写入 {path}");
                Shutdown();
                return;
            }

            var text = await SelfTest.RunAsync();
            MessageBox.Show(text, "LN-Mirror 环境自检", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _controller = new AppController(SettingsStore.Load(), Dispatcher);

        _tray = new TrayIconService(
            _controller,
            showMainWindow: () => Dispatcher.Invoke(ShowMainWindow),
            showToolbar: () => Dispatcher.Invoke(ShowToolbar),
            exit: () => Dispatcher.Invoke(ExitApplication));

        _toolbarWindow = new ToolbarWindow(_controller, _tray);
        _mainWindow = new MainWindow(_controller, _tray);

        await _controller.InitializeAsync();

        _mainWindow.Show();
        _mainWindow.ApplyPhaseVisual();

        if (args.Any(a => a.Equals("--smoke-ui", StringComparison.OrdinalIgnoreCase)))
        {
            // 界面冒烟检查：窗口真实创建并显示若干秒，确认没有启动期异常后自动退出
            AppLog.Info("[冒烟] 界面已显示，3 秒后自动退出");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                AppLog.Info("[冒烟] 界面冒烟检查通过：主窗口与工具条创建正常，退出码 0");
                ExitApplication();
            };
            timer.Start();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.ApplyPhaseVisual();
    }

    private void ShowToolbar()
    {
        if (_toolbarWindow is null || _controller is null) return;
        if (!_controller.IsConnected) return;
        _toolbarWindow.ShowAbove();
    }

    private void ExitApplication()
    {
        _mainWindow?.AllowClose();
        _tray?.Dispose();
        _controller?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _controller?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("[应用] 界面线程未处理的异常", e.Exception);
        MessageBox.Show(
            $"发生了未预期的错误：\n\n{e.Exception.Message}\n\n详细信息已写入日志目录。",
            "LN-Mirror", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}