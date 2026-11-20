using System.Windows.Threading;
using LNMirror.Core.Configuration;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

/// <summary>
/// 轮询鼠标位置，判断它是否落在画面范围内。
/// 不做跨进程子类化，也不安装全局钩子，只读光标坐标，代价可以忽略。
/// 进入用画面矩形本身，离开时用外扩一圈的矩形，避免在边缘来回时反复显隐。
/// </summary>
public sealed class MirrorHoverWatcher
{
    private const int LeaveMargin = 6;
    private const int SmartMovementTolerance = 8;

    private readonly DispatcherTimer _timer;
    private readonly Func<WindowRect?> _rectProvider;
    private bool _revealed;
    private int _delayMilliseconds = HoverDelayModes.SmartMilliseconds;
    private bool _requireStillness = true;
    private long? _hoverStartedAt;
    private (int X, int Y) _anchor;

    public MirrorHoverWatcher(Func<WindowRect?> rectProvider, TimeSpan? interval = null)
    {
        _rectProvider = rectProvider;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>达到显示条件或离开画面时触发。</summary>
    public event Action<bool>? HoverChanged;

    public void SetDelay(int milliseconds, bool requireStillness)
    {
        _delayMilliseconds = milliseconds;
        _requireStillness = requireStillness;
        _hoverStartedAt = null;
    }

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        _hoverStartedAt = null;
        SetRevealed(false);
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
            SetRevealed(false);
            return;
        }

        var r = rect.Value;
        int margin = _revealed ? LeaveMargin : 0;
        bool within = x >= r.Left - margin && x <= r.Right + margin
            && y >= r.Top - margin && y <= r.Bottom + margin;
        if (!within)
        {
            _hoverStartedAt = null;
            SetRevealed(false);
            return;
        }
        if (_revealed) return;

        if (_hoverStartedAt is null || (_requireStillness
            && (Math.Abs(x - _anchor.X) > SmartMovementTolerance || Math.Abs(y - _anchor.Y) > SmartMovementTolerance)))
        {
            _hoverStartedAt = now;
            _anchor = (x, y);
        }

        if (now - _hoverStartedAt >= _delayMilliseconds) SetRevealed(true);
    }

    private void SetRevealed(bool value)
    {
        if (_revealed == value) return;
        _revealed = value;
        HoverChanged?.Invoke(value);
    }
}
