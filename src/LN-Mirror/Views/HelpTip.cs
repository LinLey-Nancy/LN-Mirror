using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using CustomPopupPlacement = System.Windows.Controls.Primitives.CustomPopupPlacement;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using PlacementMode = System.Windows.Controls.Primitives.PlacementMode;
using Point = System.Windows.Point;
using PopupPrimaryAxis = System.Windows.Controls.Primitives.PopupPrimaryAxis;
using Size = System.Windows.Size;
using ToolTip = System.Windows.Controls.ToolTip;

namespace LNMirror.Views;

/// <summary>
/// 控件旁的圆形问号。鼠标悬停或键盘聚焦时显示说明，提示框限宽换行。
/// 图标是独立控件：放在复选框内容里也不会触发勾选，放在禁用按钮旁仍可查看。
/// </summary>
public sealed class HelpTip : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HelpTip),
        new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

    private readonly ToolTip _toolTip;
    private readonly TextBlock _content;

    public HelpTip()
    {
        Focusable = true;
        IsTabStop = true;
        Cursor = Cursors.Help;
        AutomationProperties.SetName(this, "帮助说明");

        _content = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            FontSize = 12,
        };

        _toolTip = new ToolTip
        {
            Content = _content,
            Placement = PlacementMode.Custom,
            PlacementTarget = this,
            CustomPopupPlacementCallback = PlaceToolTip,
            Padding = new Thickness(9, 7, 9, 7),
            HasDropShadow = true,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x17, 0x24, 0x34)),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0xC5, 0xD0)),
        };

        ToolTip = _toolTip;
        // 由本控件自己控制显隐，避免 ToolTipService 和键盘焦点两套机制互相抢。
        ToolTipService.SetIsEnabled(this, false);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HelpTip tip) tip.UpdateContent();
    }

    private void UpdateContent()
    {
        _content.Text = Text ?? string.Empty;
        AutomationProperties.SetHelpText(this, Text ?? string.Empty);
    }

    /// <summary>优先放在图标上方；顶部放不下时翻到下方。水平方向由 WPF 按屏幕边缘调整。</summary>
    private CustomPopupPlacement[] PlaceToolTip(Size popupSize, Size targetSize, Point offset)
    {
        double x = (targetSize.Width - popupSize.Width) / 2;
        double y = -popupSize.Height - 6;
        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is { } target)
            {
                var origin = PointToScreen(new Point(0, 0));
                var scale = target.TransformFromDevice;
                var screen = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point((int)Math.Round(origin.X), (int)Math.Round(origin.Y)));
                double targetTop = origin.Y * scale.M22;
                double workTop = screen.WorkingArea.Top * scale.M22;
                if (targetTop + y < workTop) y = targetSize.Height + 6;
            }
        }
        catch
        {
            // 取不到屏幕信息时退回图标上方；宁可贴边也不要挡住图标本身。
        }

        return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal)];
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _toolTip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsKeyboardFocused) _toolTip.IsOpen = false;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _toolTip.IsOpen = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!IsMouseOver) _toolTip.IsOpen = false;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // 放在 CheckBox.Content 里时，点击问号不应该顺带切换复选框或按钮。
        e.Handled = true;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        base.OnPreviewMouseLeftButtonUp(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        base.OnMouseLeftButtonUp(e);
    }
}
