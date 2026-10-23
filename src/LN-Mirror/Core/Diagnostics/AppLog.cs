using System.Collections.Concurrent;
using System.Text;

namespace LNMirror.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string Format() => $"{Time:HH:mm:ss} [{LevelText(Level)}] {Message}";

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Info => "信息",
        LogLevel.Warning => "警告",
        _ => "错误",
    };
}

/// <summary>
/// 诊断日志：只记录连接状态、版本、错误与性能诊断。
/// 不记录画面、输入内容、剪贴板、配对码和 ADB 私钥。
/// </summary>
public static class AppLog
{
    private const int MaxRecent = 400;

    private static readonly object Gate = new();
    private static readonly Queue<LogEntry> Recent = new();
    private static string _directory = string.Empty;
    private static string _currentFile = string.Empty;
    private static LogLevel _fileMinimumLevel = LogLevel.Debug;

    public static event Action<LogEntry>? EntryAdded;

    public static string DirectoryPath => _directory;

    public static string CurrentFilePath => _currentFile;

    public static void Initialize(string directory, LogLevel fileMinimumLevel = LogLevel.Debug)
    {
        lock (Gate)
        {
            _directory = directory;
            _fileMinimumLevel = fileMinimumLevel;
            Directory.CreateDirectory(_directory);
            _currentFile = Path.Combine(_directory, $"LN-Mirror-{DateTime.Now:yyyyMMdd}.log");
        }
    }

    public static void SetMinimumFileLevel(LogLevel level)
    {
        lock (Gate) _fileMinimumLevel = level;
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex)
    {
        // 带堆栈：没有堆栈的错误日志等于只能猜，异常类名+消息不足以定位
        var stack = string.IsNullOrWhiteSpace(ex.StackTrace)
            ? string.Empty
            : "\n" + string.Join("\n", ex.StackTrace.Split('\n').Select(l => "    " + l.TrimEnd()));
        Write(LogLevel.Error, $"{message}：{ex.GetType().Name} {ex.Message}{stack}");
    }

    public static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);

        lock (Gate)
        {
            Recent.Enqueue(entry);
            while (Recent.Count > MaxRecent) Recent.Dequeue();

            if (level >= _fileMinimumLevel && !string.IsNullOrEmpty(_currentFile))
            {
                try
                {
                    File.AppendAllText(_currentFile, entry.Format() + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 日志写入失败不影响主流程
                }
            }
        }

        try
        {
            EntryAdded?.Invoke(entry);
        }
        catch
        {
            // 订阅者异常不参与
        }
    }

    public static IReadOnlyList<LogEntry> Snapshot(LogLevel minimumLevel = LogLevel.Info, int maxCount = MaxRecent)
    {
        lock (Gate)
        {
            return Recent
                .Where(e => e.Level >= minimumLevel)
                .TakeLast(maxCount)
                .ToList();
        }
    }

    /// <summary>用于日志面板：按时间正序拼成多行文本。</summary>
    public static string FormatRecent(LogLevel minimumLevel = LogLevel.Info, int maxCount = 200)
        => string.Join(Environment.NewLine, Snapshot(minimumLevel, maxCount).Select(e => e.Format()));
}

/// <summary>无锁的滚动文本缓存，供界面增量显示。</summary>
public sealed class LogBuffer
{
    private const int MaxLines = 400;
    private readonly ConcurrentQueue<string> _lines = new();

    public event Action? Changed;

    public void Append(LogEntry entry)
    {
        if (entry.Level == LogLevel.Debug) return;
        _lines.Enqueue(entry.Format());
        while (_lines.Count > MaxLines && _lines.TryDequeue(out _)) { }
        Changed?.Invoke();
    }

    public string Text => string.Join(Environment.NewLine, _lines);
}