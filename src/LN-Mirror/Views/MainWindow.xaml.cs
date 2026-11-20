using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using LNMirror.Core;
using LNMirror.Core.Adb;
using LNMirror.Core.Configuration;
using LNMirror.Core.Diagnostics;

namespace LNMirror.Views;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly TrayIconService _tray;
    private bool _exitRequested;
    private bool _pairingInProgress;
    private IInputElement? _pairingReturnFocus;
    private int _logUpdatePending;

    public MainWindow(AppController controller, TrayIconService tray)
    {
        _controller = controller;
        _tray = tray;
        InitializeComponent();
        DataContext = controller;
        ApplyPhaseVisual();

        AppLog.EntryAdded += OnLogEntry;
        controller.PropertyChanged += OnControllerPropertyChanged;
    }

    public void AllowClose() => _exitRequested = true;

    public void ApplyPhaseVisual()
    {
        var (label, brush) = _controller.Phase switch
        {
            ConnectionPhase.Connected => ("投屏中", "OkBrush"),
            ConnectionPhase.Connecting => ("正在连接", "CenterMuted"),
            ConnectionPhase.Unauthorized => ("等待授权", "WarnBrush"),
            ConnectionPhase.Offline => ("设备离线", "ErrBrush"),
            ConnectionPhase.Failed => ("连接失败", "ErrBrush"),
            _ when _controller.IsBusy => ("正在连接", "CenterMuted"),
            _ when _controller.SelectedDevice?.IsReady == true => ("可连接", "CenterMuted"),
            _ => ("未连接", "CenterMuted"),
        };

        PhaseText.Text = label;
        PhaseText.Foreground = PhaseDot.Fill = (System.Windows.Media.Brush)FindResource(brush);
        ConnectButton.Visibility = _controller.CanDisconnect ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = _controller.CanDisconnect ? Visibility.Visible : Visibility.Collapsed;
        DisconnectButton.Content = _controller.IsConnected ? "结束投屏" : "取消连接";

        EmptyDeviceHint.Text = _controller.IsLanMode
            ? "未发现无线设备。请开启手机的无线调试，或添加设备。"
            : "暂无 USB 设备。请连接数据线，并在手机上允许 USB 调试。";
        ConnectionDetail.Text = _controller.Phase switch
        {
            ConnectionPhase.Unauthorized => "请解锁手机，在弹窗中允许这台电脑。",
            ConnectionPhase.Offline => _controller.SelectedDeviceStateText,
            ConnectionPhase.Failed => _controller.StatusLine,
            ConnectionPhase.Connected => _controller.SelectedDeviceText,
            _ => string.Empty,
        };
        ConnectionDetail.Visibility = string.IsNullOrWhiteSpace(ConnectionDetail.Text)
            ? Visibility.Collapsed : Visibility.Visible;

        var opacity = _controller.IsConnected ? _controller.EffectiveOpacity : _controller.Opacity;
        PreviewPhone.Opacity = opacity / 100d;
        PreviewOutline.Visibility = opacity == 0 && _controller.HoverRevealHint == HoverRevealHints.Outline
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewCorner.Visibility = opacity == 0 && _controller.HoverRevealHint == HoverRevealHints.CornerMark
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewState.Text = _controller.IsConnected
            ? _controller.OpacityStateText : "连接后可调节画面";
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

    private async void OnRefreshLanClick(object sender, RoutedEventArgs e)
        => await _controller.RefreshLanDiscoveryAsync();

    private void OnAddDeviceClick(object sender, RoutedEventArgs e) => OpenPairing();

    private void OpenPairing(string? address = null)
    {
        if (_controller.IsBusy || _controller.IsConnected)
        {
            _controller.Notice = _controller.IsConnected
                ? "请先结束当前投屏，再添加设备。" : "正在处理连接，请稍候。";
            return;
        }

        _pairingReturnFocus = Keyboard.FocusedElement;
        PairAddressBox.Text = address ?? string.Empty;
        PairCodeBox.Clear();
        PairingMessage.Visibility = Visibility.Collapsed;
        PairingOverlay.Visibility = Visibility.Visible;
        Navigation.IsEnabled = false;
        if (address is null) PairAddressBox.Focus();
        else PairCodeBox.Focus();
    }

    private void ClosePairing()
    {
        PairCodeBox.Clear();
        PairAddressBox.Clear();
        PairingMessage.Text = string.Empty;
        PairingOverlay.Visibility = Visibility.Collapsed;
        Navigation.IsEnabled = true;
        if (_pairingReturnFocus is { } previous && Keyboard.Focus(previous) != previous)
            AddDeviceButton.Focus();
        _pairingReturnFocus = null;
    }

    private void OnClosePairingClick(object sender, RoutedEventArgs e) => ClosePairing();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PairingOverlay.Visibility != Visibility.Visible || e.Key != Key.Escape) return;
        if (!_pairingInProgress) ClosePairing();
        e.Handled = true;
    }

    private async void OnDiscoveredItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MdnsServiceInfo service) return;

        if (service.Kind == MdnsServiceKind.Pairing)
        {
            // 配对服务只提供地址，配对码仍需用户在手机配对对话框里查看后输入
            OpenPairing(service.Address);
            return;
        }

        await _controller.ConnectDiscoveredLanAsync(service);
        ApplyPhaseVisual();
        if (_controller.IsConnected) _tray.ShowToolbar();
    }

    private async void OnPairConnectClick(object sender, RoutedEventArgs e)
    {
        if (_pairingInProgress) return;

        _pairingInProgress = true;
        PairFields.IsEnabled = PairSubmitButton.IsEnabled = PairCloseButton.IsEnabled = CancelPairButton.IsEnabled = false;
        PairingMessage.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "CenterMuted");
        PairingMessage.Text = "正在配对并连接…";
        PairingMessage.Visibility = Visibility.Visible;
        try
        {
            await _controller.PairAndConnectAsync(PairAddressBox.Text, PairCodeBox.Text);
        }
        finally
        {
            PairCodeBox.Clear();
            PairFields.IsEnabled = PairSubmitButton.IsEnabled = PairCloseButton.IsEnabled = CancelPairButton.IsEnabled = true;
            _pairingInProgress = false;
        }

        ApplyPhaseVisual();
        if (_controller.IsConnected)
        {
            ClosePairing();
            _tray.ShowToolbar();
        }
        else
        {
            PairingMessage.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ErrBrush");
            PairingMessage.Text = string.IsNullOrWhiteSpace(_controller.Notice)
                ? "配对未完成，请重新查看手机上的配对信息。" : _controller.Notice;
            _controller.Notice = string.Empty;
            PairCodeBox.Focus();
        }
    }

    private async void OnManualLanConnectClick(object sender, RoutedEventArgs e)
    {
        await _controller.ConnectLanAsync(ManualAddressBox.Text);
        ApplyPhaseVisual();
        if (_controller.IsConnected) _tray.ShowToolbar();
    }

    private async void OnUsbSwitchLanClick(object sender, RoutedEventArgs e)
    {
        await _controller.SwitchUsbToLanAsync(UsbSwitchAddressBox.Text);
        ApplyPhaseVisual();
        if (_controller.IsConnected) _tray.ShowToolbar();
    }

    private async void OnHistoryConnectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LanDeviceEntry entry) return;
        await _controller.ConnectHistoryAsync(entry);
        ApplyPhaseVisual();
        if (_controller.IsConnected) _tray.ShowToolbar();
    }

    private async void OnHistoryRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LanDeviceEntry entry) return;
        await _controller.RemoveHistoryAsync(entry);
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        await _controller.RestoreAsync();
        _tray.ShowToolbar();
    }

    private async void OnVerifyPassThroughClick(object sender, RoutedEventArgs e)
        => await _controller.VerifyPassThroughAsync();

    /// <summary>把工具条重新叫出来（收起之后唯一的找回入口之一）。</summary>
    private void OnShowToolbarClick(object sender, RoutedEventArgs e)
    {
        if (!_controller.IsConnected)
        {
            _controller.Notice = "还没有连接，画面未建立时没有工具条。";
            return;
        }

        _tray.ShowToolbar();
    }

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
        if (e.PropertyName is nameof(AppController.Phase) or nameof(AppController.IsBusy)
            or nameof(AppController.IsLanMode) or nameof(AppController.SelectedDevice)
            or nameof(AppController.Opacity) or nameof(AppController.EffectiveOpacity)
            or nameof(AppController.OpacityStateText) or nameof(AppController.HoverRevealHint)
            or nameof(AppController.StatusLine))
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
        if (PairingOverlay.Visibility == Visibility.Visible && !_pairingInProgress) ClosePairing();
        Hide();
        _tray.NotifyMinimizedToTray();
    }
}
