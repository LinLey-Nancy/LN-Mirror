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
    private static readonly Regex[] FpsPatterns =
    [
        // 兼容不同版本的写法：59.9 fps / fps: 59.9 / Frame rate: 59.9
        new(@"(?i)([0-9]+(?:\.[0-9]+)?)\s*fps\b", RegexOptions.Compiled),
        new(@"(?i)\bfps\b\s*[:=]?\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled),
        new(@"(?i)frame\s*rate\s*[:=]?\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled),
    ];

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
    private string? _connectedSerial;
    private string _connectionMode;
    private int _lanReconnecting;
    private (int Width, int Height)? _videoSize;
    private int _opacity;
    private int _effectiveOpacity;
    private bool _hoverRevealEnabled;
    private bool _hoverRevealActive;
    private bool _passThrough;
    private bool _topmost;
    private double? _fps;
    private string _statusLine = "正在初始化…";
    private string _notice = string.Empty;
    private string _lastErrorLine = string.Empty;
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

    public bool CanDisconnect => _isBusy || IsConnected;

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

    /// <summary>悬停显示模式下，鼠标此刻是否正落在画面范围内。</summary>
    public bool HoverRevealActive => _hoverRevealActive;

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
        private set => Set(ref _videoSize, value);
    }

    public string FpsText => _fps is null ? "—" : $"{_fps:0.0}";

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

        if (AllowAutoReconnect
            && _settings.LanAutoReconnect
            && _settings.PreferredSerial is { } preferred
            && preferred.Contains(':'))
        {
            _ = TryStartupReconnectAsync(preferred);
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
            var previous = SelectedDevice;

            // 同一台手机可能同时以 mDNS 串号（adb-xxx._adb-tls-connect._tcp）和 IP/USB 串号出现两次。
            // mDNS 串号不含冒号，选它连接会被误判成 USB 会话（不记录历史、不走局域网自动重连），
            // 因此存在同型号的其它串号时不把它放进可选列表。
            var display = devices
                .Where(d => !IsDuplicateMdnsSerial(d, devices))
                .ToList();

            Devices.Clear();
            foreach (var device in display) Devices.Add(device);

            if (previous is not null)
            {
                SelectedDevice = Devices.FirstOrDefault(d => d.Serial == previous.Serial) ?? null;
            }

            if (SelectedDevice is null)
            {
                var preferred = Devices.FirstOrDefault(d => d.Serial == _settings.PreferredSerial);
                SelectedDevice = preferred
                    ?? Devices.FirstOrDefault(d => d.IsReady)
                    ?? Devices.FirstOrDefault();
            }

            if (!IsConnected) RefreshPhaseFromDevice();
        });
    }

    private static bool IsDuplicateMdnsSerial(AdbDevice device, IReadOnlyList<AdbDevice> all)
    {
        if (!device.Serial.Contains("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(device.Model)) return false;

        return all.Any(other =>
            !ReferenceEquals(other, device)
            && !other.Serial.Contains("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)
            && string.Equals(other.Model, device.Model, StringComparison.OrdinalIgnoreCase));
    }

    private void OnDeviceChanged(AdbDevice device, AdbDevice? previous)
    {
        _dispatcher.Invoke(() =>
        {
            AppLog.Info($"[设备] {device.DisplayName}：{previous?.State ?? "新设备"} → {device.State}");

            if (IsConnected && _connectedSerial is not null
                && device.Serial == _connectedSerial
                && device.State != "device")
            {
                var reason = device.State switch
                {
                    "unauthorized" => "设备授权已撤销，画面已停止",
                    "offline" => "设备已离线，画面已停止",
                    "removed" => "设备已拔出，画面已停止",
                    _ => $"设备状态变为 {device.State}，画面已停止",
                };
                EndSession(reason, ConnectionPhase.NoDevice);
                Notice = reason;

                // 设备轮询往往比 scrcpy 退出码先发现断线，重连必须从这里触发；
                // 授权被撤销属于用户主动行为，不自动重连
                if (_settings.LanAutoReconnect && device.State is "removed" or "offline")
                {
                    if (device.Serial.Contains(':'))
                    {
                        _ = ReconnectLanLoopAsync(device.Serial);
                    }
                    else
                    {
                        _ = ReconnectUsbLoopAsync(device.Serial);
                    }
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
            Phase = Devices.Count == 0 ? ConnectionPhase.NoDevice : ConnectionPhase.NoDevice;
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
        => serial.Contains(':') ? "局域网" : "USB";

    // ---------- 局域网 ----------

    private CancellationToken LifetimeToken => _lifetimeCts?.Token ?? CancellationToken.None;

    private void OnMdnsSnapshot(IReadOnlyList<MdnsServiceInfo> services)
    {
        _dispatcher.Invoke(() =>
        {
            DiscoveredLan.Clear();
            foreach (var service in services) DiscoveredLan.Add(service);
        });
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

    /// <summary>adb pair 配对码配对，成功后自动连接并启动画面。</summary>
    public async Task PairAndConnectAsync(string? pairAddress, string? pairCode)
    {
        if (_adb is null || IsBusy || IsConnected) return;

        var address = NormalizeLanAddress(pairAddress);
        var code = (pairCode ?? string.Empty).Trim();
        if (address is null)
        {
            Notice = "配对地址格式应为 IP:端口，例如 192.168.1.5:37123。";
            return;
        }

        if (code.Length < 4)
        {
            Notice = "请输入手机配对对话框里显示的配对码。";
            return;
        }

        IsBusy = true;
        Phase = ConnectionPhase.Connecting;
        StatusLine = "正在配对…";
        Notice = string.Empty;

        string? connectAddress = null;
        string? failure = null;
        try
        {
            // 记下配对服务的 mDNS 实例名，成功后用它找对应的连接服务（连接端口与配对端口不同）
            var instance = _mdns?.Current
                .FirstOrDefault(s => s.Kind == MdnsServiceKind.Pairing
                                     && string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase))
                ?.InstanceName;

            var result = await _adb.PairAsync(address, code, LifetimeToken).ConfigureAwait(true);
            if (!result.Combined.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            {
                var line = result.Combined.Split('\n')
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('*'));
                failure = line is not null && line.Contains("Wrong pairing code", StringComparison.OrdinalIgnoreCase)
                    ? "配对码错误，请核对手机配对对话框里的配对码后重试。"
                    : $"配对失败：{line ?? "未知原因"}（配对对话框关闭后配对码会失效，需要重新打开）";
            }
            else
            {
                AppLog.Info("[局域网] 配对成功");
                connectAddress = await ResolveConnectAddressAsync(instance).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }

        if (failure is not null)
        {
            StatusLine = failure;
            Phase = ConnectionPhase.Failed;
            Notice = failure;
            return;
        }

        if (connectAddress is null)
        {
            RefreshPhaseFromDevice();
            Notice = "配对成功。请在“手动连接”输入手机“无线调试”主页显示的 IP:端口（与配对端口不同），然后点连接。";
            return;
        }

        Notice = "配对成功，正在连接…";
        await ConnectLanAsync(connectAddress).ConfigureAwait(true);
    }

    /// <summary>按 mDNS 实例名找连接服务地址；找不到时刷新一次发现列表再试。</summary>
    private async Task<string?> ResolveConnectAddressAsync(string? instanceName)
    {
        if (string.IsNullOrEmpty(instanceName) || _mdns is null) return null;

        var found = _mdns.Current.FirstOrDefault(s => s.Kind == MdnsServiceKind.Connect
                                                      && s.InstanceName == instanceName)?.Address;
        if (found is not null) return found;

        try
        {
            await Task.Delay(1200, LifetimeToken).ConfigureAwait(true);
            await _mdns.RefreshAsync(LifetimeToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[发现] 配对后刷新失败：{ex.Message}");
        }

        return _mdns.Current.FirstOrDefault(s => s.Kind == MdnsServiceKind.Connect
                                                 && s.InstanceName == instanceName)?.Address;
    }

    /// <summary>adb connect 一个局域网地址，成功后启动画面。</summary>
    public async Task ConnectLanAsync(string? rawAddress)
    {
        if (_adb is null || IsBusy || IsConnected) return;

        var address = NormalizeLanAddress(rawAddress);
        if (address is null)
        {
            Notice = "地址格式应为 IP:端口，例如 192.168.1.5:41537。";
            return;
        }

        IsBusy = true;
        Phase = ConnectionPhase.Connecting;
        StatusLine = "正在连接局域网设备…";
        Notice = string.Empty;

        string? failure = null;
        AdbDevice? device = null;
        try
        {
            var result = await _adb.ConnectAsync(address, LifetimeToken).ConfigureAwait(true);
            if (!AdbClient.IsConnectSuccess(result))
            {
                failure = AdbClient.DescribeConnectFailure(result);
            }
            else
            {
                device = await WaitForDeviceAsync(address, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                if (device is null)
                {
                    failure = "设备已响应但未出现在设备列表里，请在手机上确认无线调试仍开启。";
                }
                else if (!device.IsReady)
                {
                    failure = device.IsUnauthorized
                        ? "设备已连接但未授权：请在手机上确认这台电脑的调试授权。"
                        : device.StateDescription;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        if (failure is not null || device is null)
        {
            StatusLine = failure ?? "连接失败。";
            Phase = ConnectionPhase.Failed;
            Notice = StatusLine;
            return;
        }

        SelectedDevice = device;
        await ConnectAsync().ConfigureAwait(true);
    }

    /// <summary>轮询设备列表，等待指定串号出现（最多 timeout）。</summary>
    private async Task<AdbDevice?> WaitForDeviceAsync(string serial, TimeSpan timeout)
    {
        if (_adb is null) return null;
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            LifetimeToken.ThrowIfCancellationRequested();

            var devices = await _adb.GetDevicesAsync(LifetimeToken).ConfigureAwait(true);
            var device = devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));
            if (device is not null) return device;

            await Task.Delay(600, LifetimeToken).ConfigureAwait(true);
        }

        return null;
    }

    /// <summary>连接历史设备：先试最近地址，失败时按 mDNS 实例名重新解析地址再试一次。</summary>
    public async Task ConnectHistoryAsync(LanDeviceEntry? entry)
    {
        if (entry is null || IsBusy || IsConnected) return;

        await ConnectLanAsync(entry.LastAddress).ConfigureAwait(true);
        if (IsConnected) return;

        var resolved = _mdns?.Current.FirstOrDefault(s => s.Kind == MdnsServiceKind.Connect
                                                          && s.InstanceName == entry.MdnsName)?.Address;
        if (resolved is null || string.Equals(resolved, entry.LastAddress, StringComparison.OrdinalIgnoreCase)) return;

        AppLog.Info($"[局域网] {entry.Name} 地址已变化：{entry.LastAddress} → {resolved}，按新地址重试");
        Notice = "设备地址已变化，正在按新地址重试…";
        await ConnectLanAsync(resolved).ConfigureAwait(true);
    }

    /// <summary>备用路径：把已通过 USB 授权的设备切到 TCP/IP 调试（默认 5555 端口），再按手机 IP 直连。仅限可信局域网。</summary>
    public async Task SwitchUsbToLanAsync(string? rawHost)
    {
        if (_adb is null || IsBusy || IsConnected) return;

        var host = (rawHost ?? string.Empty).Trim();
        string? address = null;
        var port = 5555;
        if (host.Length > 0 && !host.Contains(' ') && host.IndexOf(':') < 0)
        {
            address = $"{host}:{port}";
        }
        else if (host.Length > 0)
        {
            address = NormalizeLanAddress(host);
            if (address is not null)
            {
                port = int.Parse(address[(address.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (address is null)
        {
            Notice = "请输入手机 IP，例如 192.168.1.5（也可带端口）。";
            return;
        }

        var usb = SelectedDevice is { IsReady: true } selected && !selected.Serial.Contains(':')
            ? selected
            : Devices.FirstOrDefault(d => d.IsReady && !d.Serial.Contains(':'));
        if (usb is null)
        {
            Notice = "请先用数据线连接已授权的手机，再执行切换。";
            return;
        }

        IsBusy = true;
        Phase = ConnectionPhase.Connecting;
        StatusLine = $"正在把 {usb.DisplayName} 切换到 TCP/IP…";
        Notice = string.Empty;

        AdbResult switchResult;
        try
        {
            switchResult = await _adb.EnableTcpIpAsync(usb.Serial, port, LifetimeToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        if (!switchResult.Ok)
        {
            StatusLine = "切换 TCP/IP 失败，请查看日志。";
            Phase = ConnectionPhase.Failed;
            Notice = StatusLine;
            return;
        }

        // 切换后设备会先断开再监听端口，稍等再连
        try
        {
            await Task.Delay(1500, LifetimeToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Notice = "已切换 TCP/IP，正在连接…";
        await ConnectLanAsync(address).ConfigureAwait(true);
    }

    public async Task RemoveHistoryAsync(LanDeviceEntry? entry)
    {
        if (entry is null) return;

        if (_adb is not null && !string.IsNullOrWhiteSpace(entry.LastAddress))
        {
            try
            {
                await _adb.DisconnectAsync(entry.LastAddress, LifetimeToken).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Debug($"[局域网] 断开 {entry.LastAddress} 时出错：{ex.Message}");
            }
        }

        _settings.LanDevices.RemoveAll(d => string.Equals(d.LastAddress, entry.LastAddress, StringComparison.OrdinalIgnoreCase));
        LanHistory.Remove(entry);
        SaveSettings();
        Notice = $"已移除局域网设备 {entry.Name}。";
    }

    /// <summary>局域网连接成功后写入/更新历史设备。</summary>
    private void UpdateLanHistory(AdbDevice device)
    {
        var serial = device.Serial;
        var mdnsName = _mdns?.Current
            .FirstOrDefault(s => s.Kind == MdnsServiceKind.Connect
                                 && string.Equals(s.Address, serial, StringComparison.OrdinalIgnoreCase))
            ?.InstanceName ?? string.Empty;
        var name = string.IsNullOrWhiteSpace(device.Model) ? serial : device.Model;

        var entry = _settings.LanDevices.FirstOrDefault(d =>
            string.Equals(d.LastAddress, serial, StringComparison.OrdinalIgnoreCase)
            || (mdnsName.Length > 0 && d.MdnsName == mdnsName));
        if (entry is null)
        {
            entry = new LanDeviceEntry();
            _settings.LanDevices.Add(entry);
        }

        entry.Name = name;
        entry.LastAddress = serial;
        if (mdnsName.Length > 0) entry.MdnsName = mdnsName;
        entry.LastConnectedAt = DateTime.Now;
        SaveSettings();

        LanHistory.Clear();
        foreach (var e in _settings.LanDevices.OrderByDescending(d => d.LastConnectedAt))
        {
            LanHistory.Add(e);
        }
    }

    /// <summary>启动时自动重连上次的局域网设备（含直接恢复画面）。失败静默回到手动入口。</summary>
    private async Task TryStartupReconnectAsync(string serial)
    {
        try
        {
            await Task.Delay(1500, LifetimeToken).ConfigureAwait(false);
            if (LifetimeToken.IsCancellationRequested) return;

            await _dispatcher.InvokeAsync(async () =>
            {
                if (IsConnected || IsBusy) return;
                Notice = "正在自动重连上次的局域网设备…";
                await ConnectLanAsync(serial).ConfigureAwait(true);
            }).Task.Unwrap();

            if (IsConnected)
            {
                AppLog.Info("[局域网] 启动自动重连成功");
                return;
            }

            // 直连失败多半是端口已变化。mDNS 发现需要几秒，等待期间按实例名反复解析新地址
            var entry = _settings.LanDevices.FirstOrDefault(d =>
                string.Equals(d.LastAddress, serial, StringComparison.OrdinalIgnoreCase));

            for (int i = 0; i < 5 && !IsConnected && !LifetimeToken.IsCancellationRequested; i++)
            {
                await Task.Delay(2000, LifetimeToken).ConfigureAwait(false);
                await RefreshLanDiscoveryAsync().ConfigureAwait(false);

                var resolved = entry?.MdnsName is { Length: > 0 } mdnsName
                    ? _mdns?.Current.FirstOrDefault(s => s.Kind == MdnsServiceKind.Connect
                                                         && s.InstanceName == mdnsName)?.Address
                    : null;

                if (resolved is null || string.Equals(resolved, serial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AppLog.Info($"[局域网] 启动重连发现新地址：{serial} → {resolved}");
                await _dispatcher.InvokeAsync(async () =>
                {
                    if (IsConnected || IsBusy) return;
                    await ConnectLanAsync(resolved).ConfigureAwait(true);
                }).Task.Unwrap();
            }

            if (IsConnected)
            {
                AppLog.Info("[局域网] 启动自动重连成功（地址已更新）");
                return;
            }

            await _dispatcher.InvokeAsync(() =>
                Notice = "自动重连未成功，可在“局域网”页签里手动连接。");
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[局域网] 启动自动重连中止：{ex.Message}");
        }
    }

    /// <summary>断线自动重连：5/10/20 秒退避最多 3 次；手动断开与授权撤销不走这里。</summary>
    private async Task ReconnectLanLoopAsync(string serial)
    {
        if (Interlocked.Exchange(ref _lanReconnecting, 1) == 1) return;

        try
        {
            AppLog.Info($"[局域网] 开始断线自动重连 {serial}（5/10/20 秒退避，最多 3 次）");
            int[] delays = [5, 10, 20];
            for (int i = 0; i < delays.Length; i++)
            {
                if (LifetimeToken.IsCancellationRequested) return;

                var seconds = delays[i];
                await _dispatcher.InvokeAsync(() =>
                    Notice = $"局域网连接中断，{seconds} 秒后自动重连（第 {i + 1}/{delays.Length} 次）…");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), LifetimeToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var connected = false;
                await _dispatcher.InvokeAsync(async () =>
                {
                    if (IsConnected || IsBusy)
                    {
                        connected = IsConnected;
                        return;
                    }

                    await ConnectLanAsync(serial).ConfigureAwait(true);
                    connected = IsConnected;
                }).Task.Unwrap();

                if (connected)
                {
                    AppLog.Info($"[局域网] 断线自动重连成功（第 {i + 1} 次）");
                    return;
                }

                AppLog.Info($"[局域网] 第 {i + 1} 次重连未成功");

                // 地址可能已变化，按 mDNS 实例名重解析后下一轮用新地址
                var entry = _settings.LanDevices.FirstOrDefault(d =>
                    string.Equals(d.LastAddress, serial, StringComparison.OrdinalIgnoreCase));
                var resolved = entry?.MdnsName is { Length: > 0 } mdnsName
                    ? _mdns?.Current.FirstOrDefault(s => s.Kind == MdnsServiceKind.Connect
                                                         && s.InstanceName == mdnsName)?.Address
                    : null;
                if (resolved is not null && !string.Equals(resolved, serial, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Info($"[局域网] 重连时发现地址已变化：{serial} → {resolved}");
                    serial = resolved;
                }
            }

            await _dispatcher.InvokeAsync(() =>
                Notice = "自动重连未成功：请确认手机“无线调试”仍开启、与电脑在同一网段、路由器未开启客户端隔离，然后手动连接。");
            AppLog.Warn("[局域网] 断线自动重连 3 次均未成功");
        }
        finally
        {
            Interlocked.Exchange(ref _lanReconnecting, 0);
        }
    }

    /// <summary>USB 断线自动重连：只等本次掉线的同一串号重新就绪，有限次数后放弃。</summary>
    private async Task ReconnectUsbLoopAsync(string serial)
    {
        if (Interlocked.Exchange(ref _lanReconnecting, 1) == 1) return;

        try
        {
            for (int i = 0; i < 6; i++)
            {
                if (LifetimeToken.IsCancellationRequested) return;

                var attempt = i + 1;
                await _dispatcher.InvokeAsync(() =>
                    Notice = $"连接中断，等待设备重新接入后自动重连（{attempt}/6）…");

                AdbDevice? device;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), LifetimeToken).ConfigureAwait(false);
                    device = await WaitForDeviceAsync(serial, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (device is not { IsReady: true }) continue;

                var connected = false;
                await _dispatcher.InvokeAsync(async () =>
                {
                    if (IsConnected || IsBusy)
                    {
                        connected = IsConnected;
                        return;
                    }

                    SelectedDevice = Devices.FirstOrDefault(d => d.Serial == serial) ?? SelectedDevice;
                    await ConnectAsync().ConfigureAwait(true);
                    connected = IsConnected;
                }).Task.Unwrap();

                if (connected)
                {
                    AppLog.Info("[设备] USB 断线自动重连成功");
                    return;
                }
            }

            await _dispatcher.InvokeAsync(() =>
                Notice = "自动重连未成功：请重新插拔数据线或确认手机授权后手动连接。");
            AppLog.Warn("[设备] USB 断线自动重连 6 次均未成功");
        }
        finally
        {
            Interlocked.Exchange(ref _lanReconnecting, 0);
        }
    }

    // ---------- 连接 ----------

    public async Task ConnectAsync()
    {
        if (IsConnected || IsBusy) return;

        var device = SelectedDevice;
        if (device is null)
        {
            Notice = "请先选择设备。";
            return;
        }

        if (!device.IsReady)
        {
            RefreshPhaseFromDevice();
            Notice = device.StateDescription;
            return;
        }

        if (_runtime is null || _adb is null)
        {
            Notice = "内置 scrcpy 引擎不可用，无法连接。";
            return;
        }

        IsBusy = true;
        Phase = ConnectionPhase.Connecting;
        StatusLine = "正在启动画面…";
        _lastErrorLine = string.Empty;
        Notice = string.Empty;

        try
        {
            await EndSessionAsync("重新连接", ConnectionPhase.Idle, keepSelected: true).ConfigureAwait(true);

            var placement = _settings.MirrorPlacement;
            var options = new ScrcpyStartOptions
            {
                Serial = device.Serial,
                Quality = _settings.Quality,
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

            bool ok = await session.StartAsync(options, _lifetimeCts?.Token ?? CancellationToken.None).ConfigureAwait(true);

            if (!ok || !session.IsRunning)
            {
                var detail = string.IsNullOrWhiteSpace(_lastErrorLine) ? "未取得画面，请查看日志" : _lastErrorLine;
                StatusLine = $"启动失败：{detail}";
                AppLog.Error($"[连接] 启动失败：{detail}");
                Phase = ConnectionPhase.Failed;
                Notice = "启动失败，日志里有完整原因。";
                session.Dispose();
                _session = null;
                return;
            }

            _window.Attach(session.WindowHandle);
            _passThrough = false;
            _window.ApplyTopmost(_topmost);
            _window.ApplyPassThrough(false);
            // 系统边框与任务栏图标都在运行时处理，勾选后立刻能看到效果
            _window.ApplyBorderless(_settings.Borderless);
            _window.ApplyToolWindow(true);
            _hoverRevealActive = false;
            ApplyEffectiveOpacity();
            _window.MoveToVisibleArea();

            if (_hoverRevealEnabled) _hoverWatcher.Start();

            _settings.PreferredSerial = device.Serial;
            SaveSettings();

            if (device.Serial.Contains(':'))
            {
                UpdateLanHistory(device);
            }

            Phase = ConnectionPhase.Connected;
            _connectedSerial = device.Serial;
            StatusLine = $"{TransportName(device.Serial)} · {device.DisplayName} · 已连接";
            ToolbarVisible = true;
            PassThroughCheck = "尚未验证";
            AppLog.Info($"[连接] 画面已建立：{device.DisplayName}");
        }
        catch (Exception ex)
        {
            AppLog.Error("[连接] 建立会话时出错", ex);
            StatusLine = $"连接出错：{ex.Message}";
            Phase = ConnectionPhase.Failed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectAsync(string reason = "已手动断开")
    {
        if (!IsConnected && _session is null) return;
        IsBusy = true;
        await EndSessionAsync(reason, ConnectionPhase.Idle).ConfigureAwait(true);
        IsBusy = false;
        RefreshPhaseFromDevice();
    }

    private async Task EndSessionAsync(string reason, ConnectionPhase nextPhase, bool keepSelected = false)
    {
        SavePlacement();

        var session = _session;
        _session = null;
        _connectedSerial = null;
        if (session is not null)
        {
            session.OutputLine -= OnScrcpyOutput;
            session.Exited -= OnScrcpyExited;
            session.Stop();
            session.Dispose();
        }

        _window.Detach();
        _hoverWatcher.Stop();
        _hoverRevealActive = false;
        ToolbarVisible = false;
        Fps = null;
        VideoSize = null;
        MirrorMinimized = false;
        WindowInfo = "—";
        _passThrough = false;
        _effectiveOpacity = _opacity;
        OnPropertyChanged(nameof(PassThroughText));
        OnPropertyChanged(nameof(PassThroughEnabled));
        OnPropertyChanged(nameof(OpacityEnabled));
        OnPropertyChanged(nameof(EffectiveOpacity));
        OnPropertyChanged(nameof(OpacityStateText));
        PassThroughCheck = "尚未验证";

        AppLog.Info($"[会话] 结束（{reason}）");
        StatusLine = reason;
        Phase = nextPhase;

        if (!keepSelected) RefreshPhaseFromDevice();
        await Task.CompletedTask;
    }

    private void EndSession(string reason, ConnectionPhase nextPhase)
    {
        _ = _dispatcher.InvokeAsync(async () => await EndSessionAsync(reason, nextPhase).ConfigureAwait(true));
    }

    private void OnScrcpyOutput(string line)
    {
        if (TryParseFps(line, out var value))
        {
            // 这里绝不能同步 Invoke：scrcpy 的输出是在后台线程读的，
            // 同步等界面线程会让这个后台线程一直占着 Process 对象的锁，
            // 退出时界面线程正好要释放同一个对象，两边互等就把进程卡死。
            _ = _dispatcher.InvokeAsync(() =>
            {
                Fps = value;
                var rect = _window.GetRect();
                if (rect is not null)
                {
                    WindowInfo = $"{rect.Value.Width}×{rect.Value.Height}";
                }
            });
        }

        // Texture 行报告真实视频尺寸（含旋转变化），窗口缩放按它锁定比例，消除黑边
        var tex = Regex.Match(line, @"Texture.*?:\s*(\d+)x(\d+)");
        if (tex.Success
            && int.TryParse(tex.Groups[1].Value, out var tw)
            && int.TryParse(tex.Groups[2].Value, out var th)
            && tw > 0 && th > 0)
        {
            _ = _dispatcher.InvokeAsync(() => VideoSize = (tw, th));
        }

        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("error:", StringComparison.OrdinalIgnoreCase))
        {
            _lastErrorLine = line.Trim();
        }
    }

    private static bool TryParseFps(string line, out double fps)
    {
        foreach (var pattern in FpsPatterns)
        {
            var match = pattern.Match(line);
            if (match.Success
                && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out fps))
            {
                return true;
            }
        }

        fps = 0;
        return false;
    }

    private void OnScrcpyExited(int exitCode)
    {
        // 同样不能用同步 Invoke：Exited 事件是在 Process 对象的内部锁下抛出的，
        // 这里同步等界面线程就会占着锁不放，退出时界面线程正好要释放这个对象。
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (!IsConnected && _session is null) return;

            // 先记下掉线会话的串号：设备轮询可能随时把 SelectedDevice 改选到别的条目
            var droppedSerial = _connectedSerial;
            _connectedSerial = null;

            var reason = exitCode switch
            {
                2 => "设备连接中断，画面已停止",
                1 => "画面启动失败，请查看日志",
                0 => "画面窗口已被关闭，会话结束",
                _ => $"画面进程异常结束（退出码 {exitCode}）",
            };

            var session = _session;
            _session = null;
            if (session is not null)
            {
                session.OutputLine -= OnScrcpyOutput;
                session.Exited -= OnScrcpyExited;
                // Process.Exited 是在 Process 内部锁下抛出的：在事件链里同步 Dispose 进程对象
                // 会与抛出线程抢同一把锁形成死锁（实测卡死整个界面线程）。放到后台线程释放。
                Task.Run(() => session.Dispose());
            }

            _window.Detach();
            ToolbarVisible = false;
            Fps = null;
            VideoSize = null;
            MirrorMinimized = false;
            WindowInfo = "—";
            _passThrough = false;
            OnPropertyChanged(nameof(PassThroughText));
            PassThroughCheck = "尚未验证";

            StatusLine = reason;
            Phase = exitCode == 0 ? ConnectionPhase.Idle : ConnectionPhase.NoDevice;
            RefreshPhaseFromDevice();
            Notice = reason;

            // 仅异常断线（退出码 2）时自动重连；手动断开在入口已提前返回，不会走到这里
            if (exitCode == 2 && droppedSerial is not null && _settings.LanAutoReconnect)
            {
                if (droppedSerial.Contains(':'))
                {
                    _ = ReconnectLanLoopAsync(droppedSerial);
                }
                else
                {
                    _ = ReconnectUsbLoopAsync(droppedSerial);
                }
            }
        });
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

        if (value)
        {
            _hoverWatcher.Start();
            // 打开的瞬间先按鼠标当前位置判断一次，避免画面闪一下
            _hoverRevealActive = _hoverWatcher.IsInside;
        }
        else
        {
            _hoverWatcher.Stop();
            _hoverRevealActive = false;
        }

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
        if (!IsConnected)
        {
            PassThroughCheck = "尚未连接，无法验证";
            OnPropertyChanged(nameof(PassThroughCheck));
            return;
        }

        IsBusy = true;
        await Task.Delay(20).ConfigureAwait(true);
        var check = PassThroughVerifier.VerifyAndLog(_window);
        PassThroughCheck = $"{check.Display}（{check.Detail}）";
        OnPropertyChanged(nameof(PassThroughCheck));
        IsBusy = false;

        if (showNotice) Notice = PassThroughCheck;
    }

    /// <summary>档位、渲染后端、无边框等需要重启 scrcpy 的设置。</summary>
    public async Task ApplyAndReconnectAsync(string reason)
    {
        SaveSettings();
        Notice = $"{reason} 将重新连接画面以生效。";
        if (IsConnected)
        {
            var device = SelectedDevice;
            await EndSessionAsync($"应用设置：{reason}", ConnectionPhase.Idle, keepSelected: true).ConfigureAwait(true);
            SelectedDevice = device;
            await ConnectAsync().ConfigureAwait(true);
        }
    }

    public void SaveQuality(string key)
    {
        _settings.Quality = key;
        OnPropertyChanged(nameof(QualityDisplay));
        OnPropertyChanged(nameof(QualitySummary));
        SaveSettings();
    }

    public void SaveRenderDriver(string driver)
    {
        _settings.RenderDriver = driver;
        SaveSettings();
    }

    public void SaveBorderless(bool value)
    {
        _settings.Borderless = value;
        SaveSettings();
    }

    public void SaveKeepAwake(bool value)
    {
        _settings.KeepAwake = value;
        SaveSettings();
    }

    public void SaveVerifyPassThrough(bool value)
    {
        _settings.VerifyPassThroughOnSwitch = value;
        SaveSettings();
    }

    public void SaveToolbarFollows(bool value)
    {
        _settings.ToolbarFollowsMirror = value;
        SaveSettings();
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
        try { _session?.ForceStop(); }
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