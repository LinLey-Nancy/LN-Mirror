using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using LNMirror.Core;
using LNMirror.Core.Adb;
using LNMirror.Core.Configuration;
using LNMirror.Core.Devices;
using LNMirror.Core.Scrcpy;
using LNMirror.Core.Windowing;

const string connectAdvertisement = "adb-phone-TnSdi9 _adb-tls-connect._tcp 192.0.2.10:33015";
const string pairAdvertisement = "adb-phone-QXjCrW _adb-tls-pairing._tcp 192.0.2.10:33861";
const string otherPhone = "adb-other-TnSdi9 _adb-tls-connect._tcp 192.0.2.11:33015";
const string sameHostOtherDevice = "adb-other-TnSdi9 _adb-tls-connect._tcp 192.0.2.10:33016";
const string connectAddress = "192.0.2.10:33015";
const string pairAddress = "192.0.2.10:33861";

if (args is ["pair", pairAddress, "000000"])
{
    Console.WriteLine($"Successfully paired to {pairAddress}");
    return 0;
}

if (args is ["connect", connectAddress])
{
    Console.WriteLine($"failed to authenticate to {connectAddress}");
    return 0;
}

if (args is ["devices", "-l"])
{
    if (Environment.GetEnvironmentVariable("LN_MIRROR_TEST_ADB") == "offline")
    {
        Console.WriteLine($"List of devices attached\n{connectAddress} offline");
        return 0;
    }

    Console.Error.WriteLine("test: adb server is unavailable");
    return 1;
}

if (args is ["pending-command"])
{
    Thread.Sleep(5000);
    return 0;
}

if (args is ["connect", "192.0.2.10:33016"])
{
    Thread.Sleep(5000);
    Console.WriteLine("connected to 192.0.2.10:33016");
    return 0;
}

if (args.Length > 0) return 2;

SettingsStore.ReadOnly = true;

var cases = new (string Name, Action Check)[]
{
    ("智能悬停忽略路过的指针，停稳后显示并在离开时隐藏", () =>
    {
        var watcher = new MirrorHoverWatcher(() => null);
        var shown = false;
        watcher.HoverChanged += value => shown = value;
        SampleHover(watcher, 20, 0);
        SampleHover(watcher, 60, 300);
        SampleHover(watcher, 62, 600);
        Require(!shown);
        SampleHover(watcher, 65, 650);
        Require(shown);
        SampleHover(watcher, 130, 660);
        Require(!shown);
    }),
    ("固定悬停延迟在离开后重新计时，关闭延迟立即显示", () =>
    {
        var watcher = new MirrorHoverWatcher(() => null);
        var shown = false;
        watcher.HoverChanged += value => shown = value;
        SetHoverDelay(watcher, 1000, false);
        SampleHover(watcher, 20, 0);
        SampleHover(watcher, 130, 800);
        SampleHover(watcher, 25, 900);
        SampleHover(watcher, 70, 1800);
        Require(!shown);
        SampleHover(watcher, 75, 1900);
        Require(shown);
        SampleHover(watcher, 130, 2000);
        SetHoverDelay(watcher, 0, false);
        SampleHover(watcher, 30, 2010);
        Require(shown);
    }),
    ("关闭悬停延迟后可重新选回智能，默认设置显示智能", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        Require(controller.HoverDelayLabel == "智能");
        controller.SetHoverDelay(HoverDelayModes.Off);
        Require(controller.HoverDelayLabel == "关闭");
        controller.SetHoverDelay(HoverDelayModes.Smart);
        Require(controller.HoverDelayLabel == "智能");
        controller.SetHoverDelay(HoverDelayModes.Custom, 1250);
        Require(controller.HoverCustomDelayMs == 1250 && controller.HoverDelayLabel.StartsWith("自定义"));
    }),
    ("同型号的不同设备不会被去重", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        var apply = typeof(AppController).GetMethod("OnDevicesSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(controller, [AdbClient.ParseDevices(
            "unrelated-usb device model:Same\nadb-phone-TnSdi9._adb-tls-connect._tcp device model:Same")]);
        Require(controller.Devices.Count == 2);
    }),
    ("连接发现设备优先复用已就绪的 mDNS 串号", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        const string mdnsSerial = "adb-phone-TnSdi9._adb-tls-connect._tcp.";
        var services = SetWirelessSnapshots(controller, $"{connectAddress} device\n{mdnsSerial} device", connectAdvertisement);
        controller.ConnectDiscoveredLanAsync(services.Single()).GetAwaiter().GetResult();
        Require(controller.Notice.Contains("内置 scrcpy 引擎不可用") && controller.SelectedDevice?.Serial == mdnsSerial);
    }),
    ("同一个 mDNS 实例有多个端点时仍使用手填的就绪地址", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        SetWirelessSnapshots(controller, $"{connectAddress} device", connectAdvertisement,
            connectAdvertisement.Replace(connectAddress, "192.0.2.12:33015"));
        controller.ConnectLanAsync(connectAddress).GetAwaiter().GetResult();
        Require(controller.SelectedDevice?.Serial == connectAddress && controller.Notice.Contains("内置 scrcpy 引擎不可用"));
    }),
    ("旧 IP 首选可选中已经合并成 mDNS 行的同一台设备", () =>
    {
        const string serial = "adb-phone-TnSdi9._adb-tls-connect._tcp";
        using var controller = new AppController(new AppSettings { PreferredSerial = connectAddress }, Dispatcher.CurrentDispatcher);
        var output = $"other-usb device\n{connectAddress} device\n{serial} device";
        SetWirelessSnapshots(controller, output, connectAdvertisement);
        var apply = typeof(AppController).GetMethod("OnDevicesSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(controller, [AdbClient.ParseDevices(output)]);
        Require(controller.Devices.Count == 2 && controller.SelectedDevice?.Serial == serial);
    }),
    ("旧 IP 历史成功后补记当前确认的 mDNS 身份", () =>
    {
        var legacy = new LanDeviceEntry { LastAddress = connectAddress };
        var settings = new AppSettings { LanDevices = [legacy] };
        using var controller = new AppController(settings, Dispatcher.CurrentDispatcher);
        SetWirelessSnapshots(controller, $"{connectAddress} device", connectAdvertisement);
        RecordWirelessHistory(controller, connectAddress);
        Require(settings.LanDevices.Count == 1 && legacy.MdnsName == "adb-phone-TnSdi9");
    }),
    ("无法核对同地址的设备身份时保留已有命名历史", () =>
    {
        var known = new LanDeviceEntry { LastAddress = connectAddress, MdnsName = "adb-phone-TnSdi9" };
        var settings = new AppSettings { PreferredSerial = "adb-phone-TnSdi9._adb-tls-connect._tcp", LanDevices = [known] };
        using var controller = new AppController(settings, Dispatcher.CurrentDispatcher);
        RecordWirelessHistory(controller, connectAddress);
        Require(settings.LanDevices.Count == 1 && settings.LanDevices[0] == known);
    }),
    ("已知 mDNS 身份的历史设备不会直连未验证的旧端口", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        SetControllerField(controller, "_adb", new AdbClient(Environment.ProcessPath!));
        var entry = new LanDeviceEntry { LastAddress = connectAddress, MdnsName = "adb-phone-TnSdi9" };
        controller.ConnectHistoryAsync(entry).GetAwaiter().GetResult();
        Require(controller.Notice.Contains("尚未发现"));
    }),
    ("旧版首选地址被另一设备占用时仍按原历史记录移除", () =>
    {
        var original = new LanDeviceEntry { LastAddress = connectAddress, MdnsName = "adb-phone-TnSdi9" };
        var settings = new AppSettings { PreferredSerial = connectAddress, LanDevices = [original] };
        using var controller = new AppController(settings, Dispatcher.CurrentDispatcher);
        var mdns = new MdnsMonitor(new AdbClient(Environment.ProcessPath!));
        typeof(MdnsMonitor).GetProperty(nameof(MdnsMonitor.Current))!.SetValue(mdns,
            MdnsServicesParser.Parse(connectAdvertisement.Replace("adb-phone-TnSdi9", "adb-replacement-TnSdi9")));
        SetControllerField(controller, "_mdns", mdns);
        controller.RemoveHistoryAsync(original).GetAwaiter().GetResult();
        Require(settings.PreferredSerial is null && settings.LanDevices.Count == 0);
    }),
    ("自动重连等待期间可以取消", () =>
    {
        using var controller = new AppController(new AppSettings { LanAutoReconnect = true }, Dispatcher.CurrentDispatcher);
        var reconnect = typeof(AppController).GetMethod("ReconnectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)reconnect.Invoke(controller, ["test-serial", null, false, null])!;
        Require(controller.CanDisconnect && !controller.IsBusy);
        controller.DisconnectAsync().GetAwaiter().GetResult();
        Require(task.Wait(TimeSpan.FromSeconds(2)) && !controller.CanDisconnect);
    }),
    ("取消连接能中止尚未创建 scrcpy 会话的 ADB 操作", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        SetControllerField(controller, "_adb", new AdbClient(Environment.ProcessPath!));
        var start = controller.ConnectLanAsync("192.0.2.10:33016");
        Require(controller.CanDisconnect);
        controller.DisconnectAsync().GetAwaiter().GetResult();
        Require(start.Wait(TimeSpan.FromSeconds(2)));
        Require(!controller.IsBusy && !controller.IsConnected && controller.Notice.Contains("取消"));
    }),
    ("应用设置按活动会话的设备重连", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        var active = new AdbDevice(connectAddress, "device", "", "", "", "1");
        controller.Devices.Add(active);
        controller.SelectedDevice = new AdbDevice("other-phone", "offline", "", "", "", "2");
        SetControllerField(controller, "_connectedDevice", active);
        SetControllerField(controller, "_phase", ConnectionPhase.Connected);
        controller.ApplyAndReconnectAsync("画质").GetAwaiter().GetResult();
        Require(controller.Notice.Contains("内置 scrcpy 引擎不可用"));
    }),
    ("设备查询失败保留最后一次成功快照", () =>
    {
        var monitor = new DeviceMonitor(new AdbClient(Environment.ProcessPath!));
        var snapshot = AdbClient.ParseDevices($"{connectAddress} device");
        typeof(DeviceMonitor).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, [snapshot]);
        var removed = false;
        monitor.DeviceChanged += (device, _) => removed |= device.State == "removed";

        using var cancel = new CancellationTokenSource();
        monitor.StartAsync(cancel.Token).GetAwaiter().GetResult();
        Require(!removed && ReferenceEquals(snapshot, monitor.Current));
        monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }),
    ("调用方取消 ADB 命令会收到取消异常", () =>
    {
        var adb = new AdbClient(Environment.ProcessPath!);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { adb.RunAsync(["pending-command"], cancel.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("ADB 命令吞掉了调用方取消");
    }),
    ("就绪等待不会把第一个 offline 当成最终结果", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        SetControllerField(controller, "_adb", new AdbClient(Environment.ProcessPath!));
        var wait = typeof(AppController).GetMethod("WaitForDeviceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var duration = TimeSpan.FromMilliseconds(850);
        Environment.SetEnvironmentVariable("LN_MIRROR_TEST_ADB", "offline");
        try
        {
            var started = Environment.TickCount64;
            var task = (Task<AdbDevice?>)wait.Invoke(controller, [connectAddress, duration, CancellationToken.None, null, false])!;
            Require(task.GetAwaiter().GetResult()?.IsOffline == true);
            Require(Environment.TickCount64 - started >= 750);
        }
        finally { Environment.SetEnvironmentVariable("LN_MIRROR_TEST_ADB", null); }
    }),
    ("无线低负载档将对应的参数传给 scrcpy", () =>
    {
        var options = new ScrcpyStartOptions { Serial = connectAddress, Quality = "WirelessLowLoad" };
        var arguments = ScrcpyRuntime.BuildArguments(options);
        Require(arguments.Contains("--max-size=1024"));
        Require(arguments.Contains("--max-fps=30"));
        Require(arguments.Contains("--video-bit-rate=3M"));
        Require(!arguments.Any(argument => argument.StartsWith("--video-buffer=")));
    }),
    ("原有均衡档仍使用 1920、60 FPS 和 8 Mbps", () =>
    {
        var arguments = ScrcpyRuntime.BuildArguments(new ScrcpyStartOptions { Serial = "usb-phone" });
        Require(arguments.Contains("--max-size=1920") && arguments.Contains("--max-fps=60"));
        Require(arguments.Contains("--video-bit-rate=8M"));
    }),
    ("无线平滑保留所选画质并为 IP 连接增加 100 ms 缓冲", () =>
    {
        var arguments = ScrcpyRuntime.BuildArguments(EnableWirelessSmoothing(connectAddress));
        Require(arguments.Contains("--max-size=1920") && arguments.Contains("--max-fps=60"));
        Require(arguments.Contains("--video-bit-rate=8M"));
        Require(arguments.Contains("--video-buffer=100"));
    }),
    ("无线调试 mDNS 串号也应用平滑缓冲", () =>
    {
        var options = EnableWirelessSmoothing("adb-phone-TnSdi9._adb-tls-connect._tcp");
        Require(ScrcpyRuntime.BuildArguments(options).Contains("--video-buffer=100"));
    }),
    ("启用无线平滑时 USB 不增加缓冲", () =>
    {
        var arguments = ScrcpyRuntime.BuildArguments(EnableWirelessSmoothing("usb-phone"));
        Require(!arguments.Any(argument => argument.StartsWith("--video-buffer=")));
    }),
    ("无线平滑关闭时局域网保留低延迟播放", () =>
    {
        var arguments = ScrcpyRuntime.BuildArguments(new ScrcpyStartOptions { Serial = connectAddress });
        Require(!arguments.Any(argument => argument.StartsWith("--video-buffer=")));
    }),
    ("无线会话使用用户选定的缓冲毫秒数", () =>
    {
        var options = new ScrcpyStartOptions { Serial = connectAddress, WirelessVideoBufferMs = 75 };
        Require(ScrcpyRuntime.BuildArguments(options).Contains("--video-buffer=75"));
    }),
    ("显式选择零缓冲会覆盖旧版无线平滑设置", () =>
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"wirelessSmoothPlayback":true,"wirelessVideoBufferMs":0}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var controller = new AppController(settings, Dispatcher.CurrentDispatcher);
        Require(controller.WirelessVideoBufferMs == 0);
    }),
    ("旧版无线平滑启用时迁移为 100 ms", () =>
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"wirelessSmoothPlayback":true}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var controller = new AppController(settings, Dispatcher.CurrentDispatcher);
        Require(controller.WirelessVideoBufferMs == 100);
    }),
    ("读取 scrcpy 渲染帧率及同一秒的跳过帧数", () =>
    {
        Require(ScrcpyFrameSample.TryParse("INFO: 48 fps (+12 frames skipped)", out var sample));
        Require(sample.RenderedFps == 48 && sample.SkippedFrames == 12);
    }),
    ("诊断分别显示视频尺寸与桌面窗口尺寸", () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var controller = new AppController(new AppSettings(), dispatcher);
        using var session = new ScrcpySession(ScrcpyRuntime.Discover()!);
        SetControllerField(controller, "_session", session);
        ApplySessionOutput(controller, session, "INFO: Texture (D3D11VA): 576x1280");
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Require(controller.VideoSizeText == "576×1280" && controller.WindowInfo == "—");
    }),
    ("静态画面零帧是有效样本", () =>
    {
        Require(ScrcpyFrameSample.TryParse("INFO: 0 fps", out var sample));
        Require(sample.RenderedFps == 0 && sample.SkippedFrames == 0);
    }),
    ("编码器配置日志不会计入渲染帧率", () =>
    {
        Require(!ScrcpyFrameSample.TryParse("[server] INFO: Encoder configured for 60 fps", out _));
        Require(!ScrcpyFrameSample.TryParse("INFO: FPS counter started", out _));
    }),
    ("跳过帧累计到当前会话，断开后清空", () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var controller = new AppController(new AppSettings(), dispatcher);
        using var session = new ScrcpySession(ScrcpyRuntime.Discover()!);
        SetControllerField(controller, "_session", session);

        ApplySessionOutput(controller, session, "INFO: 48 fps (+12 frames skipped)");
        ApplySessionOutput(controller, session, "INFO: 0 fps");
        ApplySessionOutput(controller, session, "INFO: 52 fps (+4 frames skipped)");
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Require(controller.Fps == 52);
        Require(controller.SkippedFramesText == "16");

        controller.DisconnectAsync().GetAwaiter().GetResult();
        Require(controller.Fps is null && controller.SkippedFramesText == "—");
    }),
    ("切换会话时丢弃旧引擎排队的帧率样本", () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var controller = new AppController(new AppSettings(), dispatcher);
        var runtime = ScrcpyRuntime.Discover()!;
        using var previous = new ScrcpySession(runtime);
        using var current = new ScrcpySession(runtime);
        SetControllerField(controller, "_session", previous);
        ApplySessionOutput(controller, previous, "INFO: 1 fps (+80 frames skipped)");

        SetControllerField(controller, "_session", current);
        ApplySessionOutput(controller, current, "INFO: 30 fps");
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Require(controller.Fps == 30 && controller.SkippedFramesText == "0");
    }),
    ("仅广播连接端口表示已发现", () =>
    {
        var row = DeviceRows(connectAdvertisement).Single();
        Require(row.KindText == "已发现" && row.ActionText == "如何配对" && row.Address == connectAddress);
    }),
    ("只有当前 ADB 已授权的发现设备提供连接入口", () =>
    {
        var settings = new AppSettings();
        settings.LanDevices.Add(new LanDeviceEntry { MdnsName = "adb-other-TnSdi9", LastAddress = "192.0.2.11:33015" });
        using var controller = new AppController(settings, Dispatcher.CurrentDispatcher);
        var monitor = new DeviceMonitor(new AdbClient(Environment.ProcessPath!));
        var devices = AdbClient.ParseDevices("adb-phone-TnSdi9._adb-tls-connect._tcp device\n192.0.2.11:33015 offline");
        typeof(DeviceMonitor).GetProperty(nameof(DeviceMonitor.Current))!.SetValue(monitor, devices);
        SetControllerField(controller, "_monitor", monitor);
        ApplySnapshot(controller, connectAdvertisement, otherPhone);
        Require(controller.DiscoveredLan.Single(row => row.Address == connectAddress).ActionText == "连接");
        Require(controller.DiscoveredLan.Single(row => row.Address != connectAddress).ActionText == "如何配对");
    }),
    ("未授权设备只显示配对指引", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        ApplySnapshot(controller, connectAdvertisement);
        controller.ConnectDiscoveredLanAsync(controller.DiscoveredLan.Single()).GetAwaiter().GetResult();
        Require(controller.Notice.Contains("使用配对码配对设备"));
        Require(controller.Phase != ConnectionPhase.Connecting);
    }),
    ("打开配对时替换同一设备行，保留其他手机", () =>
    {
        var rows = DeviceRows(connectAdvertisement, pairAdvertisement, otherPhone, pairAdvertisement);
        Require(rows.Length == 2);
        Require(rows.Single(row => row.Kind == MdnsServiceKind.Pairing).Address == pairAddress);
        Require(rows.Single(row => row.Kind == MdnsServiceKind.Connect).Address == "192.0.2.11:33015");
    }),
    ("多个连接端口共享 IP 时优先匹配同名实例", () =>
    {
        var matchingPair = pairAdvertisement.Replace("QXjCrW", "TnSdi9");
        var rows = DeviceRows(connectAdvertisement, sameHostOtherDevice, matchingPair);
        Require(rows.Length == 2);
        Require(rows.Single(row => row.Kind == MdnsServiceKind.Connect).Address == "192.0.2.10:33016");
    }),
    ("只有配对端口时仍显示配对设备", () =>
        Require(DeviceRows(pairAdvertisement).Single().Address == pairAddress)),
    ("多个同 IP 连接服务无法区分时保留各自的入口", () =>
        Require(DeviceRows(connectAdvertisement, sameHostOtherDevice, pairAdvertisement).Length == 3)),
    ("发现列表内容相同时轮询不重建按钮", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        ApplySnapshot(controller, connectAdvertisement, otherPhone);
        var originalRow = controller.DiscoveredLan[0];
        ApplySnapshot(controller, otherPhone, connectAdvertisement);
        Require(ReferenceEquals(controller.DiscoveredLan[0], originalRow));
    }),
    ("无线设备清单未变化时不触发界面列表重建", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        var apply = typeof(AppController).GetMethod("OnDevicesSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string readyDevice = "192.0.2.10:33015 device model:Test transport_id:1";
        apply.Invoke(controller, [AdbClient.ParseDevices(readyDevice)]);
        var selection = controller.SelectedDevice;
        var changes = 0;
        controller.Devices.CollectionChanged += (_, _) => changes++;

        apply.Invoke(controller, [AdbClient.ParseDevices(readyDevice)]);
        Require(changes == 0 && selection is not null && ReferenceEquals(controller.SelectedDevice, selection));

        apply.Invoke(controller, [AdbClient.ParseDevices("192.0.2.10:33015 offline transport_id:1")]);
        Require(changes > 0 && controller.SelectedDevice?.IsOffline == true);
    }),
    ("配对成功后使用同 IP 的连接端口并反馈后续失败", () =>
    {
        using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
        var adb = new AdbClient(Environment.ProcessPath!);
        var monitor = new MdnsMonitor(adb);
        var services = MdnsServicesParser.Parse($"{connectAdvertisement}\n{pairAdvertisement}");
        typeof(MdnsMonitor).GetProperty(nameof(MdnsMonitor.Current))!.SetValue(monitor, services);
        SetControllerField(controller, "_adb", adb);
        SetControllerField(controller, "_mdns", monitor);

        var notices = new List<string>();
        controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppController.Notice)) notices.Add(controller.Notice);
        };
        controller.PairAndConnectAsync(pairAddress, "000000").GetAwaiter().GetResult();

        Require(notices.Any(notice => notice.Contains("正在配对")));
        Require(notices.Any(notice => notice.Contains("正在连接")));
        Require(controller.Phase == ConnectionPhase.Failed);
        Require(controller.Notice.Contains("使用配对码配对设备"));
    }),
    ("认证失败明确提示在手机打开配对码", () =>
    {
        var failure = new AdbResult(0, $"failed to authenticate to {connectAddress}", "");
        Require(!AdbClient.IsConnectSuccess(failure));
        Require(AdbClient.DescribeConnectFailure(failure).Contains("使用配对码配对设备"));
    }),
    ("连接成功按 ADB 独立结果行判定", () =>
    {
        Require(AdbClient.IsConnectSuccess(new(0, $"already connected to {connectAddress}", "")));
        Require(!AdbClient.IsConnectSuccess(new(0, $"not connected to {connectAddress}", "")));
    }),
};

var failures = 0;
foreach (var (name, check) in cases)
{
    try
    {
        check();
        Console.WriteLine($"通过 {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"失败 {name} {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

static MdnsServiceInfo[] DeviceRows(params string[] advertisements)
{
    using var controller = new AppController(new AppSettings(), Dispatcher.CurrentDispatcher);
    ApplySnapshot(controller, advertisements);
    return controller.DiscoveredLan.ToArray();
}

static void ApplySnapshot(AppController controller, params string[] advertisements)
{
    var services = MdnsServicesParser.Parse(string.Join('\n', advertisements));
    var applySnapshot = typeof(AppController).GetMethod("OnMdnsSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
    applySnapshot.Invoke(controller, [services]);
}

static void Require(bool condition)
{
    if (!condition) throw new InvalidOperationException("结果与预期不符");
}

static void SampleHover(MirrorHoverWatcher watcher, int x, long now)
{
    var sample = typeof(MirrorHoverWatcher).GetMethod("ObservePointer", BindingFlags.Instance | BindingFlags.NonPublic);
    Require(sample is not null);
    sample!.Invoke(watcher, [new WindowRect(0, 0, 100, 100), x, 50, now]);
}

static IReadOnlyList<MdnsServiceInfo> SetWirelessSnapshots(AppController controller, string devices, params string[] advertisements)
{
    var adb = new AdbClient(Environment.ProcessPath!);
    var monitor = new DeviceMonitor(adb);
    var mdns = new MdnsMonitor(adb);
    var services = MdnsServicesParser.Parse(string.Join('\n', advertisements));
    typeof(DeviceMonitor).GetProperty(nameof(DeviceMonitor.Current))!.SetValue(monitor, AdbClient.ParseDevices(devices));
    typeof(MdnsMonitor).GetProperty(nameof(MdnsMonitor.Current))!.SetValue(mdns, services);
    SetControllerField(controller, "_adb", adb);
    SetControllerField(controller, "_monitor", monitor);
    SetControllerField(controller, "_mdns", mdns);
    return services;
}

static void RecordWirelessHistory(AppController controller, string address)
{
    var targetType = typeof(MdnsMonitor).Assembly.GetType("LNMirror.Core.Adb.WirelessTarget")!;
    var target = Activator.CreateInstance(targetType, [address, null]);
    var update = typeof(AppController).GetMethod("UpdateLanHistory", BindingFlags.Instance | BindingFlags.NonPublic)!;
    update.Invoke(controller, [new AdbDevice(address, "device", "Test", "", "", "1"), target]);
}

static void SetHoverDelay(MirrorHoverWatcher watcher, int milliseconds, bool requireStillness)
{
    watcher.SetDelay(milliseconds, requireStillness);
}

static void ApplySessionOutput(AppController controller, ScrcpySession session, string line)
    => typeof(AppController).GetMethod("OnScrcpyOutput", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(controller, [session, line]);

static ScrcpyStartOptions EnableWirelessSmoothing(string serial)
    => new() { Serial = serial, WirelessVideoBufferMs = 100 };

static void SetControllerField(AppController controller, string name, object value)
    => typeof(AppController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, value);
