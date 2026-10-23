using LNMirror.Core.Diagnostics;

namespace LNMirror.Core.Adb;

public enum MdnsServiceKind
{
    /// <summary>_adb-tls-connect._tcp：可以直接 adb connect 的端口。</summary>
    Connect,

    /// <summary>_adb-tls-pairing._tcp：只在手机打开配对对话框时出现，用于 adb pair。</summary>
    Pairing,
}

/// <summary>mDNS 发现到的一项无线调试服务。实例名形如 adb-xxxx，同一台手机的配对与连接服务共享实例名。</summary>
public sealed record MdnsServiceInfo(string InstanceName, MdnsServiceKind Kind, string Address)
{
    public string KindText => Kind == MdnsServiceKind.Pairing ? "待配对" : "可连接";

    public string ActionText => Kind == MdnsServiceKind.Pairing ? "去配对" : "连接";
}

/// <summary>解析 `adb mdns services` 的输出。输出为制表符/空格分隔的三列：实例名、服务类型、ip:port。</summary>
public static class MdnsServicesParser
{
    public static IReadOnlyList<MdnsServiceInfo> Parse(string? output)
    {
        var services = new List<MdnsServiceInfo>();
        if (string.IsNullOrWhiteSpace(output)) return services;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim('\r', '\n', ' ', '\t');
            if (line.Length == 0) continue;
            if (line.StartsWith('*')) continue;
            if (line.StartsWith("List of", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var kind = parts[1] switch
            {
                var t when t.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase) => MdnsServiceKind.Connect,
                var t when t.Contains("_adb-tls-pairing._tcp", StringComparison.OrdinalIgnoreCase) => MdnsServiceKind.Pairing,
                _ => (MdnsServiceKind?)null,
            };
            if (kind is null) continue;

            var address = parts[2];
            if (!address.Contains(':')) continue;

            services.Add(new MdnsServiceInfo(parts[0], kind.Value, address));
        }

        return services;
    }
}

/// <summary>轮询 `adb mdns services`，把发现到的无线调试服务变化转成事件。仿照 DeviceMonitor 的轮询模式。</summary>
public sealed class MdnsMonitor : IAsyncDisposable
{
    private readonly AdbClient _adb;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    public MdnsMonitor(AdbClient adb, TimeSpan? interval = null)
    {
        _adb = adb;
        _interval = interval ?? TimeSpan.FromSeconds(5);
    }

    public IReadOnlyList<MdnsServiceInfo> Current { get; private set; } = Array.Empty<MdnsServiceInfo>();

    public event Action<IReadOnlyList<MdnsServiceInfo>>? SnapshotChanged;

    public event Action<Exception>? Failed;

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await PollAsync(_cts.Token).ConfigureAwait(false);
        _ = LoopAsync(_cts.Token);
    }

    /// <summary>立即刷新一次（界面“刷新”按钮用）。</summary>
    public async Task RefreshAsync(CancellationToken ct)
        => await PollAsync(ct).ConfigureAwait(false);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
                await PollAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var result = await _adb.GetMdnsServicesAsync(ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            AppLog.Debug($"[发现] adb mdns services 退出码 {result.ExitCode}，本轮跳过");
            return;
        }

        var services = MdnsServicesParser.Parse(result.StdOut);
        Current = services;
        SnapshotChanged?.Invoke(services);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
    }
}
