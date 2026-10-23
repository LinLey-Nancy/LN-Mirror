using System.Text.Json;
using System.Text.Json.Serialization;
using LNMirror.Core.Diagnostics;

namespace LNMirror.Core.Configuration;

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public bool HasValue { get; set; }
}

/// <summary>已配对过的局域网设备。只保存连接所需的非敏感信息：配对码与 ADB 私钥一律不写入配置。</summary>
public sealed class LanDeviceEntry
{
    /// <summary>显示名，取手机型号；取不到时为最近地址。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>最近一次成功连接的 ip:port。手机重启或重新开关无线调试后端口通常会变。</summary>
    public string LastAddress { get; set; } = string.Empty;

    /// <summary>mDNS 实例名（adb-xxxx）。地址变化后按它从发现列表重新解析新地址。</summary>
    public string MdnsName { get; set; } = string.Empty;

    public DateTime LastConnectedAt { get; set; }
}

public static class ConnectionModes
{
    public const string Usb = "Usb";
    public const string Lan = "Lan";
}

/// <summary>画面看不见时，在它原来的位置给用户的提示方式。</summary>
public static class HoverRevealHints
{
    public const string None = "None";
    public const string Outline = "Outline";
    public const string CornerMark = "CornerMark";

    public static readonly IReadOnlyList<HintOption> All =
    [
        new(HoverRevealHints.None, "不提示"),
        new(HoverRevealHints.Outline, "淡边框"),
        new(HoverRevealHints.CornerMark, "角标"),
    ];
}

public sealed record HintOption(string Key, string DisplayName);

/// <summary>本地配置。只保存显示偏好与非敏感的局域网连接信息，不保存配对码等授权凭据。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 2;

    /// <summary>上次使用的非零不透明度。启动与重连时用它恢复显示，避免画面静默不可见。</summary>
    public int Opacity { get; set; } = 100;

    public bool Topmost { get; set; } = true;

    public string Quality { get; set; } = QualityNames.Balanced;

    public bool Borderless { get; set; } = true;

    /// <summary>auto / direct3d / opengl / software。透明不生效时切换 software 或 opengl 用于兼容验证。</summary>
    public string RenderDriver { get; set; } = "auto";

    public bool KeepAwake { get; set; }

    public bool VerifyPassThroughOnSwitch { get; set; } = true;

    public string RestoreHotKey { get; set; } = "Ctrl+Alt+Shift+R";

    public string PassThroughHotKey { get; set; } = "Ctrl+Alt+Shift+T";

    public string? PreferredSerial { get; set; }

    public WindowPlacement? MirrorPlacement { get; set; }

    public bool ToolbarFollowsMirror { get; set; } = true;

    /// <summary>无边框画面窗口是否在鼠标悬停时显示拖动/缩放边框。</summary>
    public bool HoverFrameEnabled { get; set; } = true;

    /// <summary>开启后画面常态完全透明，鼠标移到画面上才显示出来。</summary>
    public bool HoverRevealEnabled { get; set; }

    /// <summary>画面看不见时用什么方式提示它的位置（None / Outline / CornerMark）。</summary>
    public string HoverRevealHint { get; set; } = HoverRevealHints.Outline;

    /// <summary>工具条自己拖出来的位置，跟随画面关闭时才会用到。</summary>
    public WindowPlacement? ToolbarPlacement { get; set; }

    /// <summary>上次使用的连接方式页签（Usb / Lan）。</summary>
    public string ConnectionMode { get; set; } = ConnectionModes.Usb;

    /// <summary>局域网设备断线后是否自动重连（含启动时恢复上次局域网会话）。</summary>
    public bool LanAutoReconnect { get; set; } = true;

    public List<LanDeviceEntry> LanDevices { get; set; } = [];
}

public static class QualityNames
{
    public const string Smooth = "Smooth";
    public const string Balanced = "Balanced";
    public const string Sharp = "Sharp";
}

public static class SettingsStore
{
    /// <summary>
    /// 只读模式：冒烟检查等场景下不把配置写回去，
    /// 免得和正在运行的另一个实例互相覆盖设置。
    /// </summary>
    public static bool ReadOnly { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LN-Mirror", "settings.json");

    public static string AppDataDirectory { get; } = Path.GetDirectoryName(SettingsPath)!;

    public static string LogDirectory { get; } = Path.Combine(AppDataDirectory, "logs");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null) return Normalize(settings);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[配置] 读取失败，改用默认配置：{ex.Message}");
        }

        return Normalize(new AppSettings());
    }

    public static void Save(AppSettings settings)
    {
        if (ReadOnly) return;

        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            var json = JsonSerializer.Serialize(settings, Options);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"[配置] 保存失败：{ex.Message}");
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Version = 2;
        settings.Opacity = Math.Clamp(settings.Opacity is >= 1 and <= 100 ? settings.Opacity : 100, 0, 100);
        settings.RestoreHotKey = string.IsNullOrWhiteSpace(settings.RestoreHotKey) ? "Ctrl+Alt+Shift+R" : settings.RestoreHotKey;
        settings.PassThroughHotKey = string.IsNullOrWhiteSpace(settings.PassThroughHotKey) ? "Ctrl+Alt+Shift+T" : settings.PassThroughHotKey;
        settings.RenderDriver = string.IsNullOrWhiteSpace(settings.RenderDriver) ? "auto" : settings.RenderDriver;
        if (settings.ConnectionMode is not (ConnectionModes.Usb or ConnectionModes.Lan))
        {
            settings.ConnectionMode = ConnectionModes.Usb;
        }

        if (settings.HoverRevealHint is not (HoverRevealHints.None or HoverRevealHints.Outline or HoverRevealHints.CornerMark))
        {
            settings.HoverRevealHint = HoverRevealHints.Outline;
        }

        settings.LanDevices ??= [];
        settings.LanDevices.RemoveAll(d => string.IsNullOrWhiteSpace(d.LastAddress));
        return settings;
    }
}