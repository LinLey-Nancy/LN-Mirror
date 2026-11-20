using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LNMirror.Core;
using LNMirror.Core.Configuration;
using LNMirror.Core.Interop;
using LNMirror.Core.Windowing;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace LNMirror.Views;

/// <summary>
/// 画面上方的覆盖层负责拖动把手与隐藏时的位置提示。
/// 中间区域点击穿透给 scrcpy。
/// </summary>
public partial class MirrorFrameOverlayWindow : Window
{
    /// <summary>指针停在画面上多久后出现拖动把手。太快出现会吃掉画面边缘的点击。</summary>
    private const int HandleDelayMs = 400;

    private readonly AppController _controller;
    private readonly DispatcherTimer _timer;
    private bool _dragging;
    private string _dragMode = "Move";
    private NativeMethods.POINT _dragStart;
    private WindowRect _startRect;
    private WindowRect _startContentRect;

    private long _insideSince = -1;

    public MirrorFrameOverlayWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        // 窗口首次 Show 之前没有句柄也不会触发 Loaded，必须在构造时就启动轮询
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void Tick()
    {
        if (_dragging)
        {
            // 安全网：鼠标捕获被其它窗口夺走时 OnHandleUp 不会触发，
            // 直接按真实按键状态结束拖动，避免边框残留卡死
            if (!NativeMethods.IsMouseButtonHeld())
            {
                _dragging = false;
            }
            else
            {
                SyncToMirror();
                return;
            }
        }

        var settings = _controller.Settings;
        bool connected = _controller.IsConnected;

        // 画面范围取客户区，系统标题栏不算画面
        var rect = connected ? _controller.Window.GetContentRect() : null;
        if (rect is null || rect.Value.IsEmpty)
        {
            HideAll();
            return;
        }

        bool pictureVisible = _controller.EffectiveOpacity > 0;
        bool handlesAllowed = settings.HoverFrameEnabled && pictureVisible && !_controller.PassThrough;
        bool overPicture = handlesAllowed && NativeMethods.GetCursorPos(out var cursor)
            && IsPointerOnPicture(rect.Value, cursor);
        long now = Environment.TickCount64;

        bool handlesVisible = false;
        if (overPicture)
        {
            if (_insideSince < 0) _insideSince = now;
            handlesVisible = now - _insideSince >= HandleDelayMs;
        }
        else
        {
            _insideSince = -1;
        }

        HandleLayer.Visibility = handlesVisible ? Visibility.Visible : Visibility.Collapsed;

        // 位置提示：只有开启"鼠标悬停显示画面"时才有，主窗口那一栏跟着一起显示，
        // 不会出现"不知道哪来的框"这种情况
        bool showHint = connected
                        && _controller.HoverRevealEnabled
                        && !pictureVisible
                        && _controller.Topmost
                        && settings.HoverRevealHint != HoverRevealHints.None;

        HintOutline.Visibility = showHint && settings.HoverRevealHint == HoverRevealHints.Outline
            ? Visibility.Visible : Visibility.Collapsed;
        HintCorner.Visibility = showHint && settings.HoverRevealHint == HoverRevealHints.CornerMark
            ? Visibility.Visible : Visibility.Collapsed;

        if (handlesVisible || showHint)
        {
            SyncToMirror(rect);
            if (!IsVisible) Show();
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    private void HideAll()
    {
        HandleLayer.Visibility = Visibility.Collapsed;
        HintOutline.Visibility = Visibility.Collapsed;
        HintCorner.Visibility = Visibility.Collapsed;
        _insideSince = -1;
        if (IsVisible) Hide();
    }

    /// <summary>
    /// 指针是否落在画面上，并且画面确实在最上层。
    /// 画面不置顶时多半被别的窗口盖着，这时不画提示，免得飘到别人家窗口上。
    /// </summary>
    private bool IsPointerOnPicture(WindowRect rect, NativeMethods.POINT cursor)
    {
        if (!_controller.Topmost) return false;

        bool inside = cursor.X >= rect.Left && cursor.X <= rect.Right
                      && cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;
        if (!inside) return false;

        var hit = NativeMethods.WindowFromPoint(cursor);
        if (hit == IntPtr.Zero) return false;

        var hitRoot = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
        if (hitRoot == _controller.Window.Handle) return true;

        // 命中最上层常常是我们这层覆盖层（它总是置顶且盖在画面上）
        var ownHandle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        return hitRoot == ownHandle;
    }

    private void SyncToMirror(WindowRect? rectOpt = null)
    {
        var rect = rectOpt ?? _controller.Window.GetContentRect();
        if (rect is null) return;

        // 画面窗口矩形是物理像素，WPF 用与设备无关的单位，按当前 DPI 换算
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;
        Left = rect.Value.Left * transform.M11;
        Top = rect.Value.Top * transform.M22;
        Width = rect.Value.Width * transform.M11;
        Height = rect.Value.Height * transform.M22;
    }

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        var rect = _controller.Window.GetRect();
        var content = _controller.Window.GetContentRect();
        if (rect is null || content is null) return;

        _dragMode = (string)((FrameworkElement)sender).Tag;
        _startRect = rect.Value;
        _startContentRect = content.Value;
        NativeMethods.GetCursorPos(out _dragStart);
        _dragging = true;
        ((FrameworkElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        NativeMethods.GetCursorPos(out var cursor);
        int dx = cursor.X - _dragStart.X;
        int dy = cursor.Y - _dragStart.Y;
        var r = _startRect;
        var c = _startContentRect;

        switch (_dragMode)
        {
            case "Move":
                _controller.Window.MoveTo(r.Left + dx, r.Top + dy);
                break;
            case "SE":
                ResizeSnapped(c.Left, c.Top, c.Right + dx, c.Bottom + dy, keepRight: false, keepBottom: false);
                break;
            case "NW":
                ResizeSnapped(c.Left + dx, c.Top + dy, c.Right, c.Bottom, keepRight: true, keepBottom: true);
                break;
            case "NE":
                ResizeSnapped(c.Left, c.Top + dy, c.Right + dx, c.Bottom, keepRight: false, keepBottom: true);
                break;
            case "SW":
                ResizeSnapped(c.Left + dx, c.Top, c.Right, c.Bottom + dy, keepRight: true, keepBottom: false);
                break;
        }

        SyncToMirror();
    }

    /// <summary>
    /// 按视频比例缩放：画面内容始终贴合画面，不产生黑边。
    /// 传入的是客户区（画面）坐标，落回窗口坐标时要把标题栏/边框的厚度加回去。
    /// </summary>
    private void ResizeSnapped(int left, int top, int right, int bottom, bool keepRight, bool keepBottom)
    {
        int w = Math.Max(120, right - left);
        int h = Math.Max(200, bottom - top);

        if (_controller.VideoSize is { } video)
        {
            double aspect = video.Width / (double)video.Height;
            if (w / (double)h > aspect)
            {
                w = Math.Max(120, (int)Math.Round(h * aspect));
            }
            else
            {
                h = Math.Max(200, (int)Math.Round(w / aspect));
            }
        }

        if (keepRight) left = right - w;
        if (keepBottom) top = bottom - h;

        var insets = GetChromeInsets();
        int windowLeft = left - insets.Left;
        int windowTop = top - insets.Top;
        int windowWidth = w + insets.Left + insets.Right;
        int windowHeight = h + insets.Top + insets.Bottom;

        _controller.Window.SetRect(windowLeft, windowTop, windowWidth, windowHeight);
    }

    /// <summary>窗口矩形与客户区矩形之间的差值，也就是标题栏与边框占掉的厚度。</summary>
    private WindowRect GetChromeInsets()
    {
        var win = _controller.Window.GetRect();
        var content = _controller.Window.GetContentRect();
        if (win is null || content is null) return new WindowRect(0, 0, 0, 0);

        return new WindowRect(
            win.Value.Left - content.Value.Left,
            win.Value.Top - content.Value.Top,
            win.Value.Right - content.Value.Right,
            win.Value.Bottom - content.Value.Bottom);
    }

    private void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ((FrameworkElement)sender).ReleaseMouseCapture();
        _controller.SavePlacement();
    }

    private void OnHandleEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border) border.Opacity = 1;
    }

    private void OnHandleLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border) border.Opacity = 0.35;
    }
}
