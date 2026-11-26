using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

public static class AppWindowDisplayModes
{
    public const string Original = "Original";
    public const string HoverReveal = "HoverReveal";
    public const string Image = "Image";
    public const string ImageHoverReveal = "ImageHoverReveal";

    public static string Normalize(string? mode) => mode switch
    {
        HoverReveal or Image or ImageHoverReveal => mode,
        _ => Original,
    };

    public static bool UsesImage(string? mode)
        => Normalize(mode) is Image or ImageHoverReveal;

    public static bool UsesHover(string? mode)
        => Normalize(mode) is HoverReveal or ImageHoverReveal;
}

public readonly record struct AppWindowVisualState(
    int TargetOpacity,
    bool TargetPassThrough,
    bool MaskVisible);

public static class AppWindowAppearance
{
    public static AppWindowVisualState Resolve(string? mode, int opacity, bool hoverShown)
    {
        mode = AppWindowDisplayModes.Normalize(mode);
        opacity = Math.Clamp(opacity, 0, 100);

        return mode switch
        {
            AppWindowDisplayModes.HoverReveal => new(
                hoverShown ? opacity : 0,
                !hoverShown || opacity == 0,
                false),
            AppWindowDisplayModes.Image => new(100, false, true),
            AppWindowDisplayModes.ImageHoverReveal when hoverShown => new(opacity, false, false),
            AppWindowDisplayModes.ImageHoverReveal => new(100, false, true),
            _ => new(opacity, opacity == 0, false),
        };
    }
}

public static class WindowTaskSwitcherStyles
{
    public static int Apply(int currentExStyle, bool hidden, int originalExStyle)
    {
        if (hidden)
        {
            return (currentExStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        }

        const int ownedBits = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_APPWINDOW;
        return (currentExStyle & ~ownedBits) | (originalExStyle & ownedBits);
    }
}
