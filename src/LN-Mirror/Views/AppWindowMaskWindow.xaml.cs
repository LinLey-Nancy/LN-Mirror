using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LNMirror.Core.Interop;
using LNMirror.Core.Windowing;

namespace LNMirror.Views;

public partial class AppWindowMaskWindow : Window
{
    private readonly DesktopAppController _controller;
    private readonly DispatcherTimer _timer;

    public AppWindowMaskWindow(DesktopAppController controller)
    {
        _controller = controller;
        DataContext = controller;
        InitializeComponent();
        _controller.PropertyChanged += OnControllerPropertyChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => SyncToTarget();
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowExStyle(Handle);
            exStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT
                | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
            NativeMethods.SetWindowExStyle(Handle, exStyle);
            NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
            SyncToTarget();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _controller.PropertyChanged -= OnControllerPropertyChanged;
        };
        _timer.Start();
    }

    public IntPtr Handle { get; private set; }

    public void SyncToTarget()
    {
        if (!_controller.MaskVisible || _controller.TargetRect is not { IsEmpty: false } rect)
        {
            if (IsVisible) Hide();
            return;
        }

        if (!IsVisible) Show();
        if (Handle == IntPtr.Zero) return;

        uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW;
        IntPtr insertAfter;
        if (_controller.MaskTopmost)
        {
            insertAfter = new IntPtr(NativeMethods.HWND_TOPMOST);
        }
        else
        {
            IntPtr previous = NativeMethods.GetWindow(_controller.TargetHandle, NativeMethods.GW_HWNDPREV);
            if (previous == Handle)
            {
                flags |= NativeMethods.SWP_NOZORDER;
                insertAfter = IntPtr.Zero;
            }
            else
            {
                insertAfter = previous == IntPtr.Zero ? new IntPtr(NativeMethods.HWND_TOP) : previous;
            }
        }

        NativeMethods.SetWindowPos(Handle, insertAfter, rect.Left, rect.Top, rect.Width, rect.Height, flags);
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DesktopAppController.MaskVisible)
            or nameof(DesktopAppController.TargetRect)
            or nameof(DesktopAppController.MaskTopmost))
        {
            Dispatcher.BeginInvoke(SyncToTarget);
        }
    }
}
