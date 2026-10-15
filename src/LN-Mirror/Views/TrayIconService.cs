using System.Drawing;
using System.Windows.Forms;
using LNMirror.Core;
using LNMirror.Core.Diagnostics;

namespace LNMirror.Views;

/// <summary>托盘图标。托盘是独立恢复通道，不假设快捷键一定注册成功。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly AppController _controller;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _showToolbarItem;
    private readonly ToolStripMenuItem _restoreItem;
    private readonly ToolStripMenuItem _disconnectItem;
    private readonly Action _showMainWindow;
    private readonly Action _showToolbar;
    private readonly Action _exit;
    private bool _balloonShown;
    private bool _disposed;

    public TrayIconService(AppController controller, Action showMainWindow, Action showToolbar, Action exit)
    {
        _controller = controller;
        _showMainWindow = showMainWindow;
        _showToolbar = showToolbar;
        _exit = exit;

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => _showMainWindow());
        _showToolbarItem = (ToolStripMenuItem)menu.Items.Add("显示工具条", null, (_, _) => _showToolbar());
        _restoreItem = (ToolStripMenuItem)menu.Items.Add("恢复 100（取消穿透并找回画面）", null, async (_, _) => await _controller.RestoreAsync());
        _disconnectItem = (ToolStripMenuItem)menu.Items.Add("断开", null, async (_, _) => await _controller.DisconnectAsync("已从托盘断开"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 LN-Mirror", null, (_, _) => _exit());

        _icon = new NotifyIcon
        {
            Text = "LN-Mirror 手机悬浮投屏",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => _showMainWindow();

        _controller.PropertyChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.Invoke(UpdateMenuState);
        UpdateMenuState();
    }

    private void UpdateMenuState()
    {
        if (_disposed) return;
        bool connected = _controller.IsConnected;
        _showToolbarItem.Enabled = connected;
        _restoreItem.Enabled = connected;
        _disconnectItem.Enabled = connected;
    }

    public void ShowMainWindow() => _showMainWindow();

    public void ShowToolbar() => _showToolbar();

    public void NotifyMinimizedToTray()
    {
        if (_balloonShown) return;
        _balloonShown = true;

        try
        {
            _icon.ShowBalloonTip(3000, "LN-Mirror 已在托盘运行",
                "画面窗口和工具条仍然工作，可从托盘找回主窗口。", ToolTipIcon.Info);
        }
        catch
        {
            // 部分环境下气泡提示不可用
        }
    }

    private static Icon LoadIcon()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
            Path.Combine(AppContext.BaseDirectory, "app.ico"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    return new Icon(path);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"[托盘] 读取图标失败：{ex.Message}");
                }
            }
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}