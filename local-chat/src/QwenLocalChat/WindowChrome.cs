using System.Runtime.InteropServices;

namespace QwenLocalChat;

internal static class WindowChrome
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeLegacy = 19;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const uint WmSetRedraw = 0x000B;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;

    public static void ApplyDarkMode(nint handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int));

        var caption = ColorRef(8, 8, 9);
        var text = ColorRef(241, 241, 238);
        DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
    }

    public static void SetRedraw(nint handle, bool enabled)
    {
        if (handle == IntPtr.Zero) return;
        _ = SendMessage(handle, WmSetRedraw, enabled ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
    }

    public static void SetRedrawTree(Control root, bool enabled)
    {
        if (root is null) return;
        if (enabled)
        {
            foreach (Control child in root.Controls) SetRedrawTree(child, true);
            if (root.IsHandleCreated) SetRedraw(root.Handle, true);
            return;
        }

        if (root.IsHandleCreated) SetRedraw(root.Handle, false);
        foreach (Control child in root.Controls) SetRedrawTree(child, false);
    }

    public static void RedrawTree(nint handle)
    {
        if (handle == IntPtr.Zero) return;
        _ = RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);
    }

    private static int ColorRef(byte red, byte green, byte blue)
        => red | (green << 8) | (blue << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint windowHandle, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(nint windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(nint windowHandle, IntPtr updateRectangle, IntPtr updateRegion, uint flags);
}
