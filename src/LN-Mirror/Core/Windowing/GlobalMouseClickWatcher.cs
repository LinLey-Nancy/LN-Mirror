using System.Runtime.InteropServices;
using System.Windows.Threading;
using LNMirror.Core.Diagnostics;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

/// <summary>一次全局鼠标点击。只保留屏幕坐标和消息类型，不记录窗口、应用或输入内容。</summary>
public readonly record struct GlobalMouseClick(int X, int Y, int Message);

/// <summary>
/// 全局鼠标点击监听。只在至少一个目标“移开仍显示”等待点击关闭时安装，
/// 最后一个保持结束时立即卸载。回调只把点击投递到界面线程，
/// 不吞掉本次点击，也不在回调里做窗口操作。
/// </summary>
public sealed class GlobalMouseClickWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<object> _holds = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();
    private HookSession? _session;
    private bool _disposed;

    public GlobalMouseClickWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public event Action<GlobalMouseClick>? Clicked;

    /// <summary>供诊断和原生冒烟测试使用。</summary>
    public bool IsInstalled
    {
        get
        {
            lock (_gate) return _session?.Installed == true;
        }
    }

    /// <summary>当前处于“等待点击关闭”状态的目标数量。</summary>
    public int ActiveHoldCount
    {
        get
        {
            lock (_gate) return _holds.Count;
        }
    }

    /// <summary>登记或注销一个需要等待点击的目标。计数从 0 到 1 时安装监听，回到 0 时卸载。</summary>
    public void SetHold(object owner, bool active)
    {
        ArgumentNullException.ThrowIfNull(owner);

        bool start = false;
        bool stop = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (active) _holds.Add(owner);
            else _holds.Remove(owner);

            if (_holds.Count > 0)
            {
                start = _session is null;
            }
            else
            {
                stop = _session is not null;
            }
        }

        if (start) StartSession();
        if (stop) StopSession();
    }

    private void StartSession()
    {
        HookSession session;
        lock (_gate)
        {
            if (_disposed || _session is not null) return;

            session = new HookSession(this);
            session.Thread = new Thread(() => RunHookThread(session))
            {
                IsBackground = true,
                Name = "LN-Mirror 全局点击监听",
            };
            _session = session;
            session.Thread.Start();
        }

        if (!session.Ready.Wait(TimeSpan.FromSeconds(2)) || !session.Installed)
        {
            AppLog.Warn($"[点击监听] 全局鼠标监听安装失败（Windows 错误 {session.Error}），保持显示将无法被点击关闭");
            lock (_gate)
            {
                if (ReferenceEquals(_session, session)) _session = null;
            }

            session.Thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    private void StopSession()
    {
        HookSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is null) return;
        StopHookThread(session);
    }

    private static void StopHookThread(HookSession session)
    {
        session.StopRequested = true;
        session.Ready.Wait(TimeSpan.FromSeconds(1));
        if (session.ThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(session.ThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        if (!session.Thread.Join(TimeSpan.FromSeconds(1)))
        {
            AppLog.Warn("[点击监听] 全局鼠标监听线程未在 1 秒内退出");
        }
    }

    private static void RunHookThread(HookSession session)
    {
        try
        {
            session.ThreadId = NativeMethods.GetCurrentThreadId();
            session.Hook = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_MOUSE_LL,
                session.HookCallback,
                NativeMethods.GetModuleHandleW(IntPtr.Zero),
                0);

            if (session.Hook == IntPtr.Zero)
            {
                session.Error = Marshal.GetLastWin32Error();
                return;
            }

            // 先建立消息队列，卸载时 PostThreadMessage(WM_QUIT) 才不会丢掉。
            NativeMethods.PeekMessageW(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
            session.Installed = true;
        }
        finally
        {
            session.Ready.Set();
        }

        try
        {
            while (!session.StopRequested)
            {
                int result = NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0);
                if (result <= 0) break;
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessageW(ref message);
            }
        }
        finally
        {
            if (session.Hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(session.Hook);
            }

            session.Installed = false;
        }
    }

    /// <summary>点击完成（抬起且未拖动）后从钩子线程投递。延迟 40 ms 让抬起先送达目标窗口。</summary>
    internal void PostClick(GlobalMouseClick click)
    {
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                // 低层钩子回调发生在目标窗口收到抬起之前。如果立刻改不透明度和穿透，
                // 抬起可能被改投到别的窗口，手机端会留下按下的触控点。
                var timer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(40),
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    RaiseClicked(click);
                };
                timer.Start();
            }));
        }
        catch (Exception)
        {
            // 退出过程中调度器可能已经关闭；点击监听本身不需要补救。
        }
    }

    private void RaiseClicked(GlobalMouseClick click)
    {
        var handlers = Clicked;
        if (handlers is null) return;

        foreach (Action<GlobalMouseClick> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(click);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[点击监听] 处理点击通知失败：{ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _holds.Clear();
        }

        StopSession();
    }

    private sealed class HookSession
    {
        private readonly GlobalMouseClickWatcher _owner;
        public Thread Thread = null!;
        public readonly ManualResetEventSlim Ready = new(false);
        public readonly NativeMethods.HookProc HookCallback;
        public volatile bool Installed;
        public volatile bool StopRequested;
        public volatile uint ThreadId;
        public IntPtr Hook;
        public int Error;

        public HookSession(GlobalMouseClickWatcher owner)
        {
            _owner = owner;
            HookCallback = OnHook;
        }

        private static readonly int DragThresholdX =
            Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG));

        private static readonly int DragThresholdY =
            Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDRAG));

        private int _downMessage;
        private int _downX;
        private int _downY;
        private bool _dragged;

        private IntPtr OnHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 回调运行在专用线程上：只做按下/抬起配对和拖拽判定，然后无条件交给下一个钩子。
            try
            {
                if (nCode >= 0) ObserveMessage(wParam, lParam);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[点击监听] 全局回调失败：{ex.Message}");
            }

            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private void ObserveMessage(IntPtr wParam, IntPtr lParam)
        {
            int message = unchecked((int)wParam.ToInt64());
            switch (message)
            {
                case NativeMethods.WM_LBUTTONDOWN:
                case NativeMethods.WM_RBUTTONDOWN:
                case NativeMethods.WM_MBUTTONDOWN:
                case NativeMethods.WM_XBUTTONDOWN:
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    _downMessage = message;
                    _downX = data.pt.X;
                    _downY = data.pt.Y;
                    _dragged = false;
                    break;
                }

                case NativeMethods.WM_MOUSEMOVE:
                    if (_downMessage == 0) return;
                    var move = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    if (Math.Abs(move.pt.X - _downX) > DragThresholdX
                        || Math.Abs(move.pt.Y - _downY) > DragThresholdY)
                    {
                        _dragged = true;
                    }

                    break;

                case NativeMethods.WM_LBUTTONUP:
                case NativeMethods.WM_RBUTTONUP:
                case NativeMethods.WM_MBUTTONUP:
                case NativeMethods.WM_XBUTTONUP:
                {
                    if (!MatchesDown(message)) return;
                    bool clicked = !_dragged;
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    _downMessage = 0;
                    _dragged = false;
                    if (clicked)
                    {
                        _owner.PostClick(new GlobalMouseClick(data.pt.X, data.pt.Y, message));
                    }

                    break;
                }
            }
        }

        private bool MatchesDown(int upMessage) => upMessage switch
        {
            NativeMethods.WM_LBUTTONUP => _downMessage == NativeMethods.WM_LBUTTONDOWN,
            NativeMethods.WM_RBUTTONUP => _downMessage == NativeMethods.WM_RBUTTONDOWN,
            NativeMethods.WM_MBUTTONUP => _downMessage == NativeMethods.WM_MBUTTONDOWN,
            NativeMethods.WM_XBUTTONUP => _downMessage == NativeMethods.WM_XBUTTONDOWN,
            _ => false,
        };
    }
}
