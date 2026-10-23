using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LNMirror.Core;
using LNMirror.Core.Diagnostics;
using LNMirror.Core.Interop;
using LNMirror.Core.Windowing;

namespace LNMirror.Views;

/// <summary>
/// 独立控制工具条：不透明度与鼠标穿透始终在这里可调，
/// 画面为 0% 时工具条本身也不会消失。
/// 跟随画面时紧贴画面摆放；不跟随时工具条与画面各自独立移动。
/// </summary>
public partial class ToolbarWindow : Window
{
    /// <summary>卡片内边距，用来让可见的卡片边贴住画面边缘。</summary>
    private const double CardInset = 8;

    private readonly AppController _controller;
    private readonly TrayIconService _tray;
    private readonly DispatcherTimer _followTimer;
    private bool _dragging;
    private bool _lastFollowMode;
    private bool _followModeKnown;

    public ToolbarWindow(AppController controller, TrayIconService tray)
    {
        _controller = controller;
        _tray = tray;
        InitializeComponent();
        DataContext = controller;

        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _followTimer.Tick += OnFollowTimerTick;

        _controller.PropertyChanged += OnControllerPropertyChanged;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _followTimer.Stop();
            _controller.PropertyChanged -= OnControllerPropertyChanged;
        };
    }

    public void ShowAbove()
    {
        if (!IsVisible)
        {
            Show();
            PositionOnShow();
        }
        else
        {
            PositionOnShow();
        }

        Activate();
    }

    public void HideToolbar() => Hide();

    /// <summary>画面不在时是否仍然留着工具条。冒烟检查用得上，正常运行时画面没了就一起收走。</summary>
    public bool KeepVisibleWithoutPicture { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _followTimer.Start();
        PositionOnShow();
    }

    private void OnFollowTimerTick(object? sender, EventArgs e) => Tick();

    private void Tick()
    {
        ConnectionTagText.Text = _controller.IsConnected
            ? $"已连接 · {_controller.OpacityStateText}"
            : "未连接";

        ApplyTopmostState();
        UpdateDragHints();

        if (!IsVisible) return;

        var rect = _controller.Window.GetContentRect();
        if (rect is null)
        {
            if (!KeepVisibleWithoutPicture) Hide();
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

    /// <summary>工具条置顶跟着画面走：画面不置顶时，工具条也不浮在最上层。</summary>
    private void ApplyTopmostState()
    {
        bool want = _controller.Topmost;
        if (Topmost == want) return;
        Topmost = want;
    }

    private void UpdateDragHints()
    {
        bool follows = _controller.Settings.ToolbarFollowsMirror;
        if (_followModeKnown && follows == _lastFollowMode) return;

        _followModeKnown = true;
        _lastFollowMode = follows;
        string hint = follows
            ? "按住这里可以整体移动（工具条与画面一起）"
            : "按住这里可以移动工具条";
        DragHandle.ToolTip = hint;
        ConnectionTag.ToolTip = hint;
    }

    /// <summary>工具条第一次出现时的落位：跟随画面就贴着画面，不跟随就用上次自己拖的位置。</summary>
    private void PositionOnShow()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        if (!_controller.Settings.ToolbarFollowsMirror
            && _controller.Settings.ToolbarPlacement is { HasValue: true } saved)
        {
            Left = saved.X;
            Top = saved.Y;
            ClampIntoWorkArea();
            return;
        }

        UpdatePosition();
    }

    /// <summary>紧贴画面：卡片下边缘压住画面上边缘，上方放不下就贴到画面下边缘。</summary>
    private void UpdatePosition()
    {
        // 画面最小化时窗口位置会跑到屏幕外，这时不能让工具条跟着走，
        // 否则"找回画面"按钮也跟着找不到了
        if (_controller.Window.IsMinimized) return;

        var rect = _controller.Window.GetContentRect();
        if (rect is null || ActualWidth <= 0 || ActualHeight <= 0) return;

        var (sx, sy) = GetScale();

        // 画面矩形是物理像素，窗口位置要用 DIP
        double left = rect.Value.Left / sx;
        double top = rect.Value.Top / sy;
        double right = rect.Value.Right / sx;
        double bottom = rect.Value.Bottom / sy;

        double width = ActualWidth;
        double height = ActualHeight;

        var work = GetWorkArea(rect.Value, sx, sy);

        double x = (left + right) / 2 - width / 2;
        double y = top - height + CardInset;

        if (y < work.Top) y = bottom - CardInset;

        // 保证工具条完整落在工作区里
        if (work.Width > width) x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        else x = work.Left;

        if (work.Height > height) y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        else y = work.Top;

        Left = x;
        Top = y;
    }

    /// <summary>把工具条拉回可见范围，防止拖到屏幕外找不回来。</summary>
    private void ClampIntoWorkArea()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var (sx, sy) = GetScale();
        int centerX = (int)((Left + ActualWidth / 2) * sx);
        int centerY = (int)((Top + ActualHeight / 2) * sy);
        var work = GetWorkArea(new WindowRect(centerX, centerY, centerX + 1, centerY + 1), sx, sy);

        if (work.Width > ActualWidth) Left = Math.Max(work.Left, Math.Min(Left, work.Right - ActualWidth));
        else Left = work.Left;

        if (work.Height > ActualHeight) Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - ActualHeight));
        else Top = work.Top;
    }

    /// <summary>画面所在显示器的工作区（换算成 DIP）。画面在第二块屏幕时不会把工具条拽回主屏。</summary>
    private static Rect GetWorkArea(WindowRect picture, double sx, double sy)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = picture.CenterX, Y = picture.CenterY },
            NativeMethods.MONITOR_DEFAULTTONEAREST);

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return new Rect(
                info.rcWork.Left / sx,
                info.rcWork.Top / sy,
                info.rcWork.Right / sx,
                info.rcWork.Bottom / sy);
        }

        var fallback = SystemParameters.WorkArea;
        return new Rect(fallback.Left, fallback.Top, fallback.Right, fallback.Bottom);
    }

    private (double X, double Y) GetScale()
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;
        double sx = transform.M11 == 0 ? 1 : transform.M11;
        double sy = transform.M22 == 0 ? 1 : transform.M22;
        return (sx, sy);
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppController.ToolbarVisible)) return;

        Dispatcher.Invoke(() =>
        {
            if (_controller.ToolbarVisible)
            {
                ShowAbove();
            }
            else if (IsVisible)
            {
                Hide();
            }
        });
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        await _controller.RestoreAsync();
        PositionOnShow();
    }

    private async void OnRecoverClick(object sender, RoutedEventArgs e)
    {
        await _controller.EnsureWindowRecoverableAsync();
        PositionOnShow();
    }

    private void OnShowMainWindowClick(object sender, RoutedEventArgs e) => _tray.ShowMainWindow();

    private void OnHideClick(object sender, RoutedEventArgs e) => HideToolbar();

    /// <summary>
    /// 拖动把手：跟随画面时工具条与画面整体一起移动；不跟随时只移动工具条，画面留在原地。
    /// </summary>
    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool follows = _controller.Settings.ToolbarFollowsMirror;
        var rect = follows ? _controller.Window.GetRect() : null;

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

        var (sx, sy) = GetScale();

        if (follows)
        {
            if (rect is null) return;

            int dx = (int)Math.Round((Left - startLeft) * sx);
            int dy = (int)Math.Round((Top - startTop) * sy);
            if (dx == 0 && dy == 0) return;

            _controller.Window.MoveTo(rect.Value.Left + dx, rect.Value.Top + dy);
            _controller.SavePlacement();
            return;
        }

        // 不跟随时工具条自己走，画面留在原地，位置记下来下次还用
        _controller.SaveToolbarPlacement((int)Left, (int)Top);
    }
}
