using System.Text;
using LNMirror.Core;
using LNMirror.Core.Configuration;
using LNMirror.Core.Interop;
using LNMirror.Core.Scrcpy;
using LNMirror.Core.Windowing;

namespace LNMirror.Core.Diagnostics;

/// <summary>
/// 环境自检与诊断信息。透明与穿透的结论必须区分"代码路径可用"与"实机已验证"，
/// 不把进程内验证说成实机通过。
/// </summary>
public static class SelfTest
{
    private sealed record Check(string Name, string Result, bool Ok);

    public static async Task<string> RunAsync(AppController? controller = null)
    {
        var checks = new List<Check>();

        checks.Add(new Check("运行环境",
            $"{Environment.OSVersion.VersionString} · {(Environment.Is64BitProcess ? "x64" : "x86")} · 程序版本 {VersionText()}",
            true));

        checks.Add(CheckConfigWritable());

        var runtime = ScrcpyRuntime.Discover();
        if (runtime is null)
        {
            checks.Add(new Check("内置 scrcpy", "未找到完整的内置引擎目录（需要 scrcpy.exe、adb.exe、scrcpy-server）", false));
        }
        else
        {
            checks.Add(new Check("内置 scrcpy", $"{runtime.VersionLabel} · {runtime.Directory}", runtime.IsComplete));
            checks.Add(new Check("内置 scrcpy-server", File.Exists(runtime.Server) ? "存在" : "缺失", File.Exists(runtime.Server)));
            checks.Add(new Check("内置 adb", File.Exists(runtime.Adb) ? "存在" : "缺失", File.Exists(runtime.Adb)));

            var version = await RunCaptureAsync(runtime.Executable, "--version", Path.GetDirectoryName(runtime.Executable)!);
            checks.Add(new Check("scrcpy 版本", FirstLine(version), version.StartsWith("scrcpy", StringComparison.OrdinalIgnoreCase)));

            var adbVersion = await RunCaptureAsync(runtime.Adb, "version", Path.GetDirectoryName(runtime.Adb)!);
            checks.Add(new Check("adb 版本", FirstLine(adbVersion), adbVersion.Contains("Android Debug Bridge", StringComparison.OrdinalIgnoreCase)));

            var devices = await RunCaptureAsync(runtime.Adb, "devices -l", Path.GetDirectoryName(runtime.Adb)!);
            var deviceLines = devices.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0
                            && !l.StartsWith('*')
                            && !l.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                .ToList();
            string deviceText = deviceLines.Count == 0 ? "未发现设备" : string.Join("；", deviceLines);
            checks.Add(new Check("当前设备", deviceText, deviceLines.Count > 0));
        }

        checks.Add(CheckLayeredAlphaRoundTrip());

        if (controller is not null)
        {
            checks.Add(new Check("当前连接状态",
                $"{controller.PhaseText} · {controller.StatusLine}", controller.IsConnected));

            if (controller.IsConnected && controller.Window.IsValid)
            {
                var alpha = controller.Window.ApplyOpacity(controller.Opacity);
                checks.Add(new Check("画面窗口不透明度读回",
                    alpha is null ? "读不到 alpha（窗口可能已失效）" : $"设定 {controller.Opacity}% → 实际 alpha {alpha}",
                    alpha is not null));

                var check = PassThroughVerifier.VerifyAndLog(controller.Window);
                checks.Add(new Check("画面窗口鼠标穿透",
                    $"{check.Display}（{check.Detail}）",
                    check.Verdict == PassThroughVerdict.Effective));
            }
            else if (controller.IsConnected)
            {
                checks.Add(new Check("画面窗口", "尚未取得 scrcpy 画面窗口句柄", false));
            }
        }

        var report = new StringBuilder();
        report.AppendLine("LN-Mirror 环境自检");
        report.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine(new string('─', 46));

        foreach (var check in checks)
        {
            report.AppendLine($"{(check.Ok ? "[正常]" : "[注意]")} {check.Name}：{check.Result}");
        }

        report.AppendLine(new string('─', 46));
        report.AppendLine("说明：进程内的透明与穿透验证只证明 Win32 调用链可用。");
        report.AppendLine("scrcpy 画面窗口在真实显卡驱动下的透明效果与鼠标穿透，");
        report.AppendLine("仍需连接手机后按《M0 验证清单》肉眼确认；程序不会替你假定通过。");
        report.AppendLine($"日志目录：{SettingsStore.LogDirectory}");

        var text = report.ToString();
        AppLog.Info("[自检]\n" + text);
        return text;
    }

    public static string BuildDiagnostics(AppController controller)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"LN-Mirror 诊断信息 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"程序版本：{VersionText()}");
        sb.AppendLine($"系统：{Environment.OSVersion.VersionString}（{(Environment.Is64BitProcess ? "x64" : "x86")}）");
        sb.AppendLine($"引擎：{controller.EngineText}");
        sb.AppendLine($"ADB：{controller.AdbVersionText}");
        sb.AppendLine($"档位：{controller.QualitySummary}；渲染后端：{controller.RenderDriver}；无边框：{controller.Borderless}");
        sb.AppendLine($"状态：{controller.PhaseText} · {controller.StatusLine}");
        sb.AppendLine($"画面：不透明度 {controller.Opacity}% · {(controller.PassThrough ? "鼠标穿透" : "操控手机")} · 置顶 {(controller.Topmost ? "开" : "关")} · 引擎帧率 {controller.FpsText} · 尺寸 {controller.WindowInfo}");
        sb.AppendLine($"穿透验证：{controller.PassThroughCheck}");

        sb.AppendLine("设备：");
        if (controller.Devices.Count == 0)
        {
            sb.AppendLine("  （无）");
        }
        else
        {
            foreach (var device in controller.Devices)
            {
                sb.AppendLine($"  {device.Serial} · {device.State} · {device.Model}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("最近日志：");
        foreach (var line in AppLog.Snapshot(LogLevel.Info, 40))
        {
            sb.AppendLine("  " + line.Format());
        }

        return sb.ToString();
    }

    private static Check CheckConfigWritable()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.AppDataDirectory);
            var probe = Path.Combine(SettingsStore.AppDataDirectory, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new Check("配置目录可写", SettingsStore.AppDataDirectory, true);
        }
        catch (Exception ex)
        {
            return new Check("配置目录可写", $"写入失败：{ex.Message}", false);
        }
    }

    /// <summary>
    /// 在一个非 WPF 托管的窗口上验证 layered alpha 的读写往返，只证明 Win32 调用链可用。
    /// 探针刻意不用 WPF 窗口：WPF 自己管理窗口样式，外部设置的 WS_EX_LAYERED 会被它改回去，
    /// 那样测出来的失败与 scrcpy 画面窗口（普通 Win32/SDL 窗口）无关。
    /// </summary>
    private static Check CheckLayeredAlphaRoundTrip()
    {
        System.Windows.Forms.Form? probe = null;
        try
        {
            probe = new System.Windows.Forms.Form
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, 0),
                Size = new System.Drawing.Size(200, 200),
                ShowInTaskbar = false,
                BackColor = System.Drawing.Color.Gray,
            };

            probe.Show();
            var hwnd = probe.Handle;
            if (hwnd == IntPtr.Zero)
            {
                return new Check("整窗 alpha 读写往返（进程内）", "探针窗口没有创建成功", false);
            }

            var results = new List<string>();
            bool allOk = true;

            foreach (var percent in new[] { 100, 50, 1, 0 })
            {
                int ex = NativeMethods.GetWindowExStyle(hwnd) | NativeMethods.WS_EX_LAYERED;
                NativeMethods.SetWindowExStyle(hwnd, ex);
                bool styleKept = (NativeMethods.GetWindowExStyle(hwnd) & NativeMethods.WS_EX_LAYERED) != 0;

                byte expected = (byte)Math.Round(percent * 255.0 / 100.0, MidpointRounding.AwayFromZero);
                bool set = NativeMethods.SetLayeredWindowAttributes(hwnd, 0, expected, NativeMethods.LWA_ALPHA);
                bool read = NativeMethods.GetLayeredWindowAttributes(hwnd, out _, out byte actual, out _);
                bool ok = set && read && actual == expected && styleKept;
                allOk &= ok;

                results.Add($"{percent}%→期望{expected}/实际{(read ? actual.ToString() : "读不到")}/设置{(set ? "成功" : "失败")}/图层样式{(styleKept ? "已生效" : "被改回")}");
            }

            return new Check("整窗 alpha 读写往返（进程内）",
                string.Join("，", results) + "；仅证明调用链可用，不等于 scrcpy 画面窗口已验证", allOk);
        }
        catch (Exception ex)
        {
            return new Check("整窗 alpha 读写往返（进程内）", $"验证过程出错：{ex.Message}", false);
        }
        finally
        {
            probe?.Close();
            probe?.Dispose();
        }
    }

    private static async Task<string> RunCaptureAsync(string fileName, string arguments, string workingDirectory)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return string.Empty;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output;
        }
        catch (Exception ex)
        {
            return $"执行失败：{ex.Message}";
        }
    }

    private static string FirstLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('*')) continue;
            return line;
        }

        return text.Trim();
    }

    private static string VersionText()
        => typeof(SelfTest).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}