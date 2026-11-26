using System.Windows.Threading;
using LNMirror.Core.Configuration;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

/// <summary>
/// 轮询鼠标位置，判断它是否落在画面范围内。
/// 不做跨进程子类化，也不安装常驻全局钩子，只读光标坐标，代价可以忽略。
/// 进入用画面矩形本身，离开时用外扩一圈的矩形，避免在边缘来回时反复显隐。
/// 开启“移开仍显示”后，指针离开时保持显示，直到收到任意鼠标点击。
/// </summary>
public sealed class MirrorHoverWatcher : IDisposable
{
    private const int LeaveMargin = 6;
    private const int SmartMovementTolerance = 8;

    private readonly DispatcherTimer _timer;
    private readonly Func<WindowRect?> _rectProvider;
    private readonly GlobalMouseClickWatcher _clickWatcher;
    private readonly bool _ownsClickWatcher;
    private readonly HoverRevealKeepState _state = new();
    private bool _keepEnabled;
    private bool _pointerInside;
    private bool _disposed;
    private int _delayMilliseconds = HoverDelayModes.SmartMilliseconds;
    private bool _requireStillness = true;
    private long? _hoverStartedAt;
    private (int X, int Y) _anchor;

    public MirrorHoverWatcher(
        Func<WindowRect?> rectProvider,
        TimeSpan? interval = null,
        GlobalMouseClickWatcher? clickWatcher = null)
    {
        _rectProvider = rectProvider;
        _clickWatcher = clickWatcher ?? new GlobalMouseClickWatcher(Dispatcher.CurrentDispatcher);
        _ownsClickWatcher = clickWatcher is null;
        _clickWatcher.Clicked += OnGlobalClick;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>达到显示条件、移开保持、点击关闭或离开区域时触发，参数为当前是否显示。</summary>
    public event Action<bool>? HoverChanged;

    public bool IsRevealed => _state.IsVisible;

    /// <summary>正在等待下一次点击关闭。为 true 时全局点击监听处于安装状态。</summary>
    public bool IsHolding => _state.IsHolding;

    /// <summary>最近一次轮询时指针是否在区域内（含离开迟滞）。</summary>
    public bool IsPointerInside => _pointerInside;

    public HoverRevealKeepPhase Phase => _state.Phase;

    /// <summary>是否启用“移开仍显示”。关闭时立即按指针当前位置回到普通悬停规则。</summary>
    public bool KeepEnabled
    {
        get => _keepEnabled;
        set
        {
            if (_keepEnabled == value) return;
            _keepEnabled = value;
            if (!value) Apply(() => _state.OnKeepDisabled(_pointerInside));
        }
    }

    public void SetDelay(int milliseconds, bool requireStillness)
    {
        _delayMilliseconds = milliseconds;
        _requireStillness = requireStillness;
        _hoverStartedAt = null;
    }

    public void Start()
    {
        if (!_disposed) _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _hoverStartedAt = null;
        _pointerInside = false;
        Apply(() => _state.Reset());
    }

    /// <summary>只清除保持/抑制状态，不停轮询；用于“找回画面”和窗口最小化。</summary>
    public void ResetHold()
    {
        _hoverStartedAt = null;
        Apply(() => _state.Reset());
    }

    private void Tick()
    {
        var rect = _rectProvider();
        if (!NativeMethods.GetCursorPos(out var cursor)) rect = null;
        ObservePointer(rect, cursor.X, cursor.Y, Environment.TickCount64);
    }

    private void ObservePointer(WindowRect? rect, int x, int y, long now)
    {
        if (rect is null || rect.Value.IsEmpty)
        {
            _hoverStartedAt = null;
            _pointerInside = false;
            Apply(() => _state.Reset());
            return;
        }

        var r = rect.Value;
        int margin = _state.IsVisible ? LeaveMargin : 0;
        bool within = x >= r.Left - margin && x <= r.Right + margin
            && y >= r.Top - margin && y <= r.Bottom + margin;
        _pointerInside = within;

        switch (_state.Phase)
        {
            case HoverRevealKeepPhase.Hidden:
                if (!within)
                {
                    _hoverStartedAt = null;
                    return;
                }

                if (_hoverStartedAt is null || (_requireStillness
                    && (Math.Abs(x - _anchor.X) > SmartMovementTolerance || Math.Abs(y - _anchor.Y) > SmartMovementTolerance)))
                {
                    _hoverStartedAt = now;
                    _anchor = (x, y);
                }

                if (now - _hoverStartedAt >= _delayMilliseconds) Apply(() => _state.OnPointerEntered());
                return;

            case HoverRevealKeepPhase.Revealed:
                if (!within)
                {
                    _hoverStartedAt = null;
                    Apply(() => _state.OnPointerLeft(_keepEnabled));
                }

                return;

            case HoverRevealKeepPhase.Held:
                // 保持显示，指针回来或再次离开都不改变显示，等下一次点击。
                return;

            case HoverRevealKeepPhase.Suppressed:
                // 指针先离开区域才解除抑制，下一次重新进入按正常悬停流程显示。
                if (!within) Apply(() => _state.OnPointerLeft(_keepEnabled));
                return;
        }
    }

    private void OnGlobalClick(GlobalMouseClick click)
    {
        if (_state.Phase != HoverRevealKeepPhase.Held) return;

        var rect = _rectProvider();
        bool inside = rect is { IsEmpty: false } r
                      && click.X >= r.Left && click.X <= r.Right
                      && click.Y >= r.Top && click.Y <= r.Bottom;

        // 点击位置就是抬起时的指针位置，据此决定是否需要“点击后抑制”。
        _pointerInside = inside;
        Apply(() => _state.OnClick(inside));
    }

    private void Apply(Action change)
    {
        bool wasVisible = _state.IsVisible;
        bool wasHolding = _state.IsHolding;
        change();

        if (wasHolding != _state.IsHolding)
        {
            _clickWatcher.SetHold(this, _state.IsHolding);
        }

        if (wasVisible != _state.IsVisible)
        {
            _hoverStartedAt = null;
            HoverChanged?.Invoke(_state.IsVisible);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _clickWatcher.Clicked -= OnGlobalClick;
        if (_ownsClickWatcher) _clickWatcher.Dispose();
    }
}
