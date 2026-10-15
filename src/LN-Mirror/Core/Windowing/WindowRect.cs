namespace LNMirror.Core.Windowing;

/// <summary>屏幕坐标矩形（虚拟桌面像素），用于跨模块传递窗口/工作区位置。</summary>
public readonly record struct WindowRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}