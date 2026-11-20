using LNMirror.Core.Adb;
using LNMirror.Core.Diagnostics;

namespace LNMirror.Core.Devices;

/// <summary>轮询 adb 设备列表，把插拔、授权变化、离线等状态差异变成事件。</summary>
public sealed class DeviceMonitor : IAsyncDisposable
{
    private readonly AdbClient _adb;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private long? _lastRefresh;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, AdbDevice> _last = new(StringComparer.Ordinal);

    public DeviceMonitor(AdbClient adb, TimeSpan? interval = null)
    {
        _adb = adb;
        _interval = interval ?? TimeSpan.FromSeconds(3);
    }

    public IReadOnlyList<AdbDevice> Current { get; private set; } = Array.Empty<AdbDevice>();

    public event Action<IReadOnlyList<AdbDevice>>? SnapshotChanged;

    public event Action<AdbDevice, AdbDevice?>? DeviceChanged;

    public event Action<Exception>? Failed;

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await PollAsync(_cts.Token).ConfigureAwait(false);
        _ = LoopAsync(_cts.Token);
    }

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
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Failed?.Invoke(ex);
        }
    }

    // 后台轮询和连接等待共享同一个快照。相邻请求复用刚完成的查询。
    public async Task<IReadOnlyList<AdbDevice>> RefreshAsync(CancellationToken ct)
    {
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lastRefresh is { } last && Environment.TickCount64 - last < 300)
                return Current;

            var devices = await _adb.GetDevicesAsync(ct).ConfigureAwait(false);
            if (!Current.SequenceEqual(devices)) ApplySnapshot(devices);
            _lastRefresh = Environment.TickCount64;
            return Current;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private void ApplySnapshot(IReadOnlyList<AdbDevice> devices)
    {
        Current = devices;

        foreach (var device in devices)
        {
            if (!_last.TryGetValue(device.Serial, out var previous) || !SameState(previous, device))
            {
                DeviceChanged?.Invoke(device, previous);
            }
        }

        foreach (var serial in _last.Keys.ToList())
        {
            if (devices.All(d => !string.Equals(d.Serial, serial, StringComparison.Ordinal)))
            {
                var gone = _last[serial];
                _last.Remove(serial);
                DeviceChanged?.Invoke(gone with { State = "removed" }, gone);
            }
        }

        _last.Clear();
        foreach (var device in devices) _last[device.Serial] = device;

        SnapshotChanged?.Invoke(devices);
    }

    private static bool SameState(AdbDevice a, AdbDevice b)
        => string.Equals(a.State, b.State, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Model, b.Model, StringComparison.OrdinalIgnoreCase);

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
