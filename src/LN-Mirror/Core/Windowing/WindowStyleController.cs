using System.Runtime.InteropServices;
using LNMirror.Core.Diagnostics;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

public sealed record WindowStyleSnapshot
{
    public int ExStyle { get; init; }
    public int Style { get; init; }
    public bool Topmost { get; init; }
    public bool HasLayeredAttributes { get; init; }
    public uint ColorKey { get; init; }
    public byte Alpha { get; init; }
    public uint LayeredFlags { get; init; }
}

public class WindowStyleController : IDisposable
{
    private IntPtr _handle;
    private int _originalExStyle;
    private int _originalStyle;
    private bool _originalTopmost;
    private bool _originalLayeredAttributes;
    private uint _originalColorKey;
    private byte _originalAlpha;
    private uint _originalLayeredFlags;
    private bool _layeredAddedByUs;
    private int _lastOpacity = 100;
    private int _lastError;

    public IntPtr Handle => _handle;
    public bool IsValid => _handle != IntPtr.Zero && NativeMethods.IsWindow(_handle);
    public int LastOpacity => _lastOpacity;
    public bool LayeredAddedByUs => _layeredAddedByUs;
    public int LastError => _lastError;
    public bool RequiresElevation => _lastError == 5;

    public void Attach(IntPtr handle)
    {
        Detach();
        _handle = handle;
        _originalExStyle = NativeMethods.GetWindowExStyle(handle);
        _originalStyle = NativeMethods.GetWindowStyle(handle);
        _originalTopmost = (_originalExStyle & NativeMethods.WS_EX_TOPMOST) != 0;
        _layeredAddedByUs = (_originalExStyle & NativeMethods.WS_EX_LAYERED) == 0;
        _originalLayeredAttributes = NativeMethods.GetLayeredWindowAttributes(
            handle, out _originalColorKey, out _originalAlpha, out _originalLayeredFlags);
        _lastError = 0;
        AppLog.Debug($"[窗口] 绑定窗口 0x{handle.ToInt64():X}，扩展样式 0x{_originalExStyle:X8}");
    }

    public void Detach()
    {
        if (_handle != IntPtr.Zero && NativeMethods.IsWindow(_handle)) RestoreStyles();
        Clear();
    }

    public int? ApplyOpacity(int percent)
    {
        if (!IsValid) return null;
        _lastError = 0;
        percent = Math.Clamp(percent, 0, 100);
        _lastOpacity = percent;

        int ex = NativeMethods.GetWindowExStyle(_handle);
        if ((ex & NativeMethods.WS_EX_LAYERED) == 0
            && !NativeMethods.SetWindowExStyle(_handle, ex | NativeMethods.WS_EX_LAYERED))
        {
            CaptureError();
            return null;
        }

        byte alpha = (byte)Math.Round(percent * 255.0 / 100.0, MidpointRounding.AwayFromZero);
        if (!NativeMethods.SetLayeredWindowAttributes(_handle, 0, alpha, NativeMethods.LWA_ALPHA))
        {
            CaptureError();
            AppLog.Warn($"[窗口] 设置不透明度失败：alpha={alpha}，错误={_lastError}");
            return null;
        }

        if (NativeMethods.GetLayeredWindowAttributes(_handle, out _, out byte readBack, out _)) return readBack;
        return alpha;
    }

    public bool ApplyTopmost(bool topmost)
    {
        if (!IsValid) return false;
        _lastError = 0;
        var after = new IntPtr(topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST);
        bool ok = NativeMethods.SetWindowPos(_handle, after, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        if (!ok) CaptureError();
        return ok;
    }

    public bool ApplyPassThrough(bool passThrough)
    {
        if (!IsValid) return false;
        _lastError = 0;
        int ex = NativeMethods.GetWindowExStyle(_handle);
        bool had = (ex & NativeMethods.WS_EX_TRANSPARENT) != 0;
        if (had == passThrough) return true;

        int updated = passThrough ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~NativeMethods.WS_EX_TRANSPARENT;
        bool ok = NativeMethods.SetWindowExStyle(_handle, updated);
        if (!ok) CaptureError();
        return ok;
    }

    public bool ApplyTaskSwitcherHidden(bool hidden)
    {
        if (!IsValid) return false;
        _lastError = 0;
        int current = NativeMethods.GetWindowExStyle(_handle);
        int updated = WindowTaskSwitcherStyles.Apply(current, hidden, _originalExStyle);
        if (updated == current) return true;
        bool ok = NativeMethods.SetWindowExStyle(_handle, updated);
        if (!ok) CaptureError();
        return ok;
    }

    public bool ApplyToolWindow(bool toolWindow) => ApplyTaskSwitcherHidden(toolWindow);

    public bool ApplyBorderless(bool borderless)
    {
        if (!IsValid) return false;
        _lastError = 0;
        int style = NativeMethods.GetWindowStyle(_handle);
        int updated = borderless
            ? style & ~NativeMethods.WS_CAPTION & ~NativeMethods.WS_THICKFRAME
            : style | NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME;
        if (updated == style) return true;
        bool ok = NativeMethods.SetWindowStyle(_handle, updated);
        if (!ok) CaptureError();
        return ok;
    }

    public void CancelPointerCapture()
    {
        if (IsValid) NativeMethods.PostMessageW(_handle, NativeMethods.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
    }

    public bool RestoreIfMinimized()
    {
        if (!IsValid || !NativeMethods.IsIconic(_handle)) return false;
        NativeMethods.ShowWindow(_handle, NativeMethods.SW_RESTORE);
        return true;
    }

    public bool IsMinimized => IsValid && NativeMethods.IsIconic(_handle);

    public WindowRect? GetRect()
    {
        if (!IsValid || !NativeMethods.GetWindowRect(_handle, out var rect)) return null;
        return new WindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public WindowRect? GetContentRect()
    {
        if (!IsValid || !NativeMethods.GetClientRect(_handle, out var client)) return null;
        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(_handle, ref origin)) return null;
        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        return width <= 0 || height <= 0
            ? null
            : new WindowRect(origin.X, origin.Y, origin.X + width, origin.Y + height);
    }

    public bool MoveTo(int x, int y)
    {
        if (!IsValid) return false;
        _lastError = 0;
        bool ok = NativeMethods.SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        if (!ok)
        {
            CaptureError();
            AppLog.Warn($"[窗口] 移动失败：句柄 0x{_handle.ToInt64():X}，目标 ({x},{y})，Windows 错误 {_lastError}");
        }

        return ok;
    }

    public bool SetRect(int x, int y, int width, int height)
    {
        if (!IsValid) return false;
        width = Math.Max(width, 120);
        height = Math.Max(height, 200);
        _lastError = 0;
        bool ok = NativeMethods.SetWindowPos(_handle, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        if (!ok)
        {
            CaptureError();
            AppLog.Warn($"[窗口] 调整大小失败：句柄 0x{_handle.ToInt64():X}，目标 ({x},{y},{width},{height})，Windows 错误 {_lastError}");
        }

        return ok;
    }

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

    private WindowRect? GetWorkAreaOfNearestMonitor()
    {
        var monitor = _handle != IntPtr.Zero
            ? NativeMethods.MonitorFromWindow(_handle, NativeMethods.MONITOR_DEFAULTTONEAREST)
            : IntPtr.Zero;
        if (monitor == IntPtr.Zero && NativeMethods.GetCursorPos(out var cursor))
            monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfoW(monitor, ref info))
                return new WindowRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        }
        return NativeMethods.TryGetMonitorWorkArea(IntPtr.Zero, out var fallback)
            ? new WindowRect(fallback.Left, fallback.Top, fallback.Right, fallback.Bottom)
            : null;
    }

    public WindowStyleSnapshot CreateSnapshot() => new()
    {
        ExStyle = _originalExStyle,
        Style = _originalStyle,
        Topmost = _originalTopmost,
        HasLayeredAttributes = _originalLayeredAttributes,
        ColorKey = _originalColorKey,
        Alpha = _originalAlpha,
        LayeredFlags = _originalLayeredFlags,
    };

    public void RestoreSnapshot(WindowStyleSnapshot snapshot)
    {
        if (!IsValid) return;
        _originalExStyle = snapshot.ExStyle;
        _originalStyle = snapshot.Style;
        _originalTopmost = snapshot.Topmost;
        _originalLayeredAttributes = snapshot.HasLayeredAttributes;
        _originalColorKey = snapshot.ColorKey;
        _originalAlpha = snapshot.Alpha;
        _originalLayeredFlags = snapshot.LayeredFlags;
        RestoreStyles();
    }

    private void RestoreStyles()
    {
        try
        {
            int currentEx = NativeMethods.GetWindowExStyle(_handle);
            const int ownedExBits = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT
                | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_APPWINDOW;
            int restoredEx = (currentEx & ~ownedExBits) | (_originalExStyle & ownedExBits);

            if (_originalLayeredAttributes)
            {
                NativeMethods.SetWindowExStyle(_handle, restoredEx | NativeMethods.WS_EX_LAYERED);
                NativeMethods.SetLayeredWindowAttributes(
                    _handle, _originalColorKey, _originalAlpha, _originalLayeredFlags);
            }
            else if ((currentEx & NativeMethods.WS_EX_LAYERED) != 0)
            {
                NativeMethods.SetLayeredWindowAttributes(_handle, 0, 255, NativeMethods.LWA_ALPHA);
            }
            NativeMethods.SetWindowExStyle(_handle, restoredEx);

            int currentStyle = NativeMethods.GetWindowStyle(_handle);
            const int ownedStyleBits = NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME;
            int restoredStyle = (currentStyle & ~ownedStyleBits) | (_originalStyle & ownedStyleBits);
            if (restoredStyle != currentStyle) NativeMethods.SetWindowStyle(_handle, restoredStyle);

            bool topmost = (NativeMethods.GetWindowExStyle(_handle) & NativeMethods.WS_EX_TOPMOST) != 0;
            if (topmost != _originalTopmost) ApplyTopmost(_originalTopmost);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[窗口] 恢复窗口样式时出错：{ex.Message}");
        }
    }

    private void CaptureError()
    {
        int error = Marshal.GetLastWin32Error();
        if (error != 0) _lastError = error;
    }

    private void Clear()
    {
        _handle = IntPtr.Zero;
        _originalExStyle = 0;
        _originalStyle = 0;
        _originalTopmost = false;
        _originalLayeredAttributes = false;
        _layeredAddedByUs = false;
        _lastError = 0;
    }

    public void Dispose() => Detach();
}
