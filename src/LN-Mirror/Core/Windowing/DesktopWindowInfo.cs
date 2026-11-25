using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

public sealed record DesktopWindowInfo
{
    public required IntPtr Handle { get; init; }
    public required uint ProcessId { get; init; }
    public long ProcessStartTimeTicks { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public string? ProcessPath { get; init; }
    public bool IsMinimized { get; init; }
    public ImageSource? Icon { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;
    public string ProcessDisplay => $"{ProcessName} · PID {ProcessId}";
    public string StateDisplay => IsMinimized ? "已最小化" : "普通窗口";

    public override string ToString() => DisplayTitle;

    public static DesktopWindowInfo Read(IntPtr handle)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out uint processId);
        string processName = "未知应用";
        string? processPath = null;
        long startTime = 0;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            processName = process.ProcessName;
            try { processPath = process.MainModule?.FileName; } catch { }
            try { startTime = process.StartTime.ToUniversalTime().Ticks; } catch { }
        }
        catch { }

        return new DesktopWindowInfo
        {
            Handle = handle,
            ProcessId = processId,
            ProcessStartTimeTicks = startTime,
            Title = NativeMethods.GetWindowTitle(handle),
            ProcessName = processName,
            ProcessPath = processPath,
            IsMinimized = NativeMethods.IsIconic(handle),
            Icon = LoadIcon(processPath),
        };
    }

    private static ImageSource? LoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
