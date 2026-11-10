// Copyright (c) Bili Copilot. All rights reserved.

using System.Runtime.InteropServices;
using System.Text;

namespace BiliCopilot.UI.Services;

/// <summary>
/// 调整 mpv 画面区域：mpv 是播放器窗口的子窗口，直播面板展开时把它的宽度让出来，
/// 这样画面会整体缩到左边而不是被面板遮住。
/// </summary>
internal static class MpvSurfaceService
{
    /// <summary>
    /// 直播面板展开时占掉的宽度（面板 360 + 收起条与间距）.
    /// </summary>
    public const int LivePanelWidth = 378;

    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoMove = 0x0002;

    /// <summary>
    /// 按「右侧 + 底部」让位重新摆放 mpv 画面：窄窗时面板改成压在下面，画面缩到上半部分.
    /// </summary>
    /// <param name="playerWindow">播放器窗口句柄.</param>
    /// <param name="rightInsetDips">右侧让位（DIP）.</param>
    /// <param name="bottomInsetDips">底部让位（DIP）.</param>
    /// <param name="rasterizationScale">DPI 缩放.</param>
    public static void ApplyInset(IntPtr playerWindow, double rightInsetDips, double bottomInsetDips, double rasterizationScale = 1d)
    {
        if (playerWindow == IntPtr.Zero)
        {
            return;
        }

        var mpv = FindChildByClass(playerWindow, "mpv");
        if (mpv == IntPtr.Zero || !GetClientRect(playerWindow, out var rect))
        {
            return;
        }

        var scale = rasterizationScale <= 0 ? 1d : rasterizationScale;
        var rightPx = (int)Math.Round(Math.Max(0, rightInsetDips) * scale);
        var bottomPx = (int)Math.Round(Math.Max(0, bottomInsetDips) * scale);
        var width = Math.Max(1, rect.Right - rect.Left - rightPx);
        var height = Math.Max(1, rect.Bottom - rect.Top - bottomPx);
        _ = SetWindowPos(mpv, IntPtr.Zero, 0, 0, width, height, SwpNoZOrder | SwpNoActivate | SwpNoMove);
    }

    /// <summary>
    /// 按右侧让出的宽度重新摆放 mpv 画面.
    /// </summary>
    /// <param name="playerWindow">播放器窗口句柄.</param>
    /// <param name="rightInsetDips">右侧需要让出的 DIP，0 表示铺满.</param>
    /// <param name="rasterizationScale">DPI 缩放（1.0 = 100%）.</param>
    public static void ApplyRightInset(IntPtr playerWindow, double rightInsetDips, double rasterizationScale = 1d)
        => ApplyInset(playerWindow, rightInsetDips, 0, rasterizationScale);

    private static IntPtr FindChildByClass(IntPtr parent, string className)
    {
        var result = IntPtr.Zero;
        _ = EnumChildWindows(
            parent,
            (handle, _) =>
            {
                var buffer = new StringBuilder(64);
                _ = GetClassName(handle, buffer, buffer.Capacity);
                if (string.Equals(buffer.ToString(), className, StringComparison.OrdinalIgnoreCase))
                {
                    result = handle;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, int flags);
}
