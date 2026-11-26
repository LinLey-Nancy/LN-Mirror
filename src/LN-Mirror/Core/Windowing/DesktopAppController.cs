using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LNMirror.Core.Configuration;
using LNMirror.Core.Diagnostics;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

public sealed record AppWindowDisplayModeOption(string Key, string DisplayName);

public sealed class DesktopAppController : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings _settings;
    private readonly WindowStyleController _window = new();
    private readonly MirrorHoverWatcher _hoverWatcher;
    private readonly DispatcherTimer _timer;
    private readonly List<AppWindowDisplayModeOption> _displayModeOptions =
    [
        new(AppWindowDisplayModes.Original, "原始窗口"),
        new(AppWindowDisplayModes.HoverReveal, "悬停显示"),
        new(AppWindowDisplayModes.Image, "始终显示图片"),
        new(AppWindowDisplayModes.ImageHoverReveal, "图片 · 悬停显示原窗口"),
    ];

    private DesktopWindowInfo? _selectedWindow;
    private BitmapImage? _maskImage;
    private WindowRect? _targetRect;
    private string _notice = string.Empty;
    private string _statusText = "请选择一个窗口";
    private bool _maskVisible;
    private bool _elevationRequired;
    private bool _disposed;
    private bool _appearanceDirty = true;
    private bool _wasMinimized;
    private long _lastRefresh;
    private int _effectiveTargetOpacity = 100;
    private int _lastError;
    private ElevatedWindowSession? _elevatedSession;
    private bool _isBusy;
    private int _applyVersion;
    private bool _selectionChanging;
    private bool _refreshingWindows;
    private readonly WindowRecoveryStore _recovery;
    private readonly Action<AppSettings>? _saveSettings;

    public DesktopAppController(
        AppSettings settings,
        Dispatcher dispatcher,
        string? recoveryFilePath = null,
        Action<AppSettings>? saveSettings = null,
        GlobalMouseClickWatcher? clickWatcher = null)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _recovery = new WindowRecoveryStore(recoveryFilePath ?? Path.Combine(SettingsStore.AppDataDirectory, "app-window-recovery.json"));
        _recovery.Recover();
        _hoverWatcher = new MirrorHoverWatcher(() => TargetRect, clickWatcher: clickWatcher);
        _hoverWatcher.KeepEnabled = settings.AppWindow.HoverKeepOnLeave;
        _hoverWatcher.SetDelay(0, false);
        _hoverWatcher.HoverChanged += OnHoverChanged;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, Tick, dispatcher);
        _timer.Start();
        LoadSavedImage();
        RefreshWindows();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DesktopWindowInfo> Windows { get; } = [];
    public IReadOnlyList<AppWindowDisplayModeOption> DisplayModeOptions => _displayModeOptions;
    public IReadOnlyList<string> ImageStretchOptions { get; } = ["Uniform", "UniformToFill", "Fill", "None"];

    public DesktopWindowInfo? SelectedWindow
    {
        get => _selectedWindow;
        set => SelectWindow(value);
    }

    public bool IsTargetActive => _selectedWindow is not null && _window.IsValid;
    public bool IsManageEnabled => IsTargetActive && !IsBusy;
    public bool HasTarget => _selectedWindow is not null;
    public string TargetTitle => _selectedWindow?.DisplayTitle ?? "尚未选择窗口";
    public string TargetProcess => _selectedWindow?.ProcessDisplay ?? "从左侧列表选择一个普通应用窗口";
    public IntPtr TargetHandle => _window.Handle;
    public uint TargetProcessId => _selectedWindow?.ProcessId ?? 0;
    public long TargetProcessStartTimeTicks => _selectedWindow?.ProcessStartTimeTicks ?? 0;

    public int Opacity
    {
        get => _settings.AppWindow.Opacity;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (_settings.AppWindow.Opacity == value) return;
            _settings.AppWindow.Opacity = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskOpacity));
            InvalidateAppearance();
        }
    }

    public double MaskOpacity => Opacity / 100d;

    public bool Topmost
    {
        get => _settings.AppWindow.Topmost;
        set
        {
            if (_settings.AppWindow.Topmost == value) return;
            _settings.AppWindow.Topmost = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskTopmost));
            InvalidateAppearance();
        }
    }

    public bool MaskTopmost => Topmost;

    public bool TaskSwitcherHidden
    {
        get => _settings.AppWindow.TaskSwitcherHidden;
        set
        {
            if (_settings.AppWindow.TaskSwitcherHidden == value) return;
            _settings.AppWindow.TaskSwitcherHidden = value;
            SaveSettings();
            OnPropertyChanged();
            InvalidateAppearance();
        }
    }

    /// <summary>两种悬停模式下，鼠标移开后保持显示，直到下一次任意鼠标点击。</summary>
    public bool KeepOnLeave
    {
        get => _settings.AppWindow.HoverKeepOnLeave;
        set
        {
            if (_settings.AppWindow.HoverKeepOnLeave == value) return;
            _settings.AppWindow.HoverKeepOnLeave = value;
            _hoverWatcher.KeepEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            InvalidateAppearance();
        }
    }

    public string DisplayMode
    {
        get => _settings.AppWindow.DisplayMode;
        set => SetDisplayMode(value);
    }

    public bool UsesImage => AppWindowDisplayModes.UsesImage(DisplayMode);
    public bool UsesHover => AppWindowDisplayModes.UsesHover(DisplayMode);
    public bool HasImage => _maskImage is not null;
    public bool IsImageMode => UsesImage;
    public string OpacityLabel => UsesImage && !(_hoverWatcher.IsRevealed && DisplayMode == AppWindowDisplayModes.ImageHoverReveal)
        ? "图片不透明度"
        : "窗口不透明度";
    public string ImagePath => _settings.AppWindow.ImagePath;
    public string ImageName => string.IsNullOrWhiteSpace(ImagePath) ? "尚未选择图片" : Path.GetFileName(ImagePath);
    public ImageSource? MaskImage => _maskImage;
    public Stretch MaskStretch => Enum.TryParse<Stretch>(_settings.AppWindow.ImageStretch, out var stretch) ? stretch : Stretch.Uniform;

    public string ImageStretch
    {
        get => _settings.AppWindow.ImageStretch;
        set
        {
            if (value is not ("Uniform" or "UniformToFill" or "Fill" or "None") || _settings.AppWindow.ImageStretch == value) return;
            _settings.AppWindow.ImageStretch = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskStretch));
        }
    }

    public WindowRect? TargetRect
    {
        get => _targetRect;
        private set => Set(ref _targetRect, value);
    }

    public bool MaskVisible
    {
        get => _maskVisible;
        private set => Set(ref _maskVisible, value);
    }

    public int EffectiveTargetOpacity
    {
        get => _effectiveTargetOpacity;
        private set => Set(ref _effectiveTargetOpacity, value);
    }

    public int LastError
    {
        get => _lastError;
        private set => Set(ref _lastError, value);
    }

    public bool ElevationRequired
    {
        get => _elevationRequired;
        private set
        {
            if (Set(ref _elevationRequired, value)) OnPropertyChanged(nameof(CanRequestElevation));
        }
    }

    public bool CanRequestElevation => ElevationRequired && HasTarget && !IsBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsManageEnabled));
            OnPropertyChanged(nameof(CanRequestElevation));
        }
    }

    public string Notice
    {
        get => _notice;
        set => Set(ref _notice, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public void RefreshWindows()
    {
        if (_refreshingWindows) return;
        _refreshingWindows = true;
        try
        {
            _lastRefresh = Environment.TickCount64;
            var fresh = DesktopWindowEnumerator.Enumerate(_selectedWindow?.Handle ?? IntPtr.Zero);
            bool unchanged = fresh.Count == Windows.Count && fresh.Zip(Windows).All(pair =>
                pair.First.Handle == pair.Second.Handle
                && pair.First.Title == pair.Second.Title
                && pair.First.IsMinimized == pair.Second.IsMinimized
                && pair.First.ProcessId == pair.Second.ProcessId);
            if (unchanged) return;

            Windows.Clear();
            foreach (var window in fresh) Windows.Add(window);
            if (_selectedWindow is not null)
            {
                _selectedWindow = fresh.FirstOrDefault(window => window.Handle == _selectedWindow.Handle) ?? _selectedWindow;
                OnPropertyChanged(nameof(SelectedWindow));
                OnPropertyChanged(nameof(TargetTitle));
                OnPropertyChanged(nameof(TargetProcess));
            }
        }
        finally
        {
            _refreshingWindows = false;
        }
    }

    public void SelectWindow(DesktopWindowInfo? info)
    {
        if (_selectionChanging || _refreshingWindows) return;
        _selectionChanging = true;
        try
        {
            if (info is null)
            {
                RestoreCore();
                return;
            }
            if (_selectedWindow?.Handle == info.Handle && _window.IsValid) return;

            ReleaseTarget();
            _selectedWindow = info;
            if (!Windows.Any(window => window.Handle == info.Handle)) Windows.Add(info);
            _window.Attach(info.Handle);
            _recovery.Save(info, _window.CreateSnapshot());
            _window.RestoreIfMinimized();
            ElevationRequired = false;
            LastError = 0;
            Notice = string.Empty;
            StatusText = "正在应用窗口设置…";
            InvalidateAppearance();
            NotifyTargetChanged();
        }
        finally
        {
            _selectionChanging = false;
        }
    }

    public bool ChooseImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Notice = "图片文件不存在，请重新选择。";
            return false;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            _maskImage = image;
            _settings.AppWindow.ImagePath = Path.GetFullPath(path);
            SaveSettings();
            OnPropertyChanged(nameof(MaskImage));
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(ImagePath));
            OnPropertyChanged(nameof(ImageName));
            Notice = string.Empty;
            SetDisplayMode(AppWindowDisplayModes.ImageHoverReveal);
            return true;
        }
        catch (Exception ex)
        {
            Notice = $"无法读取图片：{ex.Message}";
            return false;
        }
    }

    public void SetDisplayMode(string? mode)
    {
        mode = AppWindowDisplayModes.Normalize(mode);
        if (AppWindowDisplayModes.UsesImage(mode) && !HasImage)
        {
            Notice = "请先选择一张图片。";
            OnPropertyChanged(nameof(DisplayMode));
            return;
        }
        if (_settings.AppWindow.DisplayMode == mode) return;
        _settings.AppWindow.DisplayMode = mode;
        SaveSettings();
        _hoverWatcher.Stop();
        OnPropertyChanged(nameof(DisplayMode));
        OnPropertyChanged(nameof(UsesImage));
        OnPropertyChanged(nameof(UsesHover));
        OnPropertyChanged(nameof(IsImageMode));
        OnPropertyChanged(nameof(OpacityLabel));
        InvalidateAppearance();
    }

    public async Task RetryWithElevationAsync()
    {
        if (!CanRequestElevation || _selectedWindow is not { } target) return;
        IsBusy = true;
        Notice = "正在等待 Windows 管理员授权…";
        _hoverWatcher.Stop();
        try
        {
            var handle = target.Handle;
            _window.Detach();
            _window.Attach(handle);
            _recovery.Save(target, _window.CreateSnapshot());
            var session = await ElevatedWindowSession.StartAsync(target).ConfigureAwait(true);
            if (_selectedWindow?.Handle != target.Handle || _disposed)
            {
                session.Dispose();
                return;
            }

            _elevatedSession?.Dispose();
            _elevatedSession = session;
            ElevationRequired = false;
            LastError = 0;
            Notice = "已取得本次窗口管理所需的管理员权限。";
            InvalidateAppearance();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Notice = "已取消管理员授权，窗口保持普通权限管理。";
        }
        catch (Exception ex)
        {
            LastError = ex is WindowHelperException helper ? helper.Error : 0;
            Notice = $"提权辅助进程启动失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Restore()
    {
        if (_selectionChanging || _refreshingWindows) return;
        _selectionChanging = true;
        try { RestoreCore(); }
        finally { _selectionChanging = false; }
    }

    private void RestoreCore()
    {
        ReleaseTarget();
        Notice = "已恢复窗口原始状态。";
        StatusText = "请选择一个窗口";
        RefreshWindows();
        OnPropertyChanged(nameof(SelectedWindow));
    }

    private void ReleaseTarget()
    {
        _applyVersion++;
        _hoverWatcher.Stop();
        _wasMinimized = false;
        MaskVisible = false;
        TargetRect = null;
        _elevatedSession?.Dispose();
        _elevatedSession = null;
        _window.Detach();
        _recovery.Clear();
        _selectedWindow = null;
        _appearanceDirty = true;
        NotifyTargetChanged();
    }

    private void NotifyTargetChanged()
    {
        OnPropertyChanged(nameof(SelectedWindow));
        OnPropertyChanged(nameof(IsTargetActive));
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(TargetTitle));
        OnPropertyChanged(nameof(TargetProcess));
        OnPropertyChanged(nameof(TargetHandle));
        OnPropertyChanged(nameof(TargetProcessId));
        OnPropertyChanged(nameof(TargetProcessStartTimeTicks));
        OnPropertyChanged(nameof(CanRequestElevation));
        OnPropertyChanged(nameof(IsManageEnabled));
    }

    private void InvalidateAppearance()
    {
        _appearanceDirty = true;
        _ = ApplyAppearanceAsync();
    }

    private async Task ApplyAppearanceAsync()
    {
        if (!_appearanceDirty || _disposed) return;
        _appearanceDirty = false;
        int version = ++_applyVersion;
        if (!IsTargetActive)
        {
            MaskVisible = false;
            UpdateHoverWatcher();
            return;
        }

        try
        {
            var state = AppWindowAppearance.Resolve(DisplayMode, Opacity, _hoverWatcher.IsRevealed);
            var result = await ExecuteOperationAsync("opacity", state.TargetOpacity).ConfigureAwait(true);
            if (result.Ok) result = await ExecuteOperationAsync("passthrough", state.TargetPassThrough ? 1 : 0).ConfigureAwait(true);
            if (result.Ok) result = await ExecuteOperationAsync("topmost", Topmost ? 1 : 0).ConfigureAwait(true);
            if (result.Ok) result = await ExecuteOperationAsync("switcher", TaskSwitcherHidden ? 1 : 0).ConfigureAwait(true);
            if (version != _applyVersion || _disposed) return;
            if (!result.Ok)
            {
                HandleOperationFailure(result.Error, result.Message);
                return;
            }

            LastError = 0;
            ElevationRequired = false;
            EffectiveTargetOpacity = state.TargetOpacity;
            TargetRect = _window.GetRect();
            MaskVisible = state.MaskVisible && HasImage && TargetRect is { IsEmpty: false } && !_window.IsMinimized;
            StatusText = UsesImage && MaskVisible
                ? $"图片覆盖中 · {Opacity}% · 点击仍由原应用处理"
                : $"窗口显示中 · {EffectiveTargetOpacity}%";
            UpdateHoverWatcher();
        }
        catch (Exception ex)
        {
            if (version == _applyVersion && !_disposed) HandleOperationFailure(4000, ex.Message);
        }
    }

    private async Task<WindowHelperResponse> ExecuteOperationAsync(string command, int value)
    {
        if (_elevatedSession is not null)
            return await _elevatedSession.SendAsync(new WindowHelperRequest { Command = command, Value = value }).ConfigureAwait(true);

        bool ok = command switch
        {
            "opacity" => _window.ApplyOpacity(value) is not null,
            "passthrough" => _window.ApplyPassThrough(value != 0),
            "topmost" => _window.ApplyTopmost(value != 0),
            "switcher" => _window.ApplyTaskSwitcherHidden(value != 0),
            _ => false,
        };
        return new WindowHelperResponse { Ok = ok, Error = ok ? 0 : _window.LastError };
    }

    private void HandleOperationFailure(int error, string? detail = null)
    {
        // 窗口已经不可管理时，先清掉保持状态并卸载全局监听。
        _hoverWatcher.Stop();
        bool wasElevated = _elevatedSession is not null;
        _elevatedSession?.Dispose();
        _elevatedSession = null;
        var handle = _window.Handle;
        MaskVisible = false;
        _window.Detach();
        if (NativeMethods.IsWindow(handle) && _selectedWindow is not null)
        {
            _window.Attach(handle);
            _recovery.Save(_selectedWindow, _window.CreateSnapshot());
        }
        LastError = error;
        ElevationRequired = error == 5 && !wasElevated;
        Notice = ElevationRequired
            ? "目标窗口需要管理员权限。普通权限下的修改已回滚，请使用管理员权限重试。"
            : wasElevated
                ? $"管理员权限下仍无法设置该窗口（Windows 错误 {error}），已恢复原状态。{detail}"
                : $"该窗口不支持所需的外观设置（Windows 错误 {error}），已恢复原状态。{detail}";
        StatusText = "设置未生效";
    }

    private void UpdateHoverWatcher()
    {
        if (IsTargetActive && UsesHover && !_window.IsMinimized) _hoverWatcher.Start();
        else _hoverWatcher.Stop();
    }

    private void OnHoverChanged(bool shown)
    {
        if (!UsesHover || !IsTargetActive) return;
        OnPropertyChanged(nameof(OpacityLabel));
        InvalidateAppearance();
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_selectedWindow is not null && !_window.IsValid)
        {
            SelectWindow(null);
            Notice = "目标窗口已关闭，已结束管理。";
            StatusText = "请选择一个窗口";
            RefreshWindows();
            return;
        }

        if (IsTargetActive)
        {
            TargetRect = _window.GetRect();
            bool minimized = _window.IsMinimized;
            if (minimized)
            {
                if (MaskVisible) MaskVisible = false;
                // 最小化时完全停止轮询；恢复时由下面的 _wasMinimized 分支重新启动。
                _hoverWatcher.Stop();
            }
            else
            {
                // 最小化时监听被停掉；恢复后要重新按当前显示方式启动，否则悬停显示不再工作。
                if (_wasMinimized) UpdateHoverWatcher();
                if (_appearanceDirty) _ = ApplyAppearanceAsync();
            }

            _wasMinimized = minimized;
        }

        if (Environment.TickCount64 - _lastRefresh >= 2000) RefreshWindows();
    }

    private void LoadSavedImage()
    {
        if (string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath)) return;
        try
        {
            var image = new BitmapImage(new Uri(ImagePath, UriKind.Absolute));
            image.Freeze();
            _maskImage = image;
        }
        catch
        {
            _settings.AppWindow.ImagePath = string.Empty;
            _settings.AppWindow.DisplayMode = AppWindowDisplayModes.Original;
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        if (_saveSettings is not null) _saveSettings(_settings);
        else SettingsStore.Save(_settings);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _hoverWatcher.Dispose();
        MaskVisible = false;
        _elevatedSession?.Dispose();
        _elevatedSession = null;
        _window.Dispose();
        _recovery.Clear();
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
