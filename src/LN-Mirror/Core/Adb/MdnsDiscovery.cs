using LNMirror.Core.Diagnostics;

namespace LNMirror.Core.Adb;

public enum MdnsServiceKind
{
    /// <summary>_adb-tls-connect._tcp：无线调试的连接端口。广播本身不代表已配对。</summary>
    Connect,

    /// <summary>_adb-tls-pairing._tcp：只在手机打开配对对话框时出现，用于 adb pair。</summary>
    Pairing,
}

/// <summary>mDNS 发现到的一项无线调试服务。配对与连接端口的实例名可能带不同后缀。</summary>
public sealed record MdnsServiceInfo(string InstanceName, MdnsServiceKind Kind, string Address)
{
    public bool IsAuthorized { get; init; }

    public string KindText => Kind == MdnsServiceKind.Pairing ? "待配对" : IsAuthorized ? "已授权" : "已发现";

    public string ActionText => Kind == MdnsServiceKind.Pairing ? "去配对" : IsAuthorized ? "连接" : "如何配对";
}

internal sealed record WirelessTarget(string? Address, string? InstanceName)
{
    public static WirelessTarget FromSerial(string serial, IReadOnlyList<MdnsServiceInfo> services)
    {
        if (AdbDevice.GetMdnsInstanceName(serial) is { } instance)
            return new(null, instance);

        return new(serial, WirelessDebuggingDiscovery.FindByAddress(services, serial)?.InstanceName);
    }

    public string? ResolveAddress(IReadOnlyList<MdnsServiceInfo> services)
    {
        if (InstanceName is null) return Address;
        if (Address is not null && string.Equals(
                WirelessDebuggingDiscovery.FindByAddress(services, Address)?.InstanceName,
                InstanceName, StringComparison.OrdinalIgnoreCase))
            return Address;

        var service = WirelessDebuggingDiscovery.FindByInstance(services, InstanceName);
        return service is not null && WirelessDebuggingDiscovery.FindByAddress(services, service.Address) is not null
            ? service.Address
            : null;
    }

    public bool Matches(AdbDevice device, string? resolvedAddress)
        => (InstanceName is not null && string.Equals(InstanceName, AdbDevice.GetMdnsInstanceName(device.Serial),
                StringComparison.OrdinalIgnoreCase))
            || (resolvedAddress is not null && string.Equals(resolvedAddress, device.Serial, StringComparison.OrdinalIgnoreCase));

    public AdbDevice? FindDevice(IReadOnlyList<AdbDevice> devices, string? address, bool preferAddress)
    {
        AdbDevice? alternate = null;
        AdbDevice? unready = null;
        foreach (var device in devices)
        {
            if (!Matches(device, address)) continue;
            if (!device.IsReady)
            {
                unready = device;
                continue;
            }

            bool matchesAddress = string.Equals(device.Serial, address, StringComparison.OrdinalIgnoreCase);
            if (matchesAddress == preferAddress) return device;
            alternate ??= device;
        }
        return alternate ?? unready;
    }
}

internal static class WirelessDebuggingDiscovery
{
    public static MdnsServiceInfo? FindByAddress(IReadOnlyList<MdnsServiceInfo> services, string address)
        => Unique(services.Where(s => s.Kind == MdnsServiceKind.Connect
            && string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase)), s => s.InstanceName);

    public static MdnsServiceInfo? FindByInstance(IReadOnlyList<MdnsServiceInfo> services, string instanceName)
        => Unique(services.Where(s => s.Kind == MdnsServiceKind.Connect
            && string.Equals(s.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase)), s => s.Address);

    private static MdnsServiceInfo? Unique(IEnumerable<MdnsServiceInfo> services, Func<MdnsServiceInfo, string> key)
    {
        var matches = services.DistinctBy(key, StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static MdnsServiceInfo[] GetDeviceRows(IReadOnlyList<MdnsServiceInfo> services)
    {
        var replacedAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pairing in services.Where(service => service.Kind == MdnsServiceKind.Pairing))
        {
            var connection = FindConnectService(services, pairing.Address, pairing.InstanceName);
            if (connection is not null) replacedAddresses.Add(connection.Address);
        }

        return services
            .Where(service => service.Kind == MdnsServiceKind.Pairing || !replacedAddresses.Contains(service.Address))
            .DistinctBy(service => (service.Kind, service.Address))
            .OrderBy(service => GetHost(service.Address), StringComparer.OrdinalIgnoreCase)
            .ThenBy(service => service.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static MdnsServiceInfo? FindConnectService(
        IReadOnlyList<MdnsServiceInfo> services, string pairingAddress, string? pairingInstance)
    {
        var host = GetHost(pairingAddress);
        var candidates = services.Where(service => service.Kind == MdnsServiceKind.Connect
            && !string.Equals(service.Address, pairingAddress, StringComparison.OrdinalIgnoreCase)
            && string.Equals(GetHost(service.Address), host, StringComparison.OrdinalIgnoreCase));

        // ADB 的配对服务与连接服务可能各自带 mDNS 后缀。只有地址唯一时才按 IP 回退。
        var exact = Unique(candidates.Where(service =>
            string.Equals(service.InstanceName, pairingInstance, StringComparison.OrdinalIgnoreCase)), s => s.Address);
        return exact ?? Unique(candidates, s => s.Address);
    }

    private static string GetHost(string address) => address[..address.LastIndexOf(':')];
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
    private readonly SemaphoreSlim _refresh = new(1, 1);
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
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _adb.GetMdnsServicesAsync(ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                AppLog.Debug($"[发现] adb mdns services 退出码 {result.ExitCode}，本轮跳过");
                return;
            }

            var services = MdnsServicesParser.Parse(result.StdOut);
            if (Current.SequenceEqual(services)) return;

            Current = services;
            SnapshotChanged?.Invoke(services);
        }
        finally
        {
            _refresh.Release();
        }
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
