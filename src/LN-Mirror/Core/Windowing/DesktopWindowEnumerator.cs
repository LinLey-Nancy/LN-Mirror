using System.Diagnostics;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

public static class DesktopWindowEnumerator
{
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "DV2ControlHost", "SysShadow",
    };

    public static IReadOnlyList<DesktopWindowInfo> Enumerate(IntPtr includeHandle = default)
    {
        var windows = new List<DesktopWindowInfo>();
        uint currentProcess = (uint)Environment.ProcessId;
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (ShouldInclude(handle, currentProcess, includeHandle))
            {
                try { windows.Add(DesktopWindowInfo.Read(handle)); }
                catch { }
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public static bool ShouldInclude(
        IntPtr handle,
        uint currentProcessId,
        IntPtr includeHandle,
        bool isVisible,
        bool isCloaked,
        bool hasOwner,
        string title,
        string className,
        int exStyle,
        uint processId)
    {
        if (handle == IntPtr.Zero || processId == currentProcessId || !isVisible || isCloaked || hasOwner) return false;
        if (string.IsNullOrWhiteSpace(title) || ExcludedClasses.Contains(className)) return false;
        if (handle != includeHandle && (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;
        return true;
    }

    private static bool ShouldInclude(IntPtr handle, uint currentProcessId, IntPtr includeHandle)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out uint processId);
        bool cloaked = NativeMethods.DwmGetWindowAttribute(
            handle, NativeMethods.DWMWA_CLOAKED, out int cloakedValue, sizeof(int)) == 0 && cloakedValue != 0;
        return ShouldInclude(
            handle,
            currentProcessId,
            includeHandle,
            NativeMethods.IsWindowVisible(handle),
            cloaked,
            NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER) != IntPtr.Zero,
            NativeMethods.GetWindowTitle(handle),
            NativeMethods.GetWindowClass(handle),
            NativeMethods.GetWindowExStyle(handle),
            processId);
    }
}
