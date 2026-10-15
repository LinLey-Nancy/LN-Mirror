using LNMirror.Core.Diagnostics;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

/// <summary>
/// 控制 scrcpy 画面窗口的外观与交互状态。
/// 所有操作都是对外部进程窗口的 Win32 调用，不做子类化、不抓取画面。
/// </summary>
public sealed class MirrorWindowController : IDisposable
{
    private IntPtr _handle;
    private int _originalExStyle;
    private bool _layeredAddedByUs;
    private int _lastOpacity = 100;

    public IntPtr Handle => _handle;

    public bool IsValid
    {
        get
        {
            if (_handle == IntPtr.Zero) return false;
            return NativeMethods.IsWindow(_handle);
        }
    }

    /// <summary>最近一次通过句柄确认存活、进程仍属于 scrcpy 的时间点由调用方自行判断。</summary>
    public int LastOpacity => _lastOpacity;

    public bool LayeredAddedByUs => _layeredAddedByUs;

    /// <summary>记录窗口初始样式，便于会话结束后恢复。</summary>
    public void Attach(IntPtr handle)
    {
        Detach();
        _handle = handle;
        _originalExStyle = NativeMethods.GetWindowExStyle(handle);
        _layeredAddedByUs = (_originalExStyle & NativeMethods.WS_EX_LAYERED) == 0;
        AppLog.Debug($"[窗口] 绑定画面窗口 0x{handle.ToInt64():X}，扩展样式 0x{_originalExStyle:X8}");
    }

    public void Detach()
    {
        if (_handle != IntPtr.Zero && NativeMethods.IsWindow(_handle))
        {
            RestoreStyles();
        }

        _handle = IntPtr.Zero;
        _originalExStyle = 0;
        _layeredAddedByUs = false;
    }

    /// <summary>
    /// 设置整窗不透明度（0-100）。Windows 的整窗 alpha 为 0-255。
    /// 返回读回的实际 alpha，便于自检时确认设置是否真正生效。
    /// </summary>
    public int? ApplyOpacity(int percent)
    {
        if (!IsValid) return null;

        percent = Math.Clamp(percent, 0, 100);
        _lastOpacity = percent;

        var ex = NativeMethods.GetWindowExStyle(_handle);
        if ((ex & NativeMethods.WS_EX_LAYERED) == 0)
        {
            ex |= NativeMethods.WS_EX_LAYERED;
            NativeMethods.SetWindowExStyle(_handle, ex);
        }

        byte alpha = (byte)Math.Round(percent * 255.0 / 100.0, MidpointRounding.AwayFromZero);
        if (!NativeMethods.SetLayeredWindowAttributes(_handle, 0, alpha, NativeMethods.LWA_ALPHA))
        {
            AppLog.Warn($"[窗口] 设置不透明度失败：alpha={alpha}（不透明度 {percent}%）");
            return null;
        }

        if (NativeMethods.GetLayeredWindowAttributes(_handle, out _, out byte readBack, out _))
        {
            AppLog.Debug($"[窗口] 不透明度 {percent}% → alpha {alpha}（读回 {readBack}）");
            return readBack;
        }

        return alpha;
    }

    public bool ApplyTopmost(bool topmost)
    {
        if (!IsValid) return false;
        var after = topmost ? new IntPtr(NativeMethods.HWND_TOPMOST) : new IntPtr(NativeMethods.HWND_NOTOPMOST);
        bool ok = NativeMethods.SetWindowPos(_handle, after, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        AppLog.Debug($"[窗口] 置顶={(topmost ? "开" : "关")}");
        return ok;
    }

    /// <summary>设置鼠标穿透（WS_EX_TRANSPARENT）。与不透明度互相独立。</summary>
    public bool ApplyPassThrough(bool passThrough)
    {
        if (!IsValid) return false;

        var ex = NativeMethods.GetWindowExStyle(_handle);
        bool had = (ex & NativeMethods.WS_EX_TRANSPARENT) != 0;
        if (had == passThrough) return true;

        ex = passThrough ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowExStyle(_handle, ex);
        AppLog.Debug($"[窗口] 鼠标穿透={(passThrough ? "开" : "关")}（WS_EX_TRANSPARENT {(passThrough ? "已设置" : "已移除")}）");
        return true;
    }

    /// <summary>切换输入模式前调用：通知 scrcpy 取消当前按住/拖动，避免手机端卡在长按。</summary>
    public void CancelPointerCapture()
    {
        if (!IsValid) return;
        NativeMethods.PostMessageW(_handle, NativeMethods.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
    }

    public bool RestoreIfMinimized()
    {
        if (!IsValid) return false;
        if (!NativeMethods.IsIconic(_handle)) return false;
        NativeMethods.ShowWindow(_handle, NativeMethods.SW_RESTORE);
        AppLog.Info("[窗口] 画面窗口已从最小化恢复");
        return true;
    }

    public bool IsMinimized => IsValid && NativeMethods.IsIconic(_handle);

    public WindowRect? GetRect()
    {
        if (!IsValid) return null;
        if (!NativeMethods.GetWindowRect(_handle, out var rect)) return null;
        return new WindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public bool MoveTo(int x, int y)
    {
        if (!IsValid) return false;
        return NativeMethods.SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>把窗口移回可见显示器的工作区，避免拔掉显示器或改分辨率后画面找不到。</summary>
    public bool MoveToVisibleArea()
    {
        if (!IsValid) return false;

        var work = GetWorkAreaOfNearestMonitor();
        var rect = GetRect();
        if (work is null || rect is null) return false;

        if (Intersects(rect.Value, work.Value)) return false;

        int width = Math.Max(rect.Value.Width, 240);
        int height = Math.Max(rect.Value.Height, 320);
        int x = work.Value.Left + Math.Max(0, (work.Value.Width - width) / 2);
        int y = work.Value.Top + Math.Max(0, (work.Value.Height - height) / 2);
        NativeMethods.SetWindowPos(_handle, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        AppLog.Info("[窗口] 原位置不在可见显示器上，已移回当前显示器工作区");
        return true;
    }

    private static bool Intersects(WindowRect a, WindowRect b)
    {
        int left = Math.Max(a.Left, b.Left);
        int top = Math.Max(a.Top, b.Top);
        int right = Math.Min(a.Right, b.Right);
        int bottom = Math.Min(a.Bottom, b.Bottom);
        return right - left > 40 && bottom - top > 40;
    }

    private static WindowRect? GetWorkAreaOfNearestMonitor()
    {
        NativeMethods.GetCursorPos(out var cursor);
        var point = new NativeMethods.POINT { X = cursor.X, Y = cursor.Y };
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return new WindowRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        }

        if (NativeMethods.TryGetMonitorWorkArea(IntPtr.Zero, out var fallback))
        {
            return new WindowRect(fallback.Left, fallback.Top, fallback.Right, fallback.Bottom);
        }

        return null;
    }

    private void RestoreStyles()
    {
        try
        {
            NativeMethods.SetWindowExStyle(_handle, _originalExStyle);
            NativeMethods.SetLayeredWindowAttributes(_handle, 0, 255, NativeMethods.LWA_ALPHA);
            AppLog.Debug("[窗口] 已恢复画面窗口初始样式");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[窗口] 恢复窗口样式时出错：{ex.Message}");
        }
    }

    public void Dispose() => Detach();
}