using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LNMirror.Core;
using LNMirror.Core.Configuration;
using LNMirror.Core.Diagnostics;

namespace LNMirror.Views;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly TrayIconService _tray;
    private bool _exitRequested;
    private int _logUpdatePending;

    public MainWindow(AppController controller, TrayIconService tray)
    {
        _controller = controller;
        _tray = tray;
        InitializeComponent();
        DataContext = controller;

        AppLog.EntryAdded += OnLogEntry;
        controller.PropertyChanged += OnControllerPropertyChanged;
    }

    public void AllowClose() => _exitRequested = true;

    public void ApplyPhaseVisual()
    {
        var (background, border, foreground) = _controller.Phase switch
        {
            Core.ConnectionPhase.Connected => ("#FFEAF4EA", "#FFBBD9BB", "#FF24562A"),
            Core.ConnectionPhase.Connecting => ("#FFF3F0E4", "#FFE0D3A8", "#FF6B5310"),
            Core.ConnectionPhase.Unauthorized => ("#FFF3F0E4", "#FFE0D3A8", "#FF6B5310"),
            Core.ConnectionPhase.Offline => ("#FFF6ECE9", "#FFE6C4BB", "#FF8A3524"),
            Core.ConnectionPhase.Failed => ("#FFF6ECE9", "#FFE6C4BB", "#FF8A3524"),
            _ => ("#FFF0F0EE", "#FFE2E2E0", "#FF232323"),
        };

        PhaseStrip.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        PhaseStrip.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
        PhaseText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        ApplyPhaseVisual();
        await _controller.ConnectAsync();
        ApplyPhaseVisual();
        if (_controller.IsConnected) _tray.ShowToolbar();
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        await _controller.DisconnectAsync("已手动断开");
        ApplyPhaseVisual();
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        await _controller.RestoreAsync();
        _tray.ShowToolbar();
    }

    private async void OnVerifyPassThroughClick(object sender, RoutedEventArgs e)
        => await _controller.VerifyPassThroughAsync();

    private async void OnApplyAndReconnectClick(object sender, RoutedEventArgs e)
    {
        await _controller.ApplyAndReconnectAsync("画面设置");
        ApplyPhaseVisual();
        _tray.ShowToolbar();
    }

    private async void OnSelfTestClick(object sender, RoutedEventArgs e)
    {
        var report = await SelfTest.RunAsync(_controller);
        MessageBox.Show(this, report, "LN-Mirror 环境自检", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var text = SelfTest.BuildDiagnostics(_controller);
        try
        {
            Clipboard.SetText(text);
            _controller.Notice = "诊断信息已复制到剪贴板，可直接粘贴发给我。";
        }
        catch (Exception ex)
        {
            _controller.Notice = $"复制失败：{ex.Message}";
        }
    }

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.LogDirectory);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { SettingsStore.LogDirectory },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            _controller.Notice = $"无法打开日志目录：{ex.Message}";
        }
    }

    private void OnOpacityPreviewInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (!char.IsDigit(e.Text, 0)) e.Handled = true;
    }

    /// <summary>快捷键输入框：直接把按键组合写成标准格式，避免手打出无效内容。</summary>
    private void OnHotKeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        var box = (System.Windows.Controls.TextBox)sender;

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            box.Text = box.Tag as string == "restore" ? _controller.HotkeyRestore : _controller.HotkeyPassThrough;
            e.Handled = true;
            return;
        }

        string? keyName = e.Key switch
        {
            >= Key.A and <= Key.Z => e.Key.ToString(),
            >= Key.D0 and <= Key.D9 => e.Key.ToString().Substring(1),
            >= Key.F1 and <= Key.F24 => e.Key.ToString(),
            Key.OemTilde => "OemTilde",
            Key.OemPeriod => "OemPeriod",
            Key.OemComma => "OemComma",
            Key.OemQuestion => "OemQuestion",
            _ => null,
        };

        if (keyName is null)
        {
            e.Handled = true;
            return;
        }

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(keyName);

        var gesture = string.Join('+', parts);
        box.Text = gesture;
        e.Handled = true;

        if (box.Tag as string == "restore")
        {
            _controller.HotkeyRestore = gesture;
        }
        else
        {
            _controller.HotkeyPassThrough = gesture;
        }

        _ = _controller.UpdateHotKeysAsync(_controller.HotkeyRestore, _controller.HotkeyPassThrough);
    }

    private void OnControllerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppController.Phase) or nameof(AppController.Opacity)
            or nameof(AppController.PassThrough) or nameof(AppController.IsConnected))
        {
            Dispatcher.InvokeAsync(ApplyPhaseVisual);
        }

        if (e.PropertyName is nameof(AppController.HotkeyRestore) or nameof(AppController.HotkeyPassThrough))
        {
            Dispatcher.InvokeAsync(async () => await _controller.UpdateHotKeysAsync(_controller.HotkeyRestore, _controller.HotkeyPassThrough));
        }
    }

    private void OnLogEntry(LogEntry entry)
    {
        if (entry.Level == LogLevel.Debug) return;
        if (Interlocked.Increment(ref _logUpdatePending) > 1) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _logUpdatePending, 0);
            LogBox.AppendText(entry.Format() + Environment.NewLine);

            int overflow = LogBox.Text.Length - 200_000;
            if (overflow > 0)
            {
                LogBox.Text = LogBox.Text[overflow..];
                LogBox.CaretIndex = LogBox.Text.Length;
            }
        }));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested)
        {
            AppLog.EntryAdded -= OnLogEntry;
            _controller.PropertyChanged -= OnControllerPropertyChanged;
            return;
        }

        e.Cancel = true;
        Hide();
        _tray.NotifyMinimizedToTray();
    }
}