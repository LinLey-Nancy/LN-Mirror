using System.Windows.Threading;
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

    private readonly DispatcherTimer _timer;
    private readonly Func<WindowRect?> _rectProvider;
    private bool _inside;
    private bool _running;

    public MirrorHoverWatcher(Func<WindowRect?> rectProvider, TimeSpan? interval = null)
    {
        _rectProvider = rectProvider;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>鼠标当前是否落在画面范围内。</summary>
    public bool IsInside => _inside;

    /// <summary>进入/离开画面范围时触发，参数为进入后的状态。</summary>
    public event Action<bool>? HoverChanged;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _timer.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _timer.Stop();
        SetInside(false);
    }

    private void Tick()
    {
        var rect = _rectProvider();
        if (rect is null || rect.Value.IsEmpty)
        {
            SetInside(false);
            return;
        }

        NativeMethods.GetCursorPos(out var cursor);
        var r = rect.Value;

        bool inside;
        if (_inside)
        {
            // 已经进入时用外扩一圈的边界，光标停在边缘也不会来回闪
            inside = cursor.X >= r.Left - LeaveMargin && cursor.X <= r.Right + LeaveMargin
                  && cursor.Y >= r.Top - LeaveMargin && cursor.Y <= r.Bottom + LeaveMargin;
        }
        else
        {
            inside = cursor.X >= r.Left && cursor.X <= r.Right
                  && cursor.Y >= r.Top && cursor.Y <= r.Bottom;
        }

        SetInside(inside);
    }

    private void SetInside(bool value)
    {
        if (_inside == value) return;
        _inside = value;
        HoverChanged?.Invoke(value);
    }
}
