using System.Reflection;
using System.Windows.Threading;
using LNMirror.Core;
using LNMirror.Core.Adb;
using LNMirror.Core.Configuration;
using LNMirror.Core.Devices;

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

if (args.Length > 0) return 2;

var cases = new (string Name, Action Check)[]
{
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

static void SetControllerField(AppController controller, string name, object value)
    => typeof(AppController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, value);
