namespace LNMirror.Core.Windowing;

public enum HoverRevealKeepPhase
{
    /// <summary>未显示。</summary>
    Hidden,

    /// <summary>指针在区域内，内容按设置显示。</summary>
    Revealed,

    /// <summary>指针已离开，内容继续显示，等待下一次鼠标点击。</summary>
    Held,

    /// <summary>点击关闭后指针仍在区域内，等指针先离开才允许再次悬停显示。</summary>
    Suppressed,
}

/// <summary>
/// “移开仍显示”的纯状态机：不接触窗口、鼠标或计时器，
/// 由 <see cref="MirrorHoverWatcher"/> 在轮询和全局点击时驱动，便于独立测试。
/// </summary>
public sealed class HoverRevealKeepState
{
    public HoverRevealKeepPhase Phase { get; private set; } = HoverRevealKeepPhase.Hidden;

    public bool IsVisible => Phase is HoverRevealKeepPhase.Revealed or HoverRevealKeepPhase.Held;

    /// <summary>正在等待下一次点击关闭。只有这种状态才需要全局点击监听。</summary>
    public bool IsHolding => Phase == HoverRevealKeepPhase.Held;

    /// <summary>悬停延迟已满足。抑制状态下不显示，必须等指针先离开。</summary>
    public bool OnPointerEntered()
    {
        if (Phase != HoverRevealKeepPhase.Hidden) return false;
        Phase = HoverRevealKeepPhase.Revealed;
        return true;
    }

    /// <summary>指针离开区域。显示中按选项转入保持或隐藏；抑制状态在离开后解除。</summary>
    public bool OnPointerLeft(bool keepEnabled)
    {
        switch (Phase)
        {
            case HoverRevealKeepPhase.Revealed:
                Phase = keepEnabled ? HoverRevealKeepPhase.Held : HoverRevealKeepPhase.Hidden;
                return !keepEnabled;
            case HoverRevealKeepPhase.Suppressed:
                Phase = HoverRevealKeepPhase.Hidden;
                return false;
            default:
                return false;
        }
    }

    /// <summary>等待点击关闭时收到任意鼠标点击。返回内容是否从显示变为隐藏。</summary>
    public bool OnClick(bool pointerInside)
    {
        if (Phase != HoverRevealKeepPhase.Held) return false;
        Phase = pointerInside ? HoverRevealKeepPhase.Suppressed : HoverRevealKeepPhase.Hidden;
        return true;
    }

    /// <summary>用户取消“移开仍显示”：立即按当前指针位置恢复普通悬停规则。</summary>
    public bool OnKeepDisabled(bool pointerInside)
    {
        switch (Phase)
        {
            case HoverRevealKeepPhase.Held:
                Phase = pointerInside ? HoverRevealKeepPhase.Revealed : HoverRevealKeepPhase.Hidden;
                return !pointerInside;
            case HoverRevealKeepPhase.Suppressed:
                Phase = pointerInside ? HoverRevealKeepPhase.Revealed : HoverRevealKeepPhase.Hidden;
                return pointerInside;
            default:
                return false;
        }
    }

    /// <summary>清除保持、抑制和显示状态。</summary>
    public bool Reset()
    {
        bool wasVisible = IsVisible;
        Phase = HoverRevealKeepPhase.Hidden;
        return wasVisible;
    }
}
