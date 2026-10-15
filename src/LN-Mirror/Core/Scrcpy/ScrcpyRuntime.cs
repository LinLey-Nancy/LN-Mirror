using System.Globalization;
using System.Text.RegularExpressions;
using LNMirror.Core.Configuration;
using LNMirror.Core.Diagnostics;

namespace LNMirror.Core.Scrcpy;

public sealed record QualityProfile(string Key, string DisplayName, int MaxSize, int MaxFps, string BitRate)
{
    public string Summary => $"长边 {MaxSize} · 最高 {MaxFps} FPS · {BitRate}";

    public static readonly QualityProfile Smooth = new(QualityNames.Smooth, "流畅", 1280, 60, "4M");
    public static readonly QualityProfile Balanced = new(QualityNames.Balanced, "均衡（默认）", 1920, 60, "8M");
    public static readonly QualityProfile Sharp = new(QualityNames.Sharp, "清晰", 1920, 60, "12M");

    public static IReadOnlyList<QualityProfile> All { get; } = [Smooth, Balanced, Sharp];

    public static QualityProfile Resolve(string? key)
        => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Balanced;
}

public sealed record ScrcpyStartOptions
{
    public required string Serial { get; init; }

    public string Quality { get; init; } = QualityNames.Balanced;

    public bool Borderless { get; init; } = true;

    public string RenderDriver { get; init; } = "auto";

    public bool KeepAwake { get; init; }

    public bool PrintFps { get; init; } = true;

    public string Verbosity { get; init; } = "info";

    public int? WindowX { get; init; }

    public int? WindowY { get; init; }

    public int? WindowWidth { get; init; }
}

/// <summary>
/// 内置 scrcpy 引擎的位置与版本。程序只启动自己随包携带的这一份，
/// 保证电脑端与手机端 server 版本严格对应。
/// </summary>
public sealed class ScrcpyRuntime
{
    private ScrcpyRuntime(string directory)
    {
        Directory = directory;
        Executable = Path.Combine(directory, "scrcpy.exe");
        Adb = Path.Combine(directory, "adb.exe");
        Server = Path.Combine(directory, "scrcpy-server");
    }

    public string Directory { get; }

    public string Executable { get; }

    public string Adb { get; }

    public string Server { get; }

    /// <summary>目录名形如 scrcpy-win64-v5.0.1，用于显示锁定版本。</summary>
    public string VersionLabel
    {
        get
        {
            var name = System.IO.Path.GetFileName(Directory);
            var match = Regex.Match(name, @"v\d+\.\d+(\.\d+)?", RegexOptions.IgnoreCase);
            return match.Success ? match.Value : name;
        }
    }

    public bool IsComplete =>
        File.Exists(Executable) && File.Exists(Adb) && File.Exists(Server);

    public static ScrcpyRuntime? Discover()
    {
        var overrideDir = Environment.GetEnvironmentVariable("LN_MIRROR_SCRCPY_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            var forced = new ScrcpyRuntime(overrideDir);
            if (forced.IsComplete) return forced;
            AppLog.Warn($"[引擎] 环境变量指定的目录不完整：{overrideDir}");
        }

        var toolsRoot = Path.Combine(AppContext.BaseDirectory, "tools");
        if (!System.IO.Directory.Exists(toolsRoot)) return null;

        var candidates = System.IO.Directory.EnumerateDirectories(toolsRoot)
            .Select(d => new ScrcpyRuntime(d))
            .Where(r => r.IsComplete)
            .ToList();

        if (candidates.Count == 0) return null;

        var best = candidates
            .OrderByDescending(r => r.VersionLabel, Comparer<string>.Create(CompareVersion))
            .First();

        AppLog.Info($"[引擎] 使用内置 scrcpy：{best.Directory}（{best.VersionLabel}）");
        return best;
    }

    private static int CompareVersion(string left, string right)
    {
        static int[] Parse(string value)
        {
            var match = Regex.Match(value, @"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase);
            return match.Success
                ?
                [
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0,
                ]
                : [0, 0, 0];
        }

        var a = Parse(left);
        var b = Parse(right);
        for (int i = 0; i < 3; i++)
        {
            int cmp = a[i].CompareTo(b[i]);
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    /// <summary>按锁定版本生成 scrcpy 命令行。全部使用 --长选项=值 形式，避免参数顺序歧义。</summary>
    public static IReadOnlyList<string> BuildArguments(ScrcpyStartOptions options)
    {
        var quality = QualityProfile.Resolve(options.Quality);

        var args = new List<string>
        {
            $"--serial={options.Serial}",
            "--no-audio",
            "--video-codec=h264",
            $"--max-size={quality.MaxSize}",
            $"--max-fps={quality.MaxFps}",
            $"--video-bit-rate={quality.BitRate}",
            "--window-title=LN-Mirror 投屏",
            $"--verbosity={options.Verbosity}",
        };

        if (options.PrintFps) args.Add("--print-fps");
        if (options.Borderless) args.Add("--window-borderless");
        if (options.KeepAwake) args.Add("--stay-awake");
        if (!string.IsNullOrWhiteSpace(options.RenderDriver) && !options.RenderDriver.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            args.Add($"--render-driver={options.RenderDriver}");
        }

        if (options.WindowX is { } x) args.Add($"--window-x={x}");
        if (options.WindowY is { } y) args.Add($"--window-y={y}");
        if (options.WindowWidth is > 0 and < 20000 and var width) args.Add($"--window-width={width}");

        return args;
    }
}