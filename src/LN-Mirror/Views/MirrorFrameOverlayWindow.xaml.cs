using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LNMirror.Core;
using LNMirror.Core.Configuration;
using LNMirror.Core.Diagnostics;
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

    /// <summary>把手出现时的强调动画：保持 0.5 秒醒目，再用 0.2 秒淡回常驻的 0.35。</summary>
    private static readonly DoubleAnimationUsingKeyFrames HandleEmphasisAnimation = CreateHandleEmphasis();

    private readonly AppController _controller;
    private readonly DispatcherTimer _timer;
    private readonly Border[] _handleBorders;
    private bool _dragging;
    private FrameworkElement? _dragOrigin;
    private string _dragMode = "Move";
    private NativeMethods.POINT _dragStart;
    private WindowRect _startRect;
    private WindowRect _startContentRect;

    private long _insideSince = -1;

    internal bool IsDragging => _dragging;

    public MirrorFrameOverlayWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        _handleBorders =
        [
            TopMoveHandle, BottomMoveHandle, LeftMoveHandle, RightMoveHandle,
            CornerNW, CornerNE, CornerSW, CornerSE,
        ];

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        // 窗口首次 Show 之前没有句柄也不会触发 Loaded，必须在构造时就启动轮询
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            EndDrag(savePlacement: false);
        };
    }

    private void Tick()
    {
        if (_dragging)
        {
            // 拖动不依赖 MouseMove 是否还在派发：WPF 捕获可能失败或在移动外部窗口时丢失，
            // 只要左键还按着，就按真实光标位置继续移动。两条路径都用“起点 + 总位移”，重复调用无副作用。
            // 两个来源取或：GetAsyncKeyState 在个别桌面/UIPI 场景会返回 0，不能作为唯一依据。
            bool held = NativeMethods.IsLeftMouseButtonHeld() || Mouse.LeftButton == MouseButtonState.Pressed;
            if (!held)
            {
                EndDrag(savePlacement: true);
            }
            else
            {
                if (NativeMethods.GetCursorPos(out var dragCursor)) DragTo(dragCursor.X, dragCursor.Y);
                if (_dragging) return;
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

        var targetHandleVisibility = handlesVisible ? Visibility.Visible : Visibility.Collapsed;
        if (HandleLayer.Visibility != targetHandleVisibility)
        {
            HandleLayer.Visibility = targetHandleVisibility;
            if (handlesVisible) StartHandleEmphasis();
        }

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

    /// <summary>把手刚出现时先醒目半秒，再淡回常驻的淡色。鼠标已经停在把手上时保持高亮。</summary>
    private void StartHandleEmphasis()
    {
        foreach (var border in _handleBorders)
        {
            if (border.IsMouseOver) continue;
            border.BeginAnimation(OpacityProperty, HandleEmphasisAnimation);
        }
    }

    private static DoubleAnimationUsingKeyFrames CreateHandleEmphasis()
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(700),
            FillBehavior = FillBehavior.Stop,
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
        animation.Freeze();
        return animation;
    }

    private void HideAll()
    {
        EndDrag(savePlacement: true);
        HandleLayer.Visibility = Visibility.Collapsed;
        HintOutline.Visibility = Visibility.Collapsed;
        HintCorner.Visibility = Visibility.Collapsed;
        _insideSince = -1;
        if (IsVisible) Hide();
    }

    /// <summary>
    /// 指针是否落在画面上。只要窗口命中测试确认鼠标下方就是 scrcpy 窗口或本覆盖层，
    /// 就允许显示拖动把手；画面不置顶时如果被别的窗口盖住，命中测试会失败，不会误画。
    /// 位置提示仍只在画面置顶时显示（见 Tick 的 showHint）。
    /// </summary>
    private bool IsPointerOnPicture(WindowRect rect, NativeMethods.POINT cursor)
    {
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
        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        if (!BeginDrag((string)((FrameworkElement)sender).Tag, (FrameworkElement)sender, cursor.X, cursor.Y)) return;
        e.Handled = true;
    }

    /// <summary>进入拖动。捕获失败不阻止拖动，33ms 定时器会继续按真实光标位置驱动。</summary>
    internal bool BeginDrag(string mode, FrameworkElement origin, int x, int y)
    {
        var rect = _controller.Window.GetRect();
        var content = _controller.Window.GetContentRect();
        if (rect is null || content is null)
        {
            AppLog.Debug("[把手] 画面窗口句柄无效或客户区为空，忽略本次拖动");
            return false;
        }

        _dragMode = mode;
        _startRect = rect.Value;
        _startContentRect = content.Value;
        _dragStart = new NativeMethods.POINT { X = x, Y = y };
        _dragOrigin = origin;
        _dragging = true;
        if (!origin.CaptureMouse())
        {
            // 只记录，不放弃：捕获失败正是“把手能显示但拖不动”的典型现场。
            AppLog.Debug("[把手] 鼠标捕获未成功，改用定时器跟踪拖动");
        }

        return true;
    }

    /// <summary>按起点到当前光标的绝对位移移动/缩放画面窗口。</summary>
    internal bool DragTo(int x, int y)
    {
        if (!_dragging) return false;

        int dx = x - _dragStart.X;
        int dy = y - _dragStart.Y;
        var r = _startRect;
        var c = _startContentRect;

        bool ok;
        switch (_dragMode)
        {
            case "Move":
                ok = _controller.Window.MoveTo(r.Left + dx, r.Top + dy);
                break;
            case "SE":
                ok = ResizeSnapped(c.Left, c.Top, c.Right + dx, c.Bottom + dy, keepRight: false, keepBottom: false);
                break;
            case "NW":
                ok = ResizeSnapped(c.Left + dx, c.Top + dy, c.Right, c.Bottom, keepRight: true, keepBottom: true);
                break;
            case "NE":
                ok = ResizeSnapped(c.Left, c.Top + dy, c.Right + dx, c.Bottom, keepRight: false, keepBottom: true);
                break;
            case "SW":
                ok = ResizeSnapped(c.Left + dx, c.Top, c.Right, c.Bottom + dy, keepRight: true, keepBottom: false);
                break;
            default:
                ok = false;
                break;
        }

        if (!ok)
        {
            AppLog.Warn($"[把手] 画面窗口移动/缩放失败（模式 {_dragMode}，句柄 0x{_controller.Window.Handle.ToInt64():X}），已结束本次拖动");
            EndDrag(savePlacement: false);
            return false;
        }

        SyncToMirror();
        return true;
    }

    /// <summary>结束拖动。重复调用安全；先清状态再释放捕获，避免 LostMouseCapture 重入。</summary>
    internal void EndDrag(bool savePlacement)
    {
        if (!_dragging) return;
        _dragging = false;
        var origin = _dragOrigin;
        _dragOrigin = null;
        try { origin?.ReleaseMouseCapture(); }
        catch { /* 捕获可能已经丢失 */ }
        if (savePlacement) _controller.SavePlacement();
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        DragTo(cursor.X, cursor.Y);
    }

    private void OnHandleLostCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        AppLog.Warn("[把手] 拖动中丢失鼠标捕获，已结束本次拖动");
        EndDrag(savePlacement: true);
    }

    /// <summary>
    /// 按视频比例缩放：画面内容始终贴合画面，不产生黑边。
    /// 传入的是客户区（画面）坐标，落回窗口坐标时要把标题栏/边框的厚度加回去。
    /// </summary>
    private bool ResizeSnapped(int left, int top, int right, int bottom, bool keepRight, bool keepBottom)
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

        return _controller.Window.SetRect(windowLeft, windowTop, windowWidth, windowHeight);
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
        if (e.ChangedButton != MouseButton.Left) return;
        EndDrag(savePlacement: true);
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
