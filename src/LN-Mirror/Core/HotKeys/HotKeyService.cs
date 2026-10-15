using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using LNMirror.Core.Diagnostics;
using LNMirror.Core.Interop;

namespace LNMirror.Core.HotKeys;

public enum HotKeyConflict
{
    None = 0,
    InvalidFormat,
    AlreadyRegistered,
    NeedsModifier,
}

/// <summary>全局快捷键。注册失败时如实报告，不假设一定能注册成功。</summary>
public readonly record struct HotKey(uint Modifiers, Key Key)
{
    public static HotKeyConflict LastConflict { get; private set; }

    public static bool TryParse(string? text, out HotKey hotKey)
    {
        hotKey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            LastConflict = HotKeyConflict.InvalidFormat;
            return false;
        }

        uint modifiers = 0;
        Key? key = null;
        var parts = text.Split(['+', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var raw in parts)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    continue;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    continue;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    continue;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.MOD_WIN;
                    continue;
            }

            if (!Enum.TryParse<Key>(raw, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                LastConflict = HotKeyConflict.InvalidFormat;
                return false;
            }

            if (key is not null)
            {
                LastConflict = HotKeyConflict.InvalidFormat;
                return false;
            }

            key = parsed;
        }

        if (key is null)
        {
            LastConflict = HotKeyConflict.InvalidFormat;
            return false;
        }

        // 必须带修饰键，否则会抢占正常打字
        if ((modifiers & (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_WIN)) == 0)
        {
            LastConflict = HotKeyConflict.NeedsModifier;
            return false;
        }

        hotKey = new HotKey(modifiers | NativeMethods.MOD_NOREPEAT, key.Value);
        LastConflict = HotKeyConflict.None;
        return true;
    }

    public string Format()
    {
        var sb = new StringBuilder();
        if ((Modifiers & NativeMethods.MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((Modifiers & NativeMethods.MOD_ALT) != 0) sb.Append("Alt+");
        if ((Modifiers & NativeMethods.MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((Modifiers & NativeMethods.MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(Key.ToString().ToUpperInvariant());
        return sb.ToString();
    }
}

/// <summary>把 RegisterHotKey 的回调转成事件，供 WPF 主线程处理。</summary>
public sealed class HotKeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _registered = new();
    private int _nextId = 0x4C4E;

    public HotKeyService()
    {
        var parameters = new HwndSourceParameters("LN-Mirror.HotKeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event Action<string>? HotKeyPressed;

    public bool Register(string displayName, string gestureText)
    {
        if (!HotKey.TryParse(gestureText, out var hotKey))
        {
            AppLog.Warn($"[热键] {displayName} 的组合键无效：{gestureText}（{HotKey.LastConflict}）");
            return false;
        }

        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, hotKey.Modifiers, (uint)hotKey.Key))
        {
            int err = Marshal.GetLastWin32Error();
            AppLog.Warn($"[热键] {displayName} 注册失败：{gestureText}（Win32 错误 {err}，通常已被其它程序占用）");
            return false;
        }

        _registered[id] = displayName;
        AppLog.Info($"[热键] {displayName} 已注册：{gestureText}");
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList())
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registered.TryGetValue(id, out var name))
            {
                handled = true;
                HotKeyPressed?.Invoke(name);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}