using System.Diagnostics;
using System.Text;
using LNMirror.Core.Diagnostics;

namespace LNMirror.Core.Adb;

public sealed record AdbDevice(string Serial, string State, string Model, string Product, string Device, string TransportId)
{
    public bool IsReady => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    public bool IsUnauthorized => string.Equals(State, "unauthorized", StringComparison.OrdinalIgnoreCase);

    public bool IsOffline => string.Equals(State, "offline", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Serial : $"{Model} · {Serial}";

    /// <summary>给界面用的中文状态说明。unknown/offline/recovery 等状态不静默处理。</summary>
    public string StateDescription => State.ToLowerInvariant() switch
    {
        "device" => "已授权，可连接",
        "unauthorized" => "未授权，请在手机上确认这台电脑",
        "offline" => "设备离线，重新插拔数据线或重启 USB 调试",
        "recovery" => "设备处于 recovery 模式，暂不支持",
        "sideload" => "设备处于 sideload 模式，暂不支持",
        "bootloader" => "设备处于 bootloader 模式，暂不支持",
        "no" => "设备未响应 ADB",
        "host" => "这是电脑端模拟设备",
        _ => $"状态未知（{State}）",
    };
}

public sealed record AdbResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}{Environment.NewLine}{StdErr}";
}

/// <summary>对内置 adb 的调用封装。只使用自己随包携带的这一份，不做全局 kill-server。</summary>
public sealed class AdbClient
{
    private readonly string _adbPath;
    private readonly string _adbDirectory;

    public AdbClient(string adbPath)
    {
        _adbPath = adbPath;
        _adbDirectory = Path.GetDirectoryName(adbPath) ?? AppContext.BaseDirectory;
    }

    public string AdbPath => _adbPath;

    public async Task<AdbResult> RunAsync(IEnumerable<string> args, CancellationToken ct, int timeoutMs = 20000, bool log = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            WorkingDirectory = _adbDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var list = args.ToList();
        foreach (var arg in list) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new AdbResult(-1, string.Empty, "无法启动 adb");
            }
        }
        catch (Exception ex)
        {
            return new AdbResult(-1, string.Empty, ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (log) AppLog.Warn($"[ADB] 命令超时（{timeoutMs / 1000} 秒）：{Describe(list)}");
            return new AdbResult(-1, await SafeReadAsync(stdoutTask), "命令超时");
        }

        var stdout = await SafeReadAsync(stdoutTask);
        var stderr = await SafeReadAsync(stderrTask);

        if (log)
        {
            var text = FirstMeaningfulLine(stderr);
            if (text is null)
            {
                AppLog.Debug($"[ADB] {Describe(list)} → 退出码 {process.ExitCode}");
            }
            else
            {
                AppLog.Info($"[ADB] {Describe(list)} → {text}");
            }
        }

        return new AdbResult(process.ExitCode, stdout, stderr);
    }

    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken ct)
    {
        var result = await RunAsync(["devices", "-l"], ct, timeoutMs: 15000, log: false).ConfigureAwait(false);
        return ParseDevices(result.StdOut);
    }

    public static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        if (string.IsNullOrWhiteSpace(output)) return devices;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim('\r', '\n', ' ', '\t');
            if (line.Length == 0) continue;
            if (line.StartsWith('*')) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var serial = parts[0];
            var state = parts[1];
            string model = string.Empty, product = string.Empty, device = string.Empty, transport = string.Empty;

            foreach (var part in parts.Skip(2))
            {
                var idx = part.IndexOf(':');
                if (idx <= 0) continue;
                var key = part[..idx];
                var value = part[(idx + 1)..];
                switch (key)
                {
                    case "model": model = value; break;
                    case "product": product = value; break;
                    case "device": device = value; break;
                    case "transport_id": transport = value; break;
                }
            }

            devices.Add(new AdbDevice(serial, state, model, product, device, transport));
        }

        return devices;
    }

    public async Task<string?> GetPropertyAsync(string serial, string property, CancellationToken ct)
    {
        var result = await RunAsync(["-s", serial, "shell", "getprop", property], ct, timeoutMs: 10000, log: false).ConfigureAwait(false);
        if (!result.Ok) return null;
        var value = result.StdOut.Trim();
        return value.Length == 0 ? null : value;
    }

    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        var result = await RunAsync(["version"], ct, timeoutMs: 10000, log: false).ConfigureAwait(false);
        var line = FirstMeaningfulLine(result.StdOut) ?? "未知";
        return line.Trim();
    }

    /// <summary>局域网连接（下一阶段使用）。当前版本不自动调用。</summary>
    public async Task<AdbResult> ConnectAsync(string address, CancellationToken ct)
    {
        var result = await RunAsync(["connect", address], ct).ConfigureAwait(false);
        var line = FirstMeaningfulLine(result.Combined);
        AppLog.Info($"[ADB] 局域网连接 {address}：{line ?? result.ExitCode.ToString()}");
        return result;
    }

    /// <summary>无线调试配对。配对码只用于本次命令，不写入日志。</summary>
    public async Task<AdbResult> PairAsync(string address, string pairCode, CancellationToken ct)
    {
        var result = await RunAsync(["pair", address, pairCode], ct).ConfigureAwait(false);
        var line = FirstMeaningfulLine(result.Combined);
        AppLog.Info($"[ADB] 无线调试配对 {address}：{line ?? result.ExitCode.ToString()}（配对码不记录）");
        return result;
    }

    private static string Describe(IReadOnlyList<string> args)
        => string.Join(' ', args.Select(a => a.Length > 6 && a.StartsWith("pair") ? "pair …" : a));

    private static string? FirstMeaningfulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('*')) continue;
            return line;
        }

        return null;
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已退出
        }
    }
}