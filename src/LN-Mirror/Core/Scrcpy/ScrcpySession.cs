using System.Diagnostics;
using System.IO;
using System.Text;
using LNMirror.Core.Adb;
using LNMirror.Core.Diagnostics;
using LNMirror.Core.Interop;
using LNMirror.Core.Windowing;

namespace LNMirror.Core.Scrcpy;

/// <summary>
/// 一次投屏会话：由本工具启动的 scrcpy 进程 + 它自己的画面窗口。
/// 只结束自己启动的进程，不影响其它 scrcpy 会话，也不使用全局 adb kill-server。
/// </summary>
public sealed class ScrcpySession : IDisposable
{
    private static readonly TimeSpan WindowWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(4);

    private readonly ScrcpyRuntime _runtime;
    private Process? _process;
    private int _exitCode = -1;
    private bool _disposed;

    public ScrcpySession(ScrcpyRuntime runtime)
    {
        _runtime = runtime;
    }

    public IntPtr WindowHandle { get; private set; }

    public int ProcessId => _process?.Id ?? 0;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>scrcpy 输出行（标准输出与错误合并）。</summary>
    public event Action<string>? OutputLine;

    /// <summary>进程退出，参数为退出码。</summary>
    public event Action<int>? Exited;

    public async Task<bool> StartAsync(ScrcpyStartOptions options, CancellationToken ct)
    {
        var args = ScrcpyRuntime.BuildArguments(options);

        var psi = new ProcessStartInfo
        {
            FileName = _runtime.Executable,
            WorkingDirectory = _runtime.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        // 固定使用随包携带的 adb 与 server，保证电脑端与手机端版本组合一致
        psi.Environment["ADB"] = _runtime.Adb;
        psi.Environment["SCRCPY_SERVER_PATH"] = _runtime.Server;
        var currentPath = psi.Environment.TryGetValue("PATH", out var p) ? p : string.Empty;
        psi.Environment["PATH"] = _runtime.Directory + Path.PathSeparator + currentPath;

        AppLog.Info($"[会话] 启动 scrcpy（设备 {options.Serial}，档位 {QualityProfile.Resolve(options.Quality).DisplayName}）");
        AppLog.Debug($"[会话] 命令行：scrcpy {string.Join(' ', args)}");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                AppLog.Error("[会话] scrcpy 进程启动失败");
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("[会话] 无法启动 scrcpy", ex);
            return false;
        }

        _process = process;
        _exitCode = -1;

        process.Exited += OnProcessExited;
        _ = PumpAsync(process.StandardOutput, "输出");
        _ = PumpAsync(process.StandardError, "错误");

        WindowHandle = await WaitForWindowAsync(process.Id, ct).ConfigureAwait(true);

        if (WindowHandle != IntPtr.Zero)
        {
            AppLog.Info($"[会话] 已找到画面窗口（进程 {process.Id}）");
        }
        else if (process.HasExited)
        {
            AppLog.Error($"[会话] scrcpy 未能建立画面窗口，退出码 {process.ExitCode}");
        }
        else
        {
            AppLog.Warn("[会话] 在预期时间内没有找到画面窗口");
        }

        return WindowHandle != IntPtr.Zero;
    }

    public void Stop()
    {
        var process = _process;
        if (process is null) return;

        try
        {
            if (process.HasExited)
            {
                AppLog.Debug("[会话] scrcpy 已退出，无需结束");
                return;
            }

            // 优先关闭它自己的画面窗口，让 scrcpy 走正常的清理流程（移除手机端 server、恢复设备状态）
            bool closed = false;
            if (WindowHandle != IntPtr.Zero && NativeMethods.IsWindow(WindowHandle))
            {
                NativeMethods.PostMessageW(WindowHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                closed = process.WaitForExit((int)StopTimeout.TotalMilliseconds);
            }

            if (!closed)
            {
                process.CloseMainWindow();
                closed = process.WaitForExit((int)StopTimeout.TotalMilliseconds);
            }

            if (!closed)
            {
                AppLog.Warn("[会话] scrcpy 未在预期时间内退出，强制结束（手机端可能残留一次未清理的会话）");
                process.Kill(entireProcessTree: true);
            }
            else
            {
                AppLog.Info("[会话] scrcpy 已正常退出");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[会话] 结束 scrcpy 时出错：{ex.Message}");
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 忽略
            }
        }
        finally
        {
            WindowHandle = IntPtr.Zero;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int code;
        try
        {
            code = _process?.ExitCode ?? -1;
        }
        catch
        {
            code = -1;
        }

        _exitCode = code;
        AppLog.Info($"[会话] scrcpy 进程结束，退出码 {code}");
        Exited?.Invoke(code);
    }

    private async Task PumpAsync(StreamReader reader, string channel)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                AppLog.Debug($"[scrcpy {channel}] {line}");
                OutputLine?.Invoke(line);
            }
        }
        catch
        {
            // 管道关闭属正常结束
        }
    }

    private async Task<IntPtr> WaitForWindowAsync(int processId, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)WindowWaitTimeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var handle = FindMirrorWindow(processId);
            if (handle != IntPtr.Zero) return handle;

            if (_process is { HasExited: true }) return IntPtr.Zero;

            await Task.Delay(120).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }

    /// <summary>按进程号找画面窗口，不依赖窗口标题匹配。</summary>
    private static IntPtr FindMirrorWindow(int processId)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != (uint)processId) return true;
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return true;

            long area = (long)rect.Width * rect.Height;
            if (area < 4000) return true;

            var title = NativeMethods.GetWindowTitle(hwnd);
            var className = NativeMethods.GetWindowClass(hwnd);
            bool looksLikeMirror =
                title.Contains("scrcpy", StringComparison.OrdinalIgnoreCase)
                || title.Contains("LN-Mirror", StringComparison.OrdinalIgnoreCase)
                || className.StartsWith("SDL", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeMirror) return true;

            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }
    }
}