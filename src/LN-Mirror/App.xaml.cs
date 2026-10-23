using System.Windows;
using System.Windows.Media;
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
    private MirrorFrameOverlayWindow? _frameOverlay;
    private TrayIconService? _tray;
    private bool _exitRequested;
    private System.Threading.Timer? _exitWatchdog;

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

        var smoke = args.Any(a => a.Equals("--smoke-ui", StringComparison.OrdinalIgnoreCase));
        if (smoke)
        {
            // 冒烟检查只验证界面能不能起来：不连手机、不写回配置，
            // 免得和正在运行的另一个实例抢会话或互相覆盖设置
            SettingsStore.ReadOnly = true;
            _controller.AllowAutoReconnect = false;
        }

        _tray = new TrayIconService(
            _controller,
            showMainWindow: () => PostToUi(ShowMainWindow),
            showToolbar: () => PostToUi(ShowToolbar),
            exit: () => RequestExit());

        _toolbarWindow = new ToolbarWindow(_controller, _tray);
        _frameOverlay = new MirrorFrameOverlayWindow(_controller);
        _mainWindow = new MainWindow(_controller, _tray);

        await _controller.InitializeAsync();

        _mainWindow.Show();
        _mainWindow.ApplyPhaseVisual();

        if (smoke)
        {
            // 冒烟检查的窗口固定放在主屏，免得跑到另一块屏幕上打扰正在做的事
            PlaceOnPrimaryScreen(_mainWindow);

            // 工具条也露一下脸，确认它能正常创建和排版
            _toolbarWindow.KeepVisibleWithoutPicture = true;
            _toolbarWindow.ShowAbove();
            PlaceOnPrimaryScreen(_toolbarWindow, alignRight: true);
        }

        if (smoke)
        {
            // 界面冒烟检查：窗口真实创建并显示若干秒，确认没有启动期异常后自动退出。
            // 退出走的是和托盘"退出"完全同一条路径，顺带把退出流程也验一遍。
            AppLog.Info("[冒烟] 界面已显示，3 秒后自动退出");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                AppLog.Info("[冒烟] 界面冒烟检查通过：主窗口与工具条创建正常，退出码 0");
                RequestExit();
            };
            timer.Start();
        }

        // 自动退出：跑起来 N 秒后走和托盘"退出"同一条路径退出。
        // 用来验证"连着手机时退出"这种真实场景，不用人去点托盘。
        int exitAfterIndex = Array.FindIndex(args, a => a.Equals("--exit-after", StringComparison.OrdinalIgnoreCase));
        if (exitAfterIndex >= 0
            && exitAfterIndex + 1 < args.Length
            && int.TryParse(args[exitAfterIndex + 1], out int exitAfterSeconds)
            && exitAfterSeconds > 0)
        {
            AppLog.Info($"[应用] {exitAfterSeconds} 秒后自动退出");
            var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(exitAfterSeconds) };
            exitTimer.Tick += (_, _) =>
            {
                exitTimer.Stop();
                RequestExit();
            };
            exitTimer.Start();
        }
    }

    /// <summary>把窗口挪到主显示器的工作区，免得出现在另一块屏幕上。</summary>
    private static void PlaceOnPrimaryScreen(Window window, bool alignRight = false)
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
        if (area is null) return;

        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;
        double sx = transform.M11 == 0 ? 1 : transform.M11;
        double sy = transform.M22 == 0 ? 1 : transform.M22;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = alignRight
            ? area.Value.Right / sx - window.Width - 40
            : area.Value.Left / sx + 40;
        window.Top = area.Value.Top / sy + 40;
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

    /// <summary>
    /// 投递到界面线程执行。
    /// 托盘菜单的点击回调跑在 WinForms 的嵌套菜单循环里，
    /// 那里同步等调度器会把调用方一起卡住，所以只投递不等待。
    /// </summary>
    private void PostToUi(Action action)
    {
        try
        {
            Dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[应用] 投递界面动作失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 收到退出请求。
    /// 关键点：这里只把退出动作投递出去，绝不在托盘菜单的回调里同步拆托盘。
    /// 那样会让 Win32 停在"图标已经没了、进程还活着"的僵死状态。
    /// </summary>
    private void RequestExit()
    {
        if (_exitRequested) return;
        _exitRequested = true;

        AppLog.Info("[应用] 收到退出请求");
        StartExitWatchdog();

        try
        {
            Dispatcher.BeginInvoke(new Action(ExitApplication));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[应用] 投递退出动作失败：{ex.Message}");
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// 退出兜底：正常退出流程卡住时（比如某个窗口没关掉），到点就强制结束进程，
    /// 不留下"图标没了但进程还在"的僵尸。
    /// </summary>
    private void StartExitWatchdog()
    {
        _exitWatchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                AppLog.Warn("[应用] 退出超时（10 秒），强制结束进程");
                _controller?.ForceStopSession();
            }
            catch
            {
                // 兜底逻辑本身不能再抛
            }

            Environment.Exit(0);
        }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private void ExitApplication()
    {
        AppLog.Info("[应用] 开始退出清理");

        try
        {
            _mainWindow?.AllowClose();
            _toolbarWindow?.Close();
            _frameOverlay?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[应用] 关闭窗口时出错：{ex.Message}");
        }

        try
        {
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[应用] 释放托盘图标时出错：{ex.Message}");
        }

        try
        {
            _controller?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[应用] 退出清理时出错：{ex.Message}");
        }

        AppLog.Info("[应用] 清理完成，请求结束进程");

        try
        {
            Shutdown();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[应用] 结束进程时出错：{ex.Message}");
            Environment.Exit(0);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _controller?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _exitWatchdog?.Dispose();
        _exitWatchdog = null;

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("[应用] 界面线程未处理的异常", e.Exception);

        // 退出过程中再弹模态框会把进程卡住（框可能还藏在别的窗口后面，看着就像"退不掉"）。
        // 这时只写日志，让退出流程走完。
        if (_exitRequested)
        {
            e.Handled = true;
            return;
        }

        MessageBox.Show(
            $"发生了未预期的错误：\n\n{e.Exception.Message}\n\n详细信息已写入日志目录。",
            "LN-Mirror", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}