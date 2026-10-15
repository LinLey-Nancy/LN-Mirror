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

/// <summary>本地配置。只保存显示偏好，不保存设备授权信息。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

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
}

public static class QualityNames
{
    public const string Smooth = "Smooth";
    public const string Balanced = "Balanced";
    public const string Sharp = "Sharp";
}

public static class SettingsStore
{
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
        settings.Opacity = Math.Clamp(settings.Opacity is >= 1 and <= 100 ? settings.Opacity : 100, 0, 100);
        settings.RestoreHotKey = string.IsNullOrWhiteSpace(settings.RestoreHotKey) ? "Ctrl+Alt+Shift+R" : settings.RestoreHotKey;
        settings.PassThroughHotKey = string.IsNullOrWhiteSpace(settings.PassThroughHotKey) ? "Ctrl+Alt+Shift+T" : settings.PassThroughHotKey;
        settings.RenderDriver = string.IsNullOrWhiteSpace(settings.RenderDriver) ? "auto" : settings.RenderDriver;
        return settings;
    }
}