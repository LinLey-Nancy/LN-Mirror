using System.Runtime.InteropServices;
using LNMirror.Core.Diagnostics;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

public enum PassThroughVerdict
{
    /// <summary>未能取到有效采样点，无法判断（例如画面被完全遮挡或窗口在屏幕外）。</summary>
    Unknown,

    /// <summary>已确认点击落到下方窗口。</summary>
    Effective,

    /// <summary>已确认点击仍被画面窗口接收。</summary>
    NotEffective,
}

public sealed record PassThroughCheck(PassThroughVerdict Verdict, string Detail)
{
    public string Display => Verdict switch
    {
        PassThroughVerdict.Effective => "穿透已生效",
        PassThroughVerdict.NotEffective => "穿透未生效",
        _ => "穿透未确认",
    };
}

/// <summary>
/// 鼠标穿透的真实验证：在同一批屏幕采样点上，分别问系统"最上面是谁"（关闭穿透 vs 开启穿透）。
/// 只有当关闭穿透时采样点确实落在画面窗口上，这次比较才有意义，否则如实报告"无法判断"。
/// </summary>
public static class PassThroughVerifier
{
    public static PassThroughCheck Verify(MirrorWindowController window)
    {
        if (!window.IsValid)
        {
            return new PassThroughCheck(PassThroughVerdict.Unknown, "画面窗口不存在，无法验证");
        }

        var rect = window.GetRect();
        if (rect is null)
        {
            return new PassThroughCheck(PassThroughVerdict.Unknown, "读不到画面窗口位置");
        }

        if (rect.Value.Width < 40 || rect.Value.Height < 40)
        {
            return new PassThroughCheck(PassThroughVerdict.Unknown, "画面窗口过小，无法验证");
        }

        var samples = BuildSamplePoints(rect.Value);
        if (samples.Count == 0)
        {
            return new PassThroughCheck(PassThroughVerdict.Unknown, "画面窗口全部在屏幕外，无法验证");
        }

        window.ApplyPassThrough(false);
        var usable = new List<NativeMethods.POINT>();
        foreach (var p in samples)
        {
            if (NativeMethods.WindowFromPoint(p) == window.Handle)
            {
                usable.Add(p);
            }
        }

        if (usable.Count == 0)
        {
            window.ApplyPassThrough(true);
            return new PassThroughCheck(PassThroughVerdict.Unknown,
                "画面窗口被其它窗口完全遮挡，取不到基线采样点；请先把画面拖到空白处再验证");
        }

        window.ApplyPassThrough(true);
        int passed = 0;
        foreach (var p in usable)
        {
            if (NativeMethods.WindowFromPoint(p) != window.Handle) passed++;
        }

        double ratio = (double)passed / usable.Count;
        if (passed == 0)
        {
            return new PassThroughCheck(PassThroughVerdict.NotEffective,
                $"{usable.Count} 个采样点仍全部落在画面窗口上，没有穿过到下层窗口");
        }

        string detail = ratio >= 0.999
            ? $"{usable.Count} 个采样点全部穿过到下层窗口"
            : $"{passed}/{usable.Count} 个采样点穿过到下层窗口";

        return new PassThroughCheck(PassThroughVerdict.Effective, detail);
    }

    /// <summary>供自检和界面"验证穿透"按钮使用：验证并写入日志。</summary>
    public static PassThroughCheck VerifyAndLog(MirrorWindowController window)
    {
        var check = Verify(window);
        AppLog.Info($"[自检] 鼠标穿透：{check.Display}（{check.Detail}）");
        return check;
    }

    private static List<NativeMethods.POINT> BuildSamplePoints(WindowRect rect)
    {
        var points = new List<NativeMethods.POINT>();
        var fractions = new[] { 0.30, 0.45, 0.60, 0.75 };

        foreach (var fx in fractions)
        {
            foreach (var fy in fractions)
            {
                var p = new NativeMethods.POINT
                {
                    X = rect.Left + (int)(rect.Width * fx),
                    Y = rect.Top + (int)(rect.Height * fy),
                };

                if (IsOnWorkArea(p)) points.Add(p);
            }
        }

        return points;
    }

    private static bool IsOnWorkArea(NativeMethods.POINT p)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var monitor = NativeMethods.MonitorFromPoint(p, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero) return false;
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return false;

        return p.X >= info.rcWork.Left && p.X < info.rcWork.Right
            && p.Y >= info.rcWork.Top && p.Y < info.rcWork.Bottom;
    }
}