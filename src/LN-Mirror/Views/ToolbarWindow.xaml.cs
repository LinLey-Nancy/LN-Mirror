using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LNMirror.Core;
using LNMirror.Core.Diagnostics;
using LNMirror.Core.Interop;
using LNMirror.Core.Windowing;

namespace LNMirror.Views;

/// <summary>
/// 独立控制工具条：不透明度与鼠标穿透始终在这里可调，
/// 画面为 0% 时工具条本身也不会消失。
/// </summary>
public partial class ToolbarWindow : Window
{
    private readonly AppController _controller;
    private readonly TrayIconService _tray;
    private readonly DispatcherTimer _followTimer;
    private bool _dragging;

    public ToolbarWindow(AppController controller, TrayIconService tray)
    {
        _controller = controller;
        _tray = tray;
        InitializeComponent();
        DataContext = controller;

        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _followTimer.Tick += OnFollowTimerTick;

        _controller.PropertyChanged += OnControllerPropertyChanged;
        Loaded += (_, _) => _followTimer.Start();
        Closed += (_, _) =>
        {
            _followTimer.Stop();
            _controller.PropertyChanged -= OnControllerPropertyChanged;
        };
    }

    public void ShowAbove()
    {
        UpdatePosition();
        if (!IsVisible) Show();
        Activate();
    }

    public void HideToolbar() => Hide();

    private void OnFollowTimerTick(object? sender, EventArgs e) => Tick();

    private void Tick()
    {
        ConnectionTag.Text = _controller.IsConnected
            ? $"已连接 · {_controller.Opacity}%"
            : "未连接";

        if (!IsVisible) return;

        var rect = _controller.Window.GetRect();
        if (rect is null)
        {
            Hide();
            return;
        }

        bool minimized = _controller.Window.IsMinimized;
        if (minimized != _controller.MirrorMinimized)
        {
            _controller.MirrorMinimized = minimized;
            RecoverButton.Visibility = minimized ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_controller.Settings.ToolbarFollowsMirror && !_dragging)
        {
            UpdatePosition();
        }
    }

    private void UpdatePosition()
    {
        var rect = _controller.Window.GetRect();
        if (rect is null || ActualWidth <= 0 || ActualHeight <= 0) return;

        var work = GetWorkArea();
        int x = rect.Value.CenterX - (int)(ActualWidth / 2);
        int y = rect.Value.Top - (int)ActualHeight - 8;

        if (y < work.Top) y = rect.Value.Bottom + 8;
        x = Math.Max(work.Left, Math.Min(x, work.Right - (int)ActualWidth));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - (int)ActualHeight));

        Left = x;
        Top = y;
    }

    private static WindowRect GetWorkArea()
    {
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return new WindowRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        }

        return new WindowRect(
            (int)SystemParameters.WorkArea.Left,
            (int)SystemParameters.WorkArea.Top,
            (int)SystemParameters.WorkArea.Right,
            (int)SystemParameters.WorkArea.Bottom);
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppController.ToolbarVisible)) return;

        Dispatcher.Invoke(() =>
        {
            if (_controller.ToolbarVisible)
            {
                UpdatePosition();
                if (!IsVisible) Show();
            }
            else if (IsVisible)
            {
                Hide();
            }
        });
    }

    private void OnPassThroughClick(object sender, RoutedEventArgs e)
        => _ = _controller.TogglePassThroughAsync();

    private void OnTopmostClick(object sender, RoutedEventArgs e)
        => _ = _controller.SetTopmostAsync(((System.Windows.Controls.Primitives.ToggleButton)sender).IsChecked == true);

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        await _controller.RestoreAsync();
        UpdatePosition();
    }

    private async void OnRecoverClick(object sender, RoutedEventArgs e)
    {
        await _controller.EnsureWindowRecoverableAsync();
        UpdatePosition();
    }

    private void OnShowMainWindowClick(object sender, RoutedEventArgs e) => _tray.ShowMainWindow();

    private void OnHideClick(object sender, RoutedEventArgs e) => HideToolbar();

    /// <summary>拖动工具条把手时同步移动画面窗口，避免两个窗口错位。</summary>
    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        var rect = _controller.Window.GetRect();
        double startLeft = Left;
        double startTop = Top;
        _dragging = true;

        try
        {
            DragMove();
        }
        finally
        {
            _dragging = false;
        }

        if (rect is null) return;

        int dx = (int)Math.Round(Left - startLeft);
        int dy = (int)Math.Round(Top - startTop);
        if (dx == 0 && dy == 0) return;

        _controller.Window.MoveTo(rect.Value.Left + dx, rect.Value.Top + dy);
        _controller.SavePlacement();
    }
}