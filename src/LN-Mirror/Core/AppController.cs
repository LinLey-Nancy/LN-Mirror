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
    private readonly Dispatcher _dispatcher;

    private ScrcpyRuntime? _runtime;
    private AdbClient? _adb;
    private DeviceMonitor? _monitor;
    private ScrcpySession? _session;
    private HotKeyService? _hotKeys;

    private ConnectionPhase _phase = ConnectionPhase.Idle;
    private AdbDevice? _selectedDevice;
    private int _opacity;
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
    private CancellationTokenSource? _lifetimeCts;

    public AppController(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _opacity = settings.Opacity is >= 1 and <= 100 ? settings.Opacity : 100;
        _topmost = settings.Topmost;
        _passThrough = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppSettings Settings => _settings;

    public MirrorWindowController Window => _window;

    public ObservableCollection<AdbDevice> Devices { get; } = new();

    public IReadOnlyList<QualityProfile> QualityProfiles => QualityProfile.All;

    public IReadOnlyList<string> RenderDriverOptions { get; } = ["auto", "direct3d", "opengl", "software"];

    public bool LanAvailable => false;

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
        set => _ = SetOpacityAsync(value);
    }

    public bool OpacityEnabled => IsConnected && !_isBusy;

    public bool PassThrough
    {
        get => _passThrough;
        set => _ = SetPassThroughAsync(value);
    }

    public bool PassThroughEnabled => IsConnected && !_isBusy;

    public string PassThroughText => _passThrough ? "鼠标穿透" : "操控手机";

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

        _hotKeys = new HotKeyService();
        _hotKeys.HotKeyPressed += OnHotKeyPressed;
        RegisterHotKeys();

        Phase = Devices.Count == 0 ? ConnectionPhase.NoDevice : _phase;
        AppLog.Info("[应用] 初始化完成");
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

            Devices.Clear();
            foreach (var device in devices) Devices.Add(device);

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

    private void OnDeviceChanged(AdbDevice device, AdbDevice? previous)
    {
        _dispatcher.Invoke(() =>
        {
            AppLog.Info($"[设备] {device.DisplayName}：{previous?.State ?? "新设备"} → {device.State}");

            if (IsConnected && _selectedDevice is not null
                && device.Serial == _selectedDevice.Serial
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
            }
            else if (!IsConnected)
            {
                RefreshPhaseFromDevice();
            }
        });
    }

    private void RefreshPhaseFromDevice()
    {
        if (IsBusy) return;

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
            _window.ApplyOpacity(_opacity);
            _window.ApplyTopmost(_topmost);
            _window.ApplyPassThrough(false);
            _window.MoveToVisibleArea();

            _settings.PreferredSerial = device.Serial;
            SaveSettings();

            Phase = ConnectionPhase.Connected;
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
        if (session is not null)
        {
            session.OutputLine -= OnScrcpyOutput;
            session.Exited -= OnScrcpyExited;
            session.Stop();
            session.Dispose();
        }

        _window.Detach();
        ToolbarVisible = false;
        Fps = null;
        MirrorMinimized = false;
        WindowInfo = "—";
        _passThrough = false;
        OnPropertyChanged(nameof(PassThroughText));
        OnPropertyChanged(nameof(PassThroughEnabled));
        OnPropertyChanged(nameof(OpacityEnabled));
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
            _dispatcher.Invoke(() =>
            {
                Fps = value;
                var rect = _window.GetRect();
                if (rect is not null)
                {
                    WindowInfo = $"{rect.Value.Width}×{rect.Value.Height}";
                }
            });
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
        _dispatcher.Invoke(() =>
        {
            if (!IsConnected && _session is null) return;

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
                session.Dispose();
            }

            _window.Detach();
            ToolbarVisible = false;
            Fps = null;
            MirrorMinimized = false;
            WindowInfo = "—";
            _passThrough = false;
            OnPropertyChanged(nameof(PassThroughText));
            PassThroughCheck = "尚未验证";

            StatusLine = reason;
            Phase = exitCode == 0 ? ConnectionPhase.Idle : ConnectionPhase.NoDevice;
            RefreshPhaseFromDevice();
            Notice = reason;
        });
    }

    // ---------- 外观与交互 ----------

    public async Task SetOpacityAsync(int value)
    {
        value = Math.Clamp(value, 0, 100);
        if (value == _opacity && _window.IsValid) return;

        if (value == 0 && !_passThrough)
        {
            // 画面完全不可见时必须同步开启穿透，避免盲点手机
            await SetPassThroughAsync(true, force: true).ConfigureAwait(true);
        }

        if (!Set(ref _opacity, value, nameof(Opacity))) return;

        _window.ApplyOpacity(value);

        if (value > 0)
        {
            _settings.Opacity = value;
            SaveSettings();
        }

        UpdateStatusDetail();
    }

    public async Task SetPassThroughAsync(bool value, bool force = false)
    {
        if (_passThrough == value && !force) return;
        if (!IsConnected) return;

        if (!await InputReleaseGuard.WaitForMouseReleaseAsync().ConfigureAwait(true))
        {
            Notice = "鼠标仍处于按下状态，本次切换已取消，请松开后重试。";
            return;
        }

        if (_passThrough != value)
        {
            _window.CancelPointerCapture();
            if (!Set(ref _passThrough, value, nameof(PassThrough))) return;
            _window.ApplyPassThrough(value);
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

        await SetPassThroughAsync(false, force: true).ConfigureAwait(true);

        if (!Set(ref _opacity, 100, nameof(Opacity)))
        {
            OnPropertyChanged(nameof(Opacity));
        }

        _window.ApplyOpacity(100);
        _settings.Opacity = 100;
        SaveSettings();

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

    public void Dispose()
    {
        try
        {
            SavePlacement();
            _session?.Dispose();
            _session = null;
            _window.Dispose();
            _hotKeys?.Dispose();

            // 先取消再释放：设备轮询任务挂在同一个令牌上，反过来会抛 ObjectDisposedException
            _lifetimeCts?.Cancel();
            _monitor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[应用] 退出清理时出错：{ex.Message}");
        }
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