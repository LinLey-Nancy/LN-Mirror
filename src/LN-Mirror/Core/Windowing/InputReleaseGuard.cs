using LNMirror.Core.Diagnostics;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

/// <summary>
/// 切换输入模式（操控/穿透/恢复）前确保没有未释放的鼠标按钮或按键，
/// 避免手机端停在拖动或长按状态。没有可靠的跨进程释放手段时，宁可等用户松开。
/// </summary>
public static class InputReleaseGuard
{
    private const int PollIntervalMs = 60;
    private const int DefaultTimeoutMs = 3000;

    /// <summary>
    /// 等待所有鼠标按钮松开。返回 true 表示已确认松开，可以安全切换。
    /// </summary>
    public static async Task<bool> WaitForMouseReleaseAsync(int timeoutMs = DefaultTimeoutMs)
    {
        if (!NativeMethods.IsMouseButtonHeld()) return true;

        AppLog.Info("[输入] 检测到鼠标按钮仍处于按下状态，等待松开后再切换模式");
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(PollIntervalMs).ConfigureAwait(false);
            if (!NativeMethods.IsMouseButtonHeld())
            {
                AppLog.Info("[输入] 鼠标按钮已松开");
                return true;
            }
        }

        AppLog.Warn($"[输入] 等待鼠标松开超过 {timeoutMs / 1000.0:0.#} 秒，本次切换已放弃，请松开后重试");
        return false;
    }
}