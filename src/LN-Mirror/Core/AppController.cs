using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using LNMirror.Core.Adb;
using LNMirror.Core.Configuration;
using LNMirror.Core.Devices;
using LNMirror.Core.Diagnostics;
using LNMirror.Core.HotKeys;
using LNMirror.Core.Scrcpy;
using LNMirror.Core.Windowing;

namespace LNMirror.Core;

public enum ConnectionPhase
{
    Idle,
    NoDevice,
    Unauthorized,
    Offline,
    Connecting,
    Connected,
    Failed,
}

/// <summary>
/// 应用状态的唯一来源：设备、连接、外观与交互设置都从这里出发。
/// 界面只做展示与输入，不直接操作 scrcpy 或 Win32。
/// </summary>
public sealed class AppController : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings _settings;
    private readonly MirrorWindowController _window = new();
    private readonly MirrorHoverWatcher _hoverWatcher;
    private readonly Dispatcher _dispatcher;

    private ScrcpyRuntime? _runtime;
    private AdbClient? _adb;
    private DeviceMonitor? _monitor;
    private MdnsMonitor? _mdns;
    private ScrcpySession? _session;
    private HotKeyService? _hotKeys;

    private ConnectionPhase _phase = ConnectionPhase.Idle;
    private AdbDevice? _selectedDevice;
    private AdbDevice? _connectedDevice;
    private WirelessTarget? _connectedWirelessTarget;
    private string _connectionMode;
    private CancellationTokenSource? _reconnectCts;
    private (int Width, int Height)? _videoSize;
    private int _opacity;
    private int _effectiveOpacity;
    private bool _hoverRevealEnabled;
    private bool _hoverRevealActive;
    private bool _passThrough;
    private bool _topmost;
    private double? _fps;
    private long? _skippedFrames;
    private string _sessionParameters = "—";
    private string _statusLine = "正在初始化…";
    private string _notice = string.Empty;
    private CancellationTokenSource? _connectionCts;
    private ScrcpySession? _stoppingSession;
    private bool _isBusy;
    private bool _toolbarVisible;
    private string _passThroughCheck = "尚未验证";
    private string _windowInfo = "—";
    private bool _mirrorMinimized;
    private bool _disposed;
    private CancellationTokenSource? _lifetimeCts;

    public AppController(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _opacity = settings.Opacity is >= 1 and <= 100 ? settings.Opacity : 100;
        _effectiveOpacity = _opacity;
        _topmost = settings.Topmost;
        _passThrough = false;
        _connectionMode = settings.ConnectionMode;
        _hoverRevealEnabled = settings.HoverRevealEnabled;

        _hoverWatcher = new MirrorHoverWatcher(() => _window.GetContentRect());
        _hoverWatcher.HoverChanged += OnHoverChanged;
        ApplyHoverDelay();

        foreach (var entry in settings.LanDevices.OrderByDescending(d => d.LastConnectedAt))
        {
            LanHistory.Add(entry);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppSettings Settings => _settings;

    /// <summary>
    /// 启动时是否自动恢复上次的画面。冒烟检查会关掉它，
    /// 免得测试实例去抢正在跑的那个会话、把手机端的投屏接管过去。
    /// </summary>
    public bool AllowAutoReconnect { get; set; } = true;

    public MirrorWindowController Window => _window;

    public ObservableCollection<AdbDevice> Devices { get; } = new();

    public IReadOnlyList<QualityProfile> QualityProfiles => QualityProfile.All;

    public IReadOnlyList<string> RenderDriverOptions { get; } = ["auto", "direct3d", "opengl", "software"];

    public bool LanAvailable => true;

    public ObservableCollection<MdnsServiceInfo> DiscoveredLan { get; } = new();

    public ObservableCollection<LanDeviceEntry> LanHistory { get; } = new();

    /// <summary>当前连接方式页签（Usb / Lan），持久化到配置。</summary>
    public string ConnectionMode
    {
        get => _connectionMode;
        set
        {
            value = value == ConnectionModes.Lan ? ConnectionModes.Lan : ConnectionModes.Usb;
            if (!Set(ref _connectionMode, value)) return;
            _settings.ConnectionMode = value;
            SaveSettings();
            OnPropertyChanged(nameof(IsUsbMode));
            OnPropertyChanged(nameof(IsLanMode));
        }
    }

    public bool IsUsbMode
    {
        get => _connectionMode == ConnectionModes.Usb;
        set { if (value) ConnectionMode = ConnectionModes.Usb; }
    }

    public bool IsLanMode
    {
        get => _connectionMode == ConnectionModes.Lan;
        set { if (value) ConnectionMode = ConnectionModes.Lan; }
    }

    public bool LanAutoReconnect
    {
        get => _settings.LanAutoReconnect;
        set
        {
            if (_settings.LanAutoReconnect == value) return;
            _settings.LanAutoReconnect = value;
            if (!value && CancelReconnect() && !IsBusy) Notice = "已取消自动重连。";
            SaveSettings();
            OnPropertyChanged(nameof(LanAutoReconnect));
        }
    }

    public ConnectionPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            OnPropertyChanged(nameof(PhaseText));
            OnPropertyChanged(nameof(PhaseHint));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanDisconnect));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(OpacityEnabled));
            OnPropertyChanged(nameof(PassThroughEnabled));
        }
    }

    public string PhaseText => _phase switch
    {
        ConnectionPhase.Idle => "未初始化",
        ConnectionPhase.NoDevice => "未发现设备",
        ConnectionPhase.Unauthorized => "等待手机授权",
        ConnectionPhase.Offline => "设备离线",
        ConnectionPhase.Connecting => "正在连接",
        ConnectionPhase.Connected => "已连接",
        _ => "连接失败",
    };

    public string PhaseHint => _phase switch
    {
        ConnectionPhase.NoDevice => "请检查数据线是否为数据线、手机是否开启 USB 调试，并确认已选择驱动正常的设备。",
        ConnectionPhase.Unauthorized => "请解锁手机，在弹出的授权框里勾选“始终允许”并确定。程序不会反复重启连接。",
        ConnectionPhase.Offline => "设备已离线，请重新插拔数据线；仍无反应可重启手机 USB 调试或换一根线。",
        ConnectionPhase.Connecting => "正在启动画面，请稍候…",
        ConnectionPhase.Connected => "画面已建立。工具条与托盘始终可以找回画面。",
        ConnectionPhase.Failed => _statusLine,
        _ => string.Empty,
    };

    public bool IsConnected => _phase == ConnectionPhase.Connected;

    public bool CanConnect => !_isBusy && !IsConnected && _selectedDevice is not null;

    public bool CanDisconnect => IsBusy || IsConnected || _reconnectCts is { IsCancellationRequested: false };

    public string ConnectedDeviceText => _connectedDevice?.DisplayName ?? string.Empty;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanDisconnect));
            // OpacityEnabled / PassThroughEnabled 都依赖 !IsBusy，且 IsBusy 在 Phase=Connected 之后才复位
            OnPropertyChanged(nameof(OpacityEnabled));
            OnPropertyChanged(nameof(PassThroughEnabled));
        }
    }

    public AdbDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(SelectedDeviceText));
            OnPropertyChanged(nameof(SelectedDeviceStateText));
            RefreshPhaseFromDevice();
        }
    }

    public string SelectedDeviceText => _selectedDevice?.DisplayName ?? "尚未选择设备";

    public string SelectedDeviceStateText => _selectedDevice?.StateDescription ?? "插入数据线后会自动出现";

    public int Opacity
    {
        get => _opacity;
        set => SetOpacity(value);
    }

    public bool OpacityEnabled => IsConnected && !_isBusy;

    /// <summary>当前真正作用在画面上的不透明度。悬停显示时与设置值不同。</summary>
    public int EffectiveOpacity => _effectiveOpacity;

    /// <summary>开启悬停显示后，画面常态完全透明，鼠标移到画面上才按设置值显示。</summary>
    public bool HoverRevealEnabled
    {
        get => _hoverRevealEnabled;
        set => SetHoverRevealEnabled(value);
    }

    /// <summary>悬停显示模式下，画面是否已显示。</summary>
    public bool HoverRevealActive => _hoverRevealActive;

    public int HoverCustomDelayMs => _settings.HoverCustomDelayMs;

    public string HoverDelayLabel => _settings.HoverDelayMode switch
    {
        HoverDelayModes.Off => "关闭",
        HoverDelayModes.OneSecond => "1 s",
        HoverDelayModes.TwoSeconds => "2 s",
        HoverDelayModes.Custom => $"自定义 · {HoverCustomDelayMs / 1000m:0.###} s",
        _ => "智能",
    };

    public void SetHoverDelay(string mode, int? customMilliseconds = null)
    {
        mode = HoverDelayModes.Normalize(mode);
        int milliseconds = customMilliseconds ?? HoverCustomDelayMs;
        if (milliseconds is < HoverDelayModes.MinimumCustomMilliseconds or > HoverDelayModes.MaximumCustomMilliseconds)
            return;
        if (_settings.HoverDelayMode == mode && HoverCustomDelayMs == milliseconds) return;

        _settings.HoverDelayMode = mode;
        _settings.HoverCustomDelayMs = milliseconds;
        ApplyHoverDelay();
        SaveSettings();
        OnPropertyChanged(nameof(HoverDelayLabel));
    }

    private void ApplyHoverDelay()
        => _hoverWatcher.SetDelay(HoverDelayModes.GetMilliseconds(_settings.HoverDelayMode, HoverCustomDelayMs),
            _settings.HoverDelayMode == HoverDelayModes.Smart);

    /// <summary>不透明度滑块的标题：悬停显示时它决定的是"显示出来时"的不透明度。</summary>
    public string OpacityLabel => _hoverRevealEnabled ? "悬停显示" : "不透明度";

    /// <summary>给界面看的当前状态说明。</summary>
    public string OpacityStateText => _hoverRevealEnabled
        ? (_hoverRevealActive ? $"显示中 {_effectiveOpacity}%" : "隐藏中 · 不接收点击")
        : (_effectiveOpacity == 0 ? "0% · 不接收点击" : $"{_effectiveOpacity}%");

    public IReadOnlyList<HintOption> HoverRevealHintOptions => HoverRevealHints.All;

    /// <summary>画面看不见时用什么方式提示它的位置。</summary>
    public string HoverRevealHint
    {
        get => _settings.HoverRevealHint;
        set
        {
            if (string.Equals(_settings.HoverRevealHint, value, StringComparison.Ordinal)) return;
            _settings.HoverRevealHint = value;
            SaveSettings();
            OnPropertyChanged(nameof(HoverRevealHint));
        }
    }

    public bool PassThrough
    {
        get => _passThrough;
        set => _ = SetPassThroughAsync(value);
    }

    public bool PassThroughEnabled => IsConnected && !_isBusy;

    public string PassThroughText => _passThrough ? "鼠标穿透" : "操控手机";

    /// <summary>工具条上那个开关的文字，直接说明当前处于哪种模式。</summary>
    public string PassThroughButtonText => _passThrough ? "鼠标穿透" : "操控手机";

    /// <summary>工具条上置顶开关的文字。</summary>
    public string TopmostButtonText => $"置顶 {TopmostText}";

    public bool Topmost
    {
        get => _topmost;
        set => _ = SetTopmostAsync(value);
    }

    public string TopmostText => _topmost ? "开" : "关";

    public double? Fps
    {
        get => _fps;
        private set
        {
            if (!Set(ref _fps, value)) return;
            OnPropertyChanged(nameof(FpsText));
        }
    }

    /// <summary>scrcpy 输出里解析到的视频纹理尺寸（悬停边框按比例缩放用）。</summary>
    public (int Width, int Height)? VideoSize
    {
        get => _videoSize;
        private set
        {
            if (Set(ref _videoSize, value)) OnPropertyChanged(nameof(VideoSizeText));
        }
    }

    public string VideoSizeText => _videoSize is { } size ? $"{size.Width}×{size.Height}" : "—";

    public string FpsText => _fps is null ? "—" : $"{_fps:0.0}";

    public string SkippedFramesText => _skippedFrames?.ToString() ?? "—";

    public string SessionParameters
    {
        get => _sessionParameters;
        private set => Set(ref _sessionParameters, value);
    }

    public string WindowInfo
    {
        get => _windowInfo;
        private set => Set(ref _windowInfo, value);
    }

    public string StatusLine
    {
        get => _statusLine;
        private set => Set(ref _statusLine, value);
    }

    public string Notice
    {
        get => _notice;
        set => Set(ref _notice, value);
    }

    public bool ToolbarVisible
    {
        get => _toolbarVisible;
        private set => Set(ref _toolbarVisible, value);
    }

    public string PassThroughCheck
    {
        get => _passThroughCheck;
        private set => Set(ref _passThroughCheck, value);
    }

    public bool MirrorMinimized
    {
        get => _mirrorMinimized;
        set => Set(ref _mirrorMinimized, value);
    }

    public string EngineText => _runtime is null
        ? "未找到内置 scrcpy"
        : $"{_runtime.VersionLabel} · {_runtime.Directory}";

    public string QualityDisplay => QualityProfile.Resolve(_settings.Quality).DisplayName;

    public string QualitySummary => QualityProfile.Resolve(_settings.Quality).Summary;

    public string Quality
    {
        get => _settings.Quality;
        set
        {
            if (string.Equals(_settings.Quality, value, StringComparison.Ordinal)) return;
            _settings.Quality = value;
            SaveSettings();
            OnPropertyChanged(nameof(Quality));
            OnPropertyChanged(nameof(QualityDisplay));
            OnPropertyChanged(nameof(QualitySummary));
        }
    }

    public IReadOnlyList<int> WirelessBufferDurations => WirelessVideoBuffer.Durations;

    public int WirelessVideoBufferMs
    {
        get => _settings.ResolvedWirelessVideoBufferMs;
        set
        {
            value = WirelessVideoBuffer.Normalize(value);
            if (WirelessVideoBufferMs == value) return;
            _settings.WirelessVideoBufferMs = value;
            _settings.WirelessSmoothPlayback = false;
            SaveSettings();
            OnPropertyChanged(nameof(WirelessVideoBufferMs));
        }
    }

    public string RenderDriver
    {
        get => _settings.RenderDriver;
        set
        {
            if (string.Equals(_settings.RenderDriver, value, StringComparison.Ordinal)) return;
            _settings.RenderDriver = value;
            SaveSettings();
            OnPropertyChanged(nameof(RenderDriver));
        }
    }

    public bool Borderless
    {
        get => _settings.Borderless;
        set
        {
            if (_settings.Borderless == value) return;
            _settings.Borderless = value;
            SaveSettings();
            OnPropertyChanged(nameof(Borderless));
            // 运行时直接改窗口样式，改完立刻生效，不用重连
            if (IsConnected) _window.ApplyBorderless(value);
        }
    }

    public bool KeepAwake
    {
        get => _settings.KeepAwake;
        set
        {
            if (_settings.KeepAwake == value) return;
            _settings.KeepAwake = value;
            SaveSettings();
            OnPropertyChanged(nameof(KeepAwake));
        }
    }

    public bool HoverFrameEnabled
    {
        get => _settings.HoverFrameEnabled;
        set
        {
            if (_settings.HoverFrameEnabled == value) return;
            _settings.HoverFrameEnabled = value;
            SaveSettings();
            OnPropertyChanged(nameof(HoverFrameEnabled));
        }
    }

    public bool VerifyPassThroughOnSwitch
    {
        get => _settings.VerifyPassThroughOnSwitch;
        set
        {
            if (_settings.VerifyPassThroughOnSwitch == value) return;
            _settings.VerifyPassThroughOnSwitch = value;
            SaveSettings();
            OnPropertyChanged(nameof(VerifyPassThroughOnSwitch));
        }
    }

    public bool ToolbarFollowsMirror
    {
        get => _settings.ToolbarFollowsMirror;
        set
        {
            if (_settings.ToolbarFollowsMirror == value) return;
            _settings.ToolbarFollowsMirror = value;
            SaveSettings();
            OnPropertyChanged(nameof(ToolbarFollowsMirror));
        }
    }

    public string HotkeyRestore
    {
        get => _settings.RestoreHotKey;
        set
        {
            if (string.Equals(_settings.RestoreHotKey, value, StringComparison.OrdinalIgnoreCase)) return;
            _settings.RestoreHotKey = value;
            SaveSettings();
            OnPropertyChanged(nameof(HotkeyRestore));
        }
    }

    public string HotkeyPassThrough
    {
        get => _settings.PassThroughHotKey;
        set
        {
            if (string.Equals(_settings.PassThroughHotKey, value, StringComparison.OrdinalIgnoreCase)) return;
            _settings.PassThroughHotKey = value;
            SaveSettings();
            OnPropertyChanged(nameof(HotkeyPassThrough));
        }
    }

    public string AdbVersionText { get; private set; } = "—";

    public string AppVersionText { get; private set; } = typeof(AppController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ---------- 初始化 ----------

    public async Task InitializeAsync()
    {
        AppLog.Initialize(SettingsStore.LogDirectory);
        AppLog.Info($"[应用] LN-Mirror 启动 · 版本 {AppVersionText} · {Environment.OSVersion.VersionString} · {(Environment.Is64BitProcess ? "x64" : "x86")}");

        _lifetimeCts = new CancellationTokenSource();

        _runtime = ScrcpyRuntime.Discover();
        if (_runtime is null)
        {
            AppLog.Error("[引擎] 未找到完整的内置 scrcpy（需要 scrcpy.exe、adb.exe、scrcpy-server），投屏功能不可用");
            StatusLine = "未找到内置 scrcpy 引擎";
            Phase = ConnectionPhase.Failed;
            return;
        }

        _adb = new AdbClient(_runtime.Adb);
        AdbVersionText = await _adb.GetVersionAsync(_lifetimeCts.Token).ConfigureAwait(true);
        AppLog.Info($"[ADB] {_adb.AdbPath} → {AdbVersionText}");

        _monitor = new DeviceMonitor(_adb);
        _monitor.SnapshotChanged += OnDevicesSnapshot;
        _monitor.DeviceChanged += OnDeviceChanged;
        _monitor.Failed += ex => AppLog.Warn($"[设备] 轮询失败：{ex.Message}");

        await _monitor.StartAsync(_lifetimeCts.Token).ConfigureAwait(true);

        _mdns = new MdnsMonitor(_adb);
        _mdns.SnapshotChanged += OnMdnsSnapshot;
        _mdns.Failed += ex => AppLog.Debug($"[发现] mDNS 轮询失败：{ex.Message}");
        await _mdns.StartAsync(_lifetimeCts.Token).ConfigureAwait(true);

        _hotKeys = new HotKeyService();
        _hotKeys.HotKeyPressed += OnHotKeyPressed;
        RegisterHotKeys();

        Phase = Devices.Count == 0 ? ConnectionPhase.NoDevice : _phase;
        AppLog.Info("[应用] 初始化完成");

        if (AllowAutoReconnect && LanAutoReconnect && _settings.PreferredSerial is { } preferred
            && AdbDevice.IsWirelessSerial(preferred))
        {
            var history = FindPreferredHistory(preferred);
            var target = history is null ? WirelessTarget.FromSerial(preferred, MdnsServices) : TargetFromHistory(history);
            _ = ReconnectAsync(preferred, target, startup: true);
        }
    }

    private void RegisterHotKeys()
    {
        if (_hotKeys is null) return;

        bool restoreOk = _hotKeys.Register("恢复 100", _settings.RestoreHotKey);
        bool passOk = _hotKeys.Register("穿透切换", _settings.PassThroughHotKey);

        if (!restoreOk || !passOk)
        {
            Notice = "有全局快捷键被其它程序占用，注册失败；托盘与工具条入口仍然可用。";
        }
    }

    // ---------- 设备 ----------

    private void OnDevicesSnapshot(IReadOnlyList<AdbDevice> devices)
    {
        _dispatcher.Invoke(() =>
        {
            ApplyDeviceRows(devices, MdnsServices);
            ApplyDiscoveredRows(MdnsServices);
        });
    }

    private void ApplyDeviceRows(IReadOnlyList<AdbDevice> devices, IReadOnlyList<MdnsServiceInfo> services)
    {
        var previous = SelectedDevice;
        var display = devices.Where(d => !HasPreferredWirelessAlias(d, devices, services)).ToList();
        if (!Devices.SequenceEqual(display))
        {
            Devices.Clear();
            foreach (var device in display) Devices.Add(device);
        }

        var preferred = previous?.Serial ?? _settings.PreferredSerial;
        var selected = Devices.FirstOrDefault(d => d.Serial == preferred);
        if (selected is null && preferred is not null && AdbDevice.IsWirelessSerial(preferred))
        {
            var target = WirelessTarget.FromSerial(preferred, services);
            selected = target.FindDevice(Devices, target.ResolveAddress(services), preferAddress: false);
        }
        SelectedDevice = selected
            ?? Devices.FirstOrDefault(d => d.IsReady)
            ?? Devices.FirstOrDefault();
        if (!IsConnected) RefreshPhaseFromDevice();
    }

    private static bool HasPreferredWirelessAlias(AdbDevice device, IReadOnlyList<AdbDevice> devices,
        IReadOnlyList<MdnsServiceInfo> services)
    {
        if (!AdbDevice.IsWirelessSerial(device.Serial)) return false;
        var target = WirelessTarget.FromSerial(device.Serial, services);
        if (target.InstanceName is null) return false;

        var address = target.ResolveAddress(services);
        var preferred = target.FindDevice(devices, address, preferAddress: false);
        return preferred?.IsReady == true && preferred.Serial != device.Serial;
    }

    private void OnDeviceChanged(AdbDevice device, AdbDevice? previous)
    {
        _dispatcher.Invoke(() =>
        {
            AppLog.Info($"[设备] {device.DisplayName}：{previous?.State ?? "新设备"} → {device.State}");

            if (IsConnected && device.Serial == _connectedDevice?.Serial
                && device.State != "device")
            {
                var reason = device.State switch
                {
                    "unauthorized" => "设备授权已撤销，画面已停止",
                    "offline" => "设备已离线，画面已停止",
                    "removed" => "设备已拔出，画面已停止",
                    _ => $"设备状态变为 {device.State}，画面已停止",
                };
                if (_session is { } session)
                {
                    var retry = device.State is "removed" or "offline";
                    _ = HandleDroppedSessionAsync(session, reason, retry);
                }
            }
            else if (!IsConnected)
            {
                RefreshPhaseFromDevice();
            }
        });
    }

    private void RefreshPhaseFromDevice()
    {
        // 连接中与已连接时不允许设备轮询把阶段降级（曾导致启动重连瞬间被清回待连接）
        if (IsBusy || IsConnected) return;

        if (_selectedDevice is null)
        {
            Phase = ConnectionPhase.NoDevice;
            StatusLine = Devices.Count == 0 ? "未发现设备" : "请选择要连接的设备";
            return;
        }

        if (_selectedDevice.IsReady)
        {
            Phase = ConnectionPhase.Idle;
            StatusLine = $"{TransportName(_selectedDevice.Serial)} · {_selectedDevice.DisplayName} · 就绪";
        }
        else if (_selectedDevice.IsUnauthorized)
        {
            Phase = ConnectionPhase.Unauthorized;
            StatusLine = "设备已连接但尚未授权";
        }
        else
        {
            Phase = ConnectionPhase.Offline;
            StatusLine = _selectedDevice.StateDescription;
        }
    }

    private static string TransportName(string serial)
        => AdbDevice.IsWirelessSerial(serial) ? "局域网" : "USB";

    // ---------- 局域网 ----------

    private CancellationToken LifetimeToken => _lifetimeCts?.Token ?? CancellationToken.None;

    private IReadOnlyList<MdnsServiceInfo> MdnsServices => _mdns?.Current ?? Array.Empty<MdnsServiceInfo>();
    private IReadOnlyList<AdbDevice> DeviceSnapshot => _monitor?.Current ?? Devices;

    private void OnMdnsSnapshot(IReadOnlyList<MdnsServiceInfo> services)
    {
        _dispatcher.Invoke(() =>
        {
            if (_monitor is { } monitor) ApplyDeviceRows(monitor.Current, services);
            ApplyDiscoveredRows(services);
        });
    }

    private void ApplyDiscoveredRows(IReadOnlyList<MdnsServiceInfo> services)
    {
        var rows = WirelessDebuggingDiscovery.GetDeviceRows(services);
        for (var i = 0; i < rows.Length; i++)
        {
            if (rows[i].Kind == MdnsServiceKind.Connect && HasAuthorizedTransport(rows[i]))
                rows[i] = rows[i] with { IsAuthorized = true };
        }

        if (DiscoveredLan.SequenceEqual(rows)) return;
        DiscoveredLan.Clear();
        foreach (var row in rows) DiscoveredLan.Add(row);
    }

    private bool HasAuthorizedTransport(MdnsServiceInfo service)
    {
        var target = new WirelessTarget(service.Address, service.InstanceName);
        return DeviceSnapshot.Any(device => device.IsReady && target.Matches(device, service.Address));
    }

    public Task ConnectDiscoveredLanAsync(MdnsServiceInfo service)
    {
        if (!HasAuthorizedTransport(service))
        {
            Notice = "在手机的“无线调试”页面选择“使用配对码配对设备”，然后在这条设备记录上点“去配对”。";
            return Task.CompletedTask;
        }

        if (GetAdbForLanRequest() is not { } adb) return Task.CompletedTask;
        return RunConnectionAsync(ct => ConnectLanCoreAsync(adb, new(service.Address, service.InstanceName), ct));
    }

    public async Task RefreshLanDiscoveryAsync()
    {
        if (_mdns is null) return;
        try
        {
            await _mdns.RefreshAsync(LifetimeToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[发现] 手动刷新失败：{ex.Message}");
        }
    }

    /// <summary>校验并规范化 ip:port 输入；不合法时返回 null。</summary>
    public static string? NormalizeLanAddress(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        var match = Regex.Match(text, @"^([^\s:]+):(\d{1,5})$");
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[2].Value, out var port) || port is < 1 or > 65535) return null;
        return $"{match.Groups[1].Value}:{port}";
    }

    private AdbClient? GetAdbForLanRequest()
    {
        if (IsBusy)
        {
            if (string.IsNullOrWhiteSpace(Notice)) Notice = "正在处理连接，请稍候。";
            return null;
        }

        if (IsConnected)
        {
            Notice = "投屏已连接。切换设备前请先断开当前投屏。";
            return null;
        }

        if (_adb is null) Notice = "内置 ADB 尚不可用，无法连接。";
        return _adb;
    }

    /// <summary>adb pair 配对码配对，成功后自动连接并启动画面。</summary>
    public Task PairAndConnectAsync(string? pairAddress, string? pairCode)
    {
        if (GetAdbForLanRequest() is not { } adb) return Task.CompletedTask;

        var address = NormalizeLanAddress(pairAddress);
        var code = (pairCode ?? string.Empty).Trim();
        if (address is null)
        {
            Notice = "配对地址格式应为 IP:端口，例如 192.168.1.5:37123。";
            return Task.CompletedTask;
        }

        if (code.Length < 4)
        {
            Notice = "请输入手机配对对话框里显示的配对码。";
            return Task.CompletedTask;
        }

        return RunConnectionAsync(async ct =>
        {
            StatusLine = Notice = "正在配对…";
            // 配对对话框关闭后广播可能消失，先保存配对实例名。
            var instance = _mdns?.Current.FirstOrDefault(s => s.Kind == MdnsServiceKind.Pairing
                && string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase))?.InstanceName;
            var result = await adb.PairAsync(address, code, ct);
            if (!result.Ok || !result.Combined.Split('\n', StringSplitOptions.TrimEntries)
                    .Any(line => line.StartsWith("Successfully paired to ", StringComparison.OrdinalIgnoreCase)))
            {
                var line = result.Combined.Split('\n', StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('*'));
                FailConnection(line?.Contains("Wrong pairing code", StringComparison.OrdinalIgnoreCase) == true
                    ? "配对码错误，请核对手机配对对话框里的配对码后重试。"
                    : $"配对失败：{line ?? "未知原因"}（配对对话框关闭后请重新打开）");
                return;
            }

            AppLog.Info("[局域网] 配对成功");
            var service = await ResolveConnectServiceAsync(address, instance, ct);
            if (service is null)
            {
                Notice = "配对成功。请在“手动连接”输入手机“无线调试”主页显示的 IP:端口（与配对端口不同），然后点连接。";
                return;
            }

            await ConnectLanCoreAsync(adb, new(service.Address, service.InstanceName), ct);
        });
    }

    private async Task<MdnsServiceInfo?> ResolveConnectServiceAsync(string pairingAddress, string? instanceName, CancellationToken ct)
    {
        if (_mdns is not { } mdns) return null;

        var found = WirelessDebuggingDiscovery.FindConnectService(mdns.Current, pairingAddress, instanceName);
        if (found is not null) return found;

        await Task.Delay(1200, ct);
        await mdns.RefreshAsync(ct);
        return WirelessDebuggingDiscovery.FindConnectService(mdns.Current, pairingAddress, instanceName);
    }

    public Task ConnectLanAsync(string? rawAddress)
    {
        if (GetAdbForLanRequest() is not { } adb) return Task.CompletedTask;
        var address = NormalizeLanAddress(rawAddress);
        if (address is null)
        {
            Notice = "地址格式应为 IP:端口，例如 192.168.1.5:41537。";
            return Task.CompletedTask;
        }

        return RunConnectionAsync(ct => ConnectLanCoreAsync(adb, new(address, null), ct));
    }

    private async Task ConnectLanCoreAsync(AdbClient adb, WirelessTarget target, CancellationToken ct)
    {
        var address = NormalizeLanAddress(target.ResolveAddress(MdnsServices));
        var preferAddress = target.InstanceName is null;
        var matchTarget = preferAddress && address is not null ? WirelessTarget.FromSerial(address, MdnsServices) : target;
        var ready = matchTarget.FindDevice(DeviceSnapshot, address, preferAddress);
        if (ready?.IsReady == true)
        {
            SelectedDevice = ready;
            await StartMirrorAsync(ready, ct, target);
            return;
        }

        if (address is null)
        {
            FailConnection("尚未发现这台无线设备，请确认手机的无线调试仍开启。");
            return;
        }

        StatusLine = Notice = "正在连接局域网设备…";
        var result = await adb.ConnectAsync(address, ct);
        if (!AdbClient.IsConnectSuccess(result))
        {
            FailConnection(AdbClient.DescribeConnectFailure(result, matchTarget.InstanceName is not null));
            return;
        }

        var device = await WaitForDeviceAsync(address, TimeSpan.FromSeconds(10), ct, matchTarget, preferAddress);
        if (device?.IsReady != true)
        {
            FailConnection(matchTarget.InstanceName is not null
                ? $"无线调试设备未就绪。若这台电脑尚未配对，{AdbClient.WirelessPairingHint}"
                : device?.StateDescription ?? "设备已响应但未出现在设备列表里，请在手机上确认无线调试仍开启。");
            return;
        }
        ct.ThrowIfCancellationRequested();
        SelectedDevice = device;
        await StartMirrorAsync(device, ct, target);
    }

    /// <summary>等待串号就绪，超时后返回最后一次看到的状态。</summary>
    private async Task<AdbDevice?> WaitForDeviceAsync(string serial, TimeSpan timeout, CancellationToken ct,
        WirelessTarget? wireless = null, bool preferAddress = false)
    {
        if (_adb is null) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        AdbDevice? lastSeen = null;
        try
        {
            while (true)
            {
                try
                {
                    var devices = _monitor is { } monitor
                        ? await monitor.RefreshAsync(deadline.Token)
                        : await _adb.GetDevicesAsync(deadline.Token);
                    var device = wireless is null
                        ? devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase))
                        : wireless.FindDevice(devices, serial, preferAddress);
                    if (device?.IsReady == true) return device;
                    lastSeen = device ?? lastSeen;
                }
                catch (IOException ex)
                {
                    AppLog.Debug($"[设备] 等待就绪时查询失败：{ex.Message}");
                }
                await Task.Delay(600, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return lastSeen;
        }
    }

    public Task ConnectHistoryAsync(LanDeviceEntry? entry)
    {
        if (entry is null || GetAdbForLanRequest() is not { } adb) return Task.CompletedTask;
        var target = TargetFromHistory(entry);
        return RunConnectionAsync(ct => ConnectLanCoreAsync(adb, target, ct));
    }

    /// <summary>把已通过 USB 授权的设备切到 TCP/IP 调试（仅限可信局域网）。</summary>
    public Task SwitchUsbToLanAsync(string? rawHost)
    {
        if (GetAdbForLanRequest() is not { } adb) return Task.CompletedTask;

        var host = (rawHost ?? string.Empty).Trim();
        var address = NormalizeLanAddress(host.Contains(':') ? host : $"{host}:5555");
        if (address is null)
        {
            Notice = "请输入手机 IP，例如 192.168.1.5（也可带端口）。";
            return Task.CompletedTask;
        }

        var usb = SelectedDevice is { IsReady: true } selected && !AdbDevice.IsWirelessSerial(selected.Serial)
            ? selected
            : Devices.FirstOrDefault(d => d.IsReady && !AdbDevice.IsWirelessSerial(d.Serial));
        if (usb is null)
        {
            Notice = "请先用数据线连接已授权的手机，再执行切换。";
            return Task.CompletedTask;
        }

        var port = int.Parse(address[(address.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        return RunConnectionAsync(async ct =>
        {
            StatusLine = $"正在把 {usb.DisplayName} 切换到 TCP/IP…";
            Notice = string.Empty;
            var result = await adb.EnableTcpIpAsync(usb.Serial, port, ct);
            if (!result.Ok)
            {
                FailConnection("切换 TCP/IP 失败，请查看日志。");
                return;
            }

            await Task.Delay(1500, ct);
            await ConnectLanCoreAsync(adb, new(address, null), ct);
        });
    }

    public Task RemoveHistoryAsync(LanDeviceEntry? entry)
    {
        if (entry is null) return Task.CompletedTask;

        if (_settings.PreferredSerial is { } preferred && AdbDevice.IsWirelessSerial(preferred)
            && ReferenceEquals(FindPreferredHistory(preferred), entry))
        {
            CancelReconnect();
            _settings.PreferredSerial = null;
        }
        _settings.LanDevices.Remove(entry);
        LanHistory.Remove(entry);
        SaveSettings();
        Notice = $"已移除局域网设备 {entry.Name}。";
        return Task.CompletedTask;
    }

    private static WirelessTarget TargetFromHistory(LanDeviceEntry entry)
        => new(entry.LastAddress, string.IsNullOrWhiteSpace(entry.MdnsName) ? null : entry.MdnsName);

    private LanDeviceEntry? FindPreferredHistory(string serial)
    {
        var instance = AdbDevice.GetMdnsInstanceName(serial);
        return _settings.LanDevices.Where(entry => instance is null
                ? string.Equals(entry.LastAddress, serial, StringComparison.OrdinalIgnoreCase)
                : string.Equals(entry.MdnsName, instance, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => !string.IsNullOrWhiteSpace(entry.MdnsName))
            .ThenByDescending(entry => entry.LastConnectedAt)
            .FirstOrDefault();
    }

    private LanDeviceEntry? FindLanHistory(WirelessTarget target)
    {
        if (target.InstanceName is { } name)
            return _settings.LanDevices.FirstOrDefault(d =>
                string.Equals(d.MdnsName, name, StringComparison.OrdinalIgnoreCase));

        return _settings.LanDevices.FirstOrDefault(d =>
            string.IsNullOrEmpty(d.MdnsName) && string.Equals(d.LastAddress, target.Address, StringComparison.OrdinalIgnoreCase));
    }

    private WirelessTarget UpdateLanHistory(AdbDevice device, WirelessTarget? target)
    {
        if (target?.InstanceName is null)
            target = WirelessTarget.FromSerial(device.Serial, MdnsServices);

        var address = target.ResolveAddress(MdnsServices);
        // 只有已确认的身份才可以取代同一地址的已知设备。
        if (target.InstanceName is null && _settings.LanDevices.Any(entry => !string.IsNullOrWhiteSpace(entry.MdnsName)
                && string.Equals(entry.LastAddress, address, StringComparison.OrdinalIgnoreCase)))
            return target;

        var entry = FindLanHistory(target);
        if (entry is null && address is not null)
        {
            entry = _settings.LanDevices.FirstOrDefault(d => string.IsNullOrEmpty(d.MdnsName)
                && string.Equals(d.LastAddress, address, StringComparison.OrdinalIgnoreCase));
        }
        if (entry is null)
        {
            entry = new LanDeviceEntry();
            _settings.LanDevices.Add(entry);
        }

        entry.Name = string.IsNullOrWhiteSpace(device.Model) ? device.Serial : device.Model;
        entry.LastAddress = address ?? entry.LastAddress;
        entry.MdnsName = target.InstanceName ?? entry.MdnsName;
        entry.LastConnectedAt = DateTime.Now;
        var index = LanHistory.IndexOf(entry);
        if (index >= 0) LanHistory.RemoveAt(index);
        LanHistory.Insert(0, entry);
        _settings.PreferredSerial = target.InstanceName is { } instance
            ? instance + AdbDevice.MdnsSerialSuffix
            : device.Serial;
        return target;
    }

    private bool CancelReconnect()
    {
        if (_reconnectCts is not { IsCancellationRequested: false } reconnect) return false;
        reconnect.Cancel();
        OnPropertyChanged(nameof(CanDisconnect));
        return true;
    }

    private async Task ReconnectAsync(string serial, WirelessTarget? target, bool startup = false, Task? afterStop = null)
    {
        if (!AllowAutoReconnect || !LanAutoReconnect || _disposed)
        {
            if (afterStop is not null) await afterStop;
            return;
        }

        CancelReconnect();
        using var reconnect = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken);
        _reconnectCts = reconnect;
        OnPropertyChanged(nameof(CanDisconnect));
        var ct = reconnect.Token;
        try
        {
            if (afterStop is not null) await afterStop;
            int[] delays = startup ? [1500, 2000, 2000, 2000, 2000, 2000]
                : target is not null ? [5000, 10000, 20000] : [4000, 4000, 4000, 4000, 4000, 4000];
            for (var i = 0; i < delays.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (IsConnected || IsBusy) return;
                Notice = $"等待设备恢复，将自动重连（第 {i + 1}/{delays.Length} 次）…";
                await Task.Delay(delays[i], ct);
                if (IsConnected || IsBusy) return;

                await RunConnectionAsync(async attemptToken =>
                {
                    if (target is not null && _adb is { } adb)
                    {
                        await ConnectLanCoreAsync(adb, target, attemptToken);
                        return;
                    }

                    var device = await WaitForDeviceAsync(serial, TimeSpan.FromSeconds(1), attemptToken);
                    if (device?.IsReady != true) return;

                    SelectedDevice = device;
                    await StartMirrorAsync(device, attemptToken);
                }, ct);

                ct.ThrowIfCancellationRequested();
                if (IsConnected) return;
            }

            Notice = target is not null
                ? "自动重连未成功，请确认手机的无线调试仍开启，或使用手动连接。"
                : "自动重连未成功，请重新插拔数据线或确认手机授权。";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_reconnectCts, reconnect))
            {
                _reconnectCts = null;
                OnPropertyChanged(nameof(CanDisconnect));
            }
        }
    }

    // ---------- 连接 ----------

    public Task ConnectAsync()
    {
        if (IsConnected || IsBusy) return Task.CompletedTask;
        if (SelectedDevice is not { } device)
        {
            Notice = "请先选择设备。";
            return Task.CompletedTask;
        }

        return RunConnectionAsync(ct => StartMirrorAsync(device, ct));
    }

    private async Task RunConnectionAsync(Func<CancellationToken, Task> connect, CancellationToken retryToken = default)
    {
        if (IsBusy || _disposed) return;

        if (!retryToken.CanBeCanceled) CancelReconnect();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken, retryToken);
        _connectionCts = operation;
        IsBusy = true;
        Phase = ConnectionPhase.Connecting;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            await connect(operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Notice = "已取消连接。";
        }
        catch (Exception ex)
        {
            AppLog.Error("[连接] 建立会话时出错", ex);
            FailConnection("连接出错，请查看日志。");
        }
        finally
        {
            _connectionCts = null;
            IsBusy = false;
            if (Phase == ConnectionPhase.Connecting) RefreshPhaseFromDevice();
        }
    }

    private void FailConnection(string message)
    {
        StatusLine = Notice = message;
        Phase = ConnectionPhase.Failed;
    }

    private async Task StartMirrorAsync(AdbDevice device, CancellationToken ct, WirelessTarget? target = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!device.IsReady)
        {
            Notice = device.StateDescription;
            return;
        }
        if (_runtime is null || _adb is null)
        {
            Notice = "内置 scrcpy 引擎不可用，无法连接。";
            return;
        }

        StatusLine = Notice = "正在启动画面…";
        var placement = _settings.MirrorPlacement;
        var options = new ScrcpyStartOptions
        {
            Serial = device.Serial,
            Quality = _settings.Quality,
            WirelessVideoBufferMs = WirelessVideoBufferMs,
            Borderless = _settings.Borderless,
            RenderDriver = _settings.RenderDriver,
            KeepAwake = _settings.KeepAwake,
            WindowX = placement is { HasValue: true } p ? p.X : null,
            WindowY = placement is { HasValue: true } p2 ? p2.Y : null,
            WindowWidth = placement is { Width: > 0 } p3 ? p3.Width : null,
        };

        var session = new ScrcpySession(_runtime);
        session.OutputLine += OnScrcpyOutput;
        session.Exited += OnScrcpyExited;
        _session = session;
        try
        {
            bool ok = await session.StartAsync(options, ct);
            ct.ThrowIfCancellationRequested();
            if (!ok || !session.IsRunning)
            {
                var detail = session.LastErrorLine ?? "未取得画面，请查看日志";
                FailConnection($"启动失败：{detail}");
                return;
            }

            _window.Attach(session.WindowHandle);
            _passThrough = false;
            _window.ApplyTopmost(_topmost);
            _window.ApplyPassThrough(false);
            _window.ApplyBorderless(_settings.Borderless);
            _window.ApplyToolWindow(true);
            _hoverRevealActive = false;
            ApplyEffectiveOpacity();
            _window.MoveToVisibleArea();

            if (_hoverRevealEnabled) _hoverWatcher.Start();
            if (options.IsWireless)
                target = UpdateLanHistory(device, target);
            else
                _settings.PreferredSerial = device.Serial;
            SaveSettings();

            _connectedDevice = device;
            _connectedWirelessTarget = target;
            var bufferSummary = options.IsWireless ? $" · 视频缓冲 {options.VideoBufferMs} ms" : string.Empty;
            SessionParameters = $"{TransportName(device.Serial)} · {QualityProfile.Resolve(options.Quality).Summary}{bufferSummary}";
            StatusLine = $"{TransportName(device.Serial)} · {device.DisplayName} · 已连接";
            Notice = string.Empty;
            ToolbarVisible = true;
            PassThroughCheck = "尚未验证";
            Phase = ConnectionPhase.Connected;
            AppLog.Info($"[连接] 画面已建立：{device.DisplayName} · {SessionParameters}");
        }
        finally
        {
            if (!IsConnected && ReferenceEquals(_session, session))
                await EndSessionAsync(StatusLine, Phase, savePlacement: false);
        }
    }

    public async Task DisconnectAsync(string reason = "已手动断开")
    {
        if (CancelReconnect()) Notice = "已取消自动重连。";
        if (_connectionCts is { } operation)
        {
            operation.Cancel();
            return;
        }

        if (IsBusy || (!IsConnected && _session is null)) return;
        IsBusy = true;
        try
        {
            await EndSessionAsync(reason, ConnectionPhase.Idle);
        }
        finally
        {
            IsBusy = false;
            RefreshPhaseFromDevice();
        }
    }

    private async Task EndSessionAsync(string reason, ConnectionPhase nextPhase, bool savePlacement = true)
    {
        if (savePlacement) SavePlacement();
        var session = _session;
        _session = null;
        _connectedDevice = null;
        _connectedWirelessTarget = null;
        _window.Detach();
        _hoverWatcher.Stop();
        _hoverRevealActive = false;
        ToolbarVisible = false;
        ResetSessionDiagnostics();
        MirrorMinimized = false;
        _passThrough = false;
        _effectiveOpacity = _opacity;
        OnPropertyChanged(nameof(PassThroughText));
        OnPropertyChanged(nameof(EffectiveOpacity));
        OnPropertyChanged(nameof(OpacityStateText));
        PassThroughCheck = "尚未验证";
        StatusLine = reason;
        Phase = nextPhase;

        if (session is not null)
        {
            session.OutputLine -= OnScrcpyOutput;
            session.Exited -= OnScrcpyExited;
            _stoppingSession = session;
            try { await Task.Run(session.Dispose); }
            finally { _stoppingSession = null; }
        }
        AppLog.Info($"[会话] 结束（{reason}）");
    }

    private void OnScrcpyOutput(ScrcpySession source, string line)
    {
        if (ScrcpyFrameSample.TryParse(line, out var sample))
        {
            // 这里绝不能同步 Invoke：scrcpy 的输出是在后台线程读的，
            // 同步等界面线程会让这个后台线程一直占着 Process 对象的锁，
            // 退出时界面线程正好要释放同一个对象，两边互等就把进程卡死。
            _ = _dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_session, source)) return;
                Fps = sample.RenderedFps;
                Set(ref _skippedFrames, (_skippedFrames ?? 0) + sample.SkippedFrames, nameof(SkippedFramesText));
                UpdateStatusDetail();
            });
        }

        // Texture 行报告真实视频尺寸（含旋转变化），窗口缩放按它锁定比例，消除黑边
        var tex = Regex.Match(line, @"Texture.*?:\s*(\d+)x(\d+)");
        if (tex.Success
            && int.TryParse(tex.Groups[1].Value, out var tw)
            && int.TryParse(tex.Groups[2].Value, out var th)
            && tw > 0 && th > 0)
        {
            _ = _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_session, source)) VideoSize = (tw, th);
            });
        }
    }

    private void ResetSessionDiagnostics()
    {
        Fps = null;
        Set(ref _skippedFrames, null, nameof(SkippedFramesText));
        SessionParameters = "—";
        VideoSize = null;
        WindowInfo = "—";
    }

    private void OnScrcpyExited(ScrcpySession source, int exitCode)
    {
        // Process.Exited 在进程内部锁下触发，异步转发到界面线程。
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (!IsConnected || !ReferenceEquals(_session, source)) return;
            var reason = exitCode switch
            {
                2 => "设备连接中断，画面已停止",
                1 => "画面进程异常结束，请查看日志",
                0 => "画面窗口已被关闭，会话结束",
                _ => $"画面进程异常结束（退出码 {exitCode}）",
            };

            _ = HandleDroppedSessionAsync(source, reason, retry: exitCode == 2);
        });
    }

    private Task HandleDroppedSessionAsync(ScrcpySession source, string reason, bool retry)
    {
        if (!ReferenceEquals(_session, source)) return Task.CompletedTask;
        var device = _connectedDevice;
        var target = _connectedWirelessTarget;
        var stopped = StopDroppedSessionAsync(reason);
        return retry && device is not null ? ReconnectAsync(device.Serial, target, afterStop: stopped) : stopped;
    }

    private async Task StopDroppedSessionAsync(string reason)
    {
        IsBusy = true;
        try
        {
            await EndSessionAsync(reason, ConnectionPhase.Idle);
            if (_reconnectCts is not { IsCancellationRequested: true }) Notice = reason;
        }
        finally
        {
            IsBusy = false;
            RefreshPhaseFromDevice();
        }
    }

    // ---------- 外观与交互 ----------

    /// <summary>
    /// 设置不透明度。用户的穿透设置不受影响：画面不可见时由"自动不接收点击"规则兜底，
    /// 不会反过来把穿透开关改掉。
    /// </summary>
    public void SetOpacity(int value)
    {
        value = Math.Clamp(value, 0, 100);
        // 悬停显示时 0 等于永远不显示，没有意义，按最低 1% 记
        if (_hoverRevealEnabled) value = Math.Max(value, 1);

        if (value == _opacity && _window.IsValid) return;
        if (!Set(ref _opacity, value, nameof(Opacity))) return;

        if (value > 0 || _hoverRevealEnabled)
        {
            _settings.Opacity = value;
            SaveSettings();
        }

        ApplyEffectiveOpacity();
        UpdateStatusDetail();
    }

    /// <summary>
    /// 把真正生效的不透明度应用上去。
    /// 普通模式下就是设置值；悬停显示模式下由鼠标位置决定是 0% 还是设置值。
    /// </summary>
    private void ApplyEffectiveOpacity()
    {
        int target = _hoverRevealEnabled ? (_hoverRevealActive ? _opacity : 0) : _opacity;

        _window.ApplyOpacity(target);
        _effectiveOpacity = target;

        ApplyEffectivePassThrough();

        OnPropertyChanged(nameof(EffectiveOpacity));
        OnPropertyChanged(nameof(OpacityStateText));
    }

    /// <summary>
    /// 画面实际是否接收鼠标点击。
    /// 用户自己的穿透设置说了算；画面完全看不见时额外自动不接收点击，
    /// 免得出现"看不见却挡住下面窗口"的盲区。这条自动规则不会改写用户的开关。
    /// </summary>
    private void ApplyEffectivePassThrough()
    {
        bool effective = _passThrough || _effectiveOpacity == 0;
        _window.ApplyPassThrough(effective);
    }

    /// <summary>开关"鼠标悬停显示画面"。</summary>
    public void SetHoverRevealEnabled(bool value)
    {
        if (_hoverRevealEnabled == value) return;

        _hoverRevealEnabled = value;
        _settings.HoverRevealEnabled = value;
        SaveSettings();

        _hoverRevealActive = false;
        if (value && IsConnected) _hoverWatcher.Start();
        else _hoverWatcher.Stop();

        ApplyEffectiveOpacity();

        OnPropertyChanged(nameof(HoverRevealEnabled));
        OnPropertyChanged(nameof(HoverRevealActive));
        OnPropertyChanged(nameof(OpacityLabel));
        OnPropertyChanged(nameof(OpacityStateText));
    }

    private void OnHoverChanged(bool inside)
    {
        if (!_hoverRevealEnabled || !IsConnected) return;
        if (_hoverRevealActive == inside) return;

        _hoverRevealActive = inside;
        ApplyEffectiveOpacity();
        OnPropertyChanged(nameof(HoverRevealActive));
    }

    public async Task SetPassThroughAsync(bool value, bool force = false)
    {
        if (_passThrough == value && !force) return;
        if (!IsConnected) return;

        if (!await InputReleaseGuard.WaitForMouseReleaseAsync().ConfigureAwait(true))
        {
            Notice = "鼠标仍处于按下状态，本次切换已取消，请松开后重试。";
            // 切换没生效，界面要回到真实状态，不能停在勾上的样子
            OnPropertyChanged(nameof(PassThrough));
            OnPropertyChanged(nameof(PassThroughText));
            return;
        }

        if (_passThrough != value)
        {
            _window.CancelPointerCapture();
            if (!Set(ref _passThrough, value, nameof(PassThrough))) return;
            ApplyEffectivePassThrough();
            UpdateStatusDetail();
        }

        if (value && _settings.VerifyPassThroughOnSwitch)
        {
            await VerifyPassThroughAsync(showNotice: false).ConfigureAwait(true);
        }
    }

    public async Task TogglePassThroughAsync() => await SetPassThroughAsync(!_passThrough).ConfigureAwait(true);

    public async Task SetTopmostAsync(bool value)
    {
        if (!Set(ref _topmost, value, nameof(Topmost))) return;
        OnPropertyChanged(nameof(TopmostText));
        _settings.Topmost = value;
        SaveSettings();
        if (IsConnected) _window.ApplyTopmost(value);
    }

    /// <summary>一键恢复：回到 100、取消穿透、移回可见显示器、取消最小化。</summary>
    public async Task RestoreAsync()
    {
        AppLog.Info("[恢复] 执行一键恢复");

        // 悬停显示时鼠标一移开画面就消失，先把它关掉，否则恢复等于白恢复
        if (_hoverRevealEnabled) SetHoverRevealEnabled(false);

        await SetPassThroughAsync(false, force: true).ConfigureAwait(true);

        if (!Set(ref _opacity, 100, nameof(Opacity)))
        {
            OnPropertyChanged(nameof(Opacity));
        }

        _settings.Opacity = 100;
        SaveSettings();

        ApplyEffectiveOpacity();

        _window.RestoreIfMinimized();
        _window.MoveToVisibleArea();
        MirrorMinimized = false;

        OnPropertyChanged(nameof(Opacity));
        UpdateStatusDetail();
        Notice = "已恢复为 100% 并取消穿透。";
    }

    public async Task VerifyPassThroughAsync(bool showNotice = true)
    {
        var session = _session;
        if (!IsConnected || session is null)
        {
            PassThroughCheck = "尚未连接，无法验证";
            OnPropertyChanged(nameof(PassThroughCheck));
            return;
        }

        await Task.Delay(20).ConfigureAwait(true);
        if (!ReferenceEquals(_session, session)) return;

        var check = PassThroughVerifier.VerifyAndLog(_window);
        PassThroughCheck = $"{check.Display}（{check.Detail}）";
        OnPropertyChanged(nameof(PassThroughCheck));
        if (showNotice) Notice = PassThroughCheck;
    }

    /// <summary>档位、渲染后端、无边框等需要重启 scrcpy 的设置。</summary>
    public Task ApplyAndReconnectAsync(string reason)
    {
        if (IsBusy || !IsConnected || _connectedDevice is not { } device) return Task.CompletedTask;
        var target = _connectedWirelessTarget;

        return RunConnectionAsync(async ct =>
        {
            await EndSessionAsync($"应用设置：{reason}", ConnectionPhase.Connecting);
            ct.ThrowIfCancellationRequested();
            if (target is not null && _adb is { } adb)
                await ConnectLanCoreAsync(adb, target, ct);
            else
                await StartMirrorAsync(device, ct);
        });
    }

    public async Task UpdateHotKeysAsync(string restoreText, string passThroughText)
    {
        bool changed = !string.Equals(_settings.RestoreHotKey, restoreText, StringComparison.OrdinalIgnoreCase)
                       || !string.Equals(_settings.PassThroughHotKey, passThroughText, StringComparison.OrdinalIgnoreCase);

        _settings.RestoreHotKey = restoreText;
        _settings.PassThroughHotKey = passThroughText;
        SaveSettings();

        if (!changed) return;

        _hotKeys?.UnregisterAll();
        RegisterHotKeys();
        await Task.CompletedTask;
    }

    // ---------- 位置与自恢复 ----------

    public void SavePlacement()
    {
        var rect = _window.GetRect();
        if (rect is null) return;

        _settings.MirrorPlacement = new WindowPlacement
        {
            X = rect.Value.Left,
            Y = rect.Value.Top,
            Width = rect.Value.Width,
            HasValue = true,
        };

        SaveSettings();
    }

    /// <summary>记下工具条自己拖出来的位置，下次启动还在那里。</summary>
    public void SaveToolbarPlacement(int x, int y)
    {
        _settings.ToolbarPlacement = new WindowPlacement { X = x, Y = y, HasValue = true };
        SaveSettings();
    }

    public async Task EnsureWindowRecoverableAsync()
    {
        await Task.Delay(10).ConfigureAwait(true);
        if (!IsConnected) return;

        if (_window.MoveToVisibleArea())
        {
            Notice = "画面窗口已移回可见显示器。";
        }

        if (_window.RestoreIfMinimized())
        {
            MirrorMinimized = false;
        }
    }

    private void OnHotKeyPressed(string name)
    {
        _dispatcher.Invoke(async () =>
        {
            if (name == "恢复 100")
            {
                await RestoreAsync().ConfigureAwait(true);
            }
            else if (name == "穿透切换")
            {
                await TogglePassThroughAsync().ConfigureAwait(true);
            }
        });
    }

    private void UpdateStatusDetail()
    {
        var rect = _window.GetRect();
        if (rect is not null) WindowInfo = $"{rect.Value.Width}×{rect.Value.Height}";
    }

    public void SaveSettings() => SettingsStore.Save(_settings);

    /// <summary>
    /// 退出清理。会被退出流程和 OnExit 各调一次，所以必须能重复调用。
    /// 每一步单独兜住异常：一步出错不能连累后面的步骤，否则会留下没关掉的窗口或孤儿进程。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { SavePlacement(); }
        catch (Exception ex) { AppLog.Warn($"[应用] 退出时保存画面位置失败：{ex.Message}"); }

        try { _hoverWatcher.Stop(); }
        catch { /* 忽略 */ }

        try
        {
            CancelReconnect();
            _connectionCts?.Cancel();
            _stoppingSession?.ForceStop();
            _session?.Dispose();
            _session = null;
        }
        catch (Exception ex) { AppLog.Warn($"[应用] 退出时结束投屏会话失败：{ex.Message}"); }

        try { _window.Dispose(); }
        catch { /* 忽略 */ }

        try { _hotKeys?.Dispose(); }
        catch { /* 忽略 */ }

        // 先取消再释放：设备轮询任务挂在同一个令牌上，反过来会抛 ObjectDisposedException
        try { _lifetimeCts?.Cancel(); }
        catch { /* 忽略 */ }

        // 设备轮询和 mDNS 轮询都是后台任务，取消后它们自己会退出。
        // 这里只给一个很短的等待窗口，不做无限期同步等待——退出时卡在等待上比多跑两秒更糟。
        try { _monitor?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1)); }
        catch { /* 忽略 */ }

        try { _mdns?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1)); }
        catch { /* 忽略 */ }

        try { _lifetimeCts?.Dispose(); _lifetimeCts = null; }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 退出兜底：立刻结束投屏进程，不等它正常收尾，
    /// 免得强制退出后桌面留下一个孤儿画面窗口。
    /// </summary>
    public void ForceStopSession()
    {
        try { (_session ?? _stoppingSession)?.ForceStop(); }
        catch { /* 忽略 */ }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
